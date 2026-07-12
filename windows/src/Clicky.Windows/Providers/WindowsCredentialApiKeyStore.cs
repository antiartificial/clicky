using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Clicky.Windows.Providers;

internal interface IWindowsCredentialOperations
{
    string? Read(AiProviderKind provider, string targetName);

    bool Exists(AiProviderKind provider, string targetName);

    void Write(AiProviderKind provider, string targetName, string apiKey);

    void Delete(AiProviderKind provider, string targetName);
}

public sealed class WindowsCredentialApiKeyStore : IProviderApiKeyStore
{
    public const int MaximumApiKeyByteLength = 2560;

    private const uint GenericCredentialType = 1;
    private const uint PersistForCurrentUserOnLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const string CredentialTargetPrefix = "Clicky/ProviderApiKey/v1";
    private const string LegacyCredentialTargetPrefix = "Knobnote/ProviderApiKey/v1";
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly IWindowsCredentialOperations credentialOperations;

    public WindowsCredentialApiKeyStore() : this(
        new NativeWindowsCredentialOperations(),
        requireWindows: true)
    {
    }

    internal WindowsCredentialApiKeyStore(IWindowsCredentialOperations credentialOperations) : this(
        credentialOperations,
        requireWindows: false)
    {
    }

    private WindowsCredentialApiKeyStore(
        IWindowsCredentialOperations credentialOperations,
        bool requireWindows)
    {
        ArgumentNullException.ThrowIfNull(credentialOperations);

        if (requireWindows && !OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Windows Credential Manager API key storage is available only on Windows.");
        }

        this.credentialOperations = credentialOperations;
    }

    public Task<string?> GetApiKeyAsync(
        AiProviderKind provider,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var targetName = GetCredentialTargetName(provider);
        var apiKey = credentialOperations.Read(provider, targetName);
        if (apiKey is not null)
        {
            return Task.FromResult<string?>(apiKey);
        }

        var legacyTargetName = GetLegacyCredentialTargetName(provider);
        apiKey = credentialOperations.Read(provider, legacyTargetName);
        TryMigrateLegacyCredential(provider, legacyTargetName, targetName, apiKey);
        return Task.FromResult<string?>(apiKey);
    }

    public Task SaveApiKeyAsync(
        AiProviderKind provider,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateApiKey(provider, apiKey);

        var targetName = GetCredentialTargetName(provider);
        credentialOperations.Write(provider, targetName, apiKey);
        TryDeleteMigratedLegacyCredential(provider, GetLegacyCredentialTargetName(provider));
        return Task.CompletedTask;
    }

    public Task DeleteApiKeyAsync(
        AiProviderKind provider,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Exception? firstFailure = null;
        foreach (var targetName in new[]
                 {
                     GetCredentialTargetName(provider),
                     GetLegacyCredentialTargetName(provider),
                 })
        {
            try
            {
                credentialOperations.Delete(provider, targetName);
            }
            catch (Win32Exception exception)
            {
                firstFailure ??= exception;
            }
        }

        if (firstFailure is not null)
        {
            throw firstFailure;
        }

        return Task.CompletedTask;
    }

    public Task<bool> HasApiKeyAsync(
        AiProviderKind provider,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var targetName = GetCredentialTargetName(provider);
        if (credentialOperations.Exists(provider, targetName))
        {
            return Task.FromResult(true);
        }

        var legacyTargetName = GetLegacyCredentialTargetName(provider);
        var apiKey = credentialOperations.Read(provider, legacyTargetName);
        TryMigrateLegacyCredential(provider, legacyTargetName, targetName, apiKey);
        return Task.FromResult(apiKey is not null);
    }

    public static string GetCredentialTargetName(AiProviderKind provider) =>
        provider switch
        {
            AiProviderKind.Anthropic => $"{CredentialTargetPrefix}/anthropic",
            AiProviderKind.OpenAI => $"{CredentialTargetPrefix}/openai",
            AiProviderKind.Gemini => $"{CredentialTargetPrefix}/gemini",
            AiProviderKind.ElevenLabs => $"{CredentialTargetPrefix}/elevenlabs",
            AiProviderKind.Worker => throw new ArgumentException(
                "The Worker provider does not accept a locally stored API key.",
                nameof(provider)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "The AI provider is not supported."),
        };

    internal static string GetLegacyCredentialTargetName(AiProviderKind provider) =>
        provider switch
        {
            AiProviderKind.Anthropic => $"{LegacyCredentialTargetPrefix}/anthropic",
            AiProviderKind.OpenAI => $"{LegacyCredentialTargetPrefix}/openai",
            AiProviderKind.Gemini => $"{LegacyCredentialTargetPrefix}/gemini",
            AiProviderKind.ElevenLabs => $"{LegacyCredentialTargetPrefix}/elevenlabs",
            AiProviderKind.Worker => throw new ArgumentException(
                "The Worker provider does not accept a locally stored API key.",
                nameof(provider)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "The AI provider is not supported."),
        };

    public static void ValidateApiKey(AiProviderKind provider, string apiKey)
    {
        _ = GetCredentialTargetName(provider);
        ArgumentNullException.ThrowIfNull(apiKey);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("The API key cannot be blank.", nameof(apiKey));
        }

        if (StrictUtf8.GetByteCount(apiKey) > MaximumApiKeyByteLength)
        {
            throw new ArgumentException(
                $"The API key cannot exceed {MaximumApiKeyByteLength} UTF-8 bytes.",
                nameof(apiKey));
        }
    }

    private static Win32Exception CreateCredentialManagerException(
        string operation,
        AiProviderKind provider,
        int errorCode) =>
        new(
            errorCode,
            $"Windows Credential Manager could not {operation} the {provider} API key.");

    private void TryMigrateLegacyCredential(
        AiProviderKind provider,
        string legacyTargetName,
        string targetName,
        string? apiKey)
    {
        if (apiKey is null)
        {
            return;
        }

        try
        {
            credentialOperations.Write(provider, targetName, apiKey);
        }
        catch (Win32Exception)
        {
            return;
        }

        TryDeleteMigratedLegacyCredential(provider, legacyTargetName);
    }

    private void TryDeleteMigratedLegacyCredential(
        AiProviderKind provider,
        string legacyTargetName)
    {
        try
        {
            credentialOperations.Delete(provider, legacyTargetName);
        }
        catch (Win32Exception)
        {
            // The current credential is already safe to use; stale legacy cleanup is best effort.
        }
    }

    private sealed class NativeWindowsCredentialOperations : IWindowsCredentialOperations
    {
        public string? Read(AiProviderKind provider, string targetName)
        {
            if (!CredReadW(targetName, GenericCredentialType, flags: 0, out var credentialPointer))
            {
                var errorCode = Marshal.GetLastWin32Error();
                if (errorCode == ErrorNotFound)
                {
                    return null;
                }

                throw CreateCredentialManagerException("read", provider, errorCode);
            }

            try
            {
                var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
                var credentialBlobSize = checked((int)credential.CredentialBlobSize);
                if (credentialBlobSize > MaximumApiKeyByteLength)
                {
                    throw new InvalidDataException(
                        $"The stored {provider} API key exceeds the supported size.");
                }

                if (credentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
                {
                    return null;
                }

                var apiKeyBytes = new byte[credentialBlobSize];
                try
                {
                    Marshal.Copy(credential.CredentialBlob, apiKeyBytes, startIndex: 0, credentialBlobSize);
                    return StrictUtf8.GetString(apiKeyBytes);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(apiKeyBytes);
                }
            }
            finally
            {
                ZeroAndFreeCredential(credentialPointer);
            }
        }

        public bool Exists(AiProviderKind provider, string targetName)
        {
            if (!CredReadW(targetName, GenericCredentialType, flags: 0, out var credentialPointer))
            {
                var errorCode = Marshal.GetLastWin32Error();
                if (errorCode == ErrorNotFound)
                {
                    return false;
                }

                throw CreateCredentialManagerException("inspect", provider, errorCode);
            }

            try
            {
                var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
                return credential.CredentialBlobSize > 0 && credential.CredentialBlob != IntPtr.Zero;
            }
            finally
            {
                ZeroAndFreeCredential(credentialPointer);
            }
        }

        public void Write(AiProviderKind provider, string targetName, string apiKey)
        {
            var apiKeyBytes = StrictUtf8.GetBytes(apiKey);
            var credentialBlobPointer = IntPtr.Zero;

            try
            {
                credentialBlobPointer = Marshal.AllocHGlobal(apiKeyBytes.Length);
                Marshal.Copy(apiKeyBytes, startIndex: 0, credentialBlobPointer, apiKeyBytes.Length);

                var credential = new NativeCredential
                {
                    Type = GenericCredentialType,
                    TargetName = targetName,
                    CredentialBlobSize = checked((uint)apiKeyBytes.Length),
                    CredentialBlob = credentialBlobPointer,
                    Persist = PersistForCurrentUserOnLocalMachine,
                    UserName = Environment.UserName,
                };

                if (!CredWriteW(ref credential, flags: 0))
                {
                    throw CreateCredentialManagerException(
                        "save",
                        provider,
                        Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(apiKeyBytes);
                ZeroAndFreeUnmanagedBuffer(credentialBlobPointer, apiKeyBytes.Length);
            }
        }

        public void Delete(AiProviderKind provider, string targetName)
        {
            if (!CredDeleteW(targetName, GenericCredentialType, flags: 0))
            {
                var errorCode = Marshal.GetLastWin32Error();
                if (errorCode != ErrorNotFound)
                {
                    throw CreateCredentialManagerException("delete", provider, errorCode);
                }
            }
        }
    }

    private static void ZeroAndFreeCredential(IntPtr credentialPointer)
    {
        if (credentialPointer == IntPtr.Zero)
        {
            return;
        }

        var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
        var credentialBlobSize = credential.CredentialBlobSize > int.MaxValue
            ? 0
            : (int)credential.CredentialBlobSize;
        ZeroUnmanagedBuffer(credential.CredentialBlob, credentialBlobSize);
        CredFree(credentialPointer);
    }

    private static void ZeroAndFreeUnmanagedBuffer(IntPtr buffer, int byteCount)
    {
        if (buffer == IntPtr.Zero)
        {
            return;
        }

        ZeroUnmanagedBuffer(buffer, byteCount);
        Marshal.FreeHGlobal(buffer);
    }

    private static void ZeroUnmanagedBuffer(IntPtr buffer, int byteCount)
    {
        if (buffer == IntPtr.Zero || byteCount <= 0)
        {
            return;
        }

        var zeroBytes = new byte[Math.Min(byteCount, 256)];
        for (var offset = 0; offset < byteCount; offset += zeroBytes.Length)
        {
            var bytesToClear = Math.Min(zeroBytes.Length, byteCount - offset);
            Marshal.Copy(zeroBytes, startIndex: 0, IntPtr.Add(buffer, offset), bytesToClear);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Comment;

        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetAlias;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? UserName;
    }

    [DllImport(
        "Advapi32.dll",
        EntryPoint = "CredReadW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(
        string targetName,
        uint credentialType,
        uint flags,
        out IntPtr credentialPointer);

    [DllImport(
        "Advapi32.dll",
        EntryPoint = "CredWriteW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref NativeCredential credential, uint flags);

    [DllImport(
        "Advapi32.dll",
        EntryPoint = "CredDeleteW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string targetName, uint credentialType, uint flags);

    [DllImport("Advapi32.dll", ExactSpelling = true)]
    private static extern void CredFree(IntPtr credentialPointer);
}
