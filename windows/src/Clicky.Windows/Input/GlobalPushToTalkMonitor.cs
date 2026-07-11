using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Clicky.Windows.Input;

public sealed class GlobalPushToTalkMonitor : IGlobalPushToTalkMonitor
{
    private readonly object transitionTrackerLock = new();
    private readonly PushToTalkTransitionTracker transitionTracker;
    private readonly Channel<PushToTalkTransition> transitionChannel;
    private readonly Task transitionDispatchTask;
    private readonly Thread hookThread;
    private readonly NativeMethods.LowLevelKeyboardProcedure hookCallback;
    private readonly TaskCompletionSource hookStartupCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    private nint hookHandle;
    private uint hookThreadId;
    private int transitionDispatchThreadId;
    private int isDisposed;

    public GlobalPushToTalkMonitor(PushToTalkKey selectedKey = PushToTalkKey.F13)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The global push-to-talk monitor is available only on Windows.");
        }

        transitionTracker = new PushToTalkTransitionTracker(selectedKey);
        transitionChannel = Channel.CreateUnbounded<PushToTalkTransition>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
        hookCallback = HandleLowLevelKeyboardEvent;
        transitionDispatchTask = Task.Run(DispatchTransitionsAsync);
        hookThread = new Thread(RunHookMessageLoop)
        {
            IsBackground = true,
            Name = "Clicky push-to-talk keyboard hook",
        };

        hookThread.Start();
        try
        {
            hookStartupCompletion.Task.GetAwaiter().GetResult();
        }
        catch
        {
            Interlocked.Exchange(ref isDisposed, 1);
            transitionChannel.Writer.TryComplete();
            transitionDispatchTask.GetAwaiter().GetResult();
            throw;
        }
    }

    public PushToTalkKey SelectedKey
    {
        get
        {
            lock (transitionTrackerLock)
            {
                return transitionTracker.SelectedKey;
            }
        }
        set
        {
            PushToTalkKeyValidator.Validate(value, nameof(value));
            ThrowIfDisposed();

            lock (transitionTrackerLock)
            {
                ThrowIfDisposed();
                EnqueueTransition(transitionTracker.ChangeSelectedKey(value));
            }
        }
    }

    public event EventHandler<PushToTalkTransitionEventArgs>? Transitioned;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref isDisposed, 1) != 0)
        {
            return;
        }

        var threadId = Volatile.Read(ref hookThreadId);
        if (threadId != 0)
        {
            NativeMethods.PostThreadMessage(
                threadId,
                NativeMethods.WindowMessageQuit,
                nint.Zero,
                nint.Zero);
            hookThread.Join();
        }

        lock (transitionTrackerLock)
        {
            EnqueueTransition(transitionTracker.ReleaseIfPressed());
        }

        transitionChannel.Writer.TryComplete();
        if (Environment.CurrentManagedThreadId != Volatile.Read(ref transitionDispatchThreadId))
        {
            transitionDispatchTask.GetAwaiter().GetResult();
        }

        GC.SuppressFinalize(this);
    }

    private nint HandleLowLevelKeyboardEvent(int hookCode, nint windowMessage, nint hookDataPointer)
    {
        try
        {
            if (hookCode >= 0 && Volatile.Read(ref isDisposed) == 0)
            {
                var message = unchecked((int)windowMessage);
                var isKeyDown = message is NativeMethods.WindowMessageKeyDown
                    or NativeMethods.WindowMessageSystemKeyDown;
                var isKeyUp = message is NativeMethods.WindowMessageKeyUp
                    or NativeMethods.WindowMessageSystemKeyUp;

                if (isKeyDown || isKeyUp)
                {
                    var hookData = Marshal.PtrToStructure<NativeMethods.LowLevelKeyboardData>(
                        hookDataPointer);
                    lock (transitionTrackerLock)
                    {
                        EnqueueTransition(
                            transitionTracker.ProcessKeyEvent(
                                checked((int)hookData.VirtualKeyCode),
                                isKeyDown));
                    }
                }
            }
        }
        catch
        {
            // Exceptions must never escape a native hook callback.
        }

        return NativeMethods.CallNextHookEx(
            nint.Zero,
            hookCode,
            windowMessage,
            hookDataPointer);
    }

    private void RunHookMessageLoop()
    {
        try
        {
            hookThreadId = NativeMethods.GetCurrentThreadId();
            NativeMethods.PeekMessage(
                out _,
                nint.Zero,
                minimumMessage: 0,
                maximumMessage: 0,
                removeMessage: 0);

            hookHandle = NativeMethods.SetWindowsHookEx(
                NativeMethods.LowLevelKeyboardHook,
                hookCallback,
                NativeMethods.GetModuleHandle(null),
                threadId: 0);
            if (hookHandle == nint.Zero)
            {
                hookStartupCompletion.TrySetException(
                    new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Unable to install the global push-to-talk keyboard hook."));
                return;
            }

            hookStartupCompletion.TrySetResult();
            while (NativeMethods.GetMessage(
                       out var message,
                       nint.Zero,
                       minimumMessage: 0,
                       maximumMessage: 0) > 0)
            {
                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessage(ref message);
            }
        }
        catch (Exception exception)
        {
            hookStartupCompletion.TrySetException(
                new InvalidOperationException(
                    "The global push-to-talk keyboard hook could not start.",
                    exception));
        }
        finally
        {
            if (hookHandle != nint.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(hookHandle);
            }

            hookHandle = nint.Zero;
            hookThreadId = 0;
        }
    }

    private async Task DispatchTransitionsAsync()
    {
        await foreach (var transition in transitionChannel.Reader.ReadAllAsync())
        {
            var eventArgs = new PushToTalkTransitionEventArgs(transition);
            var eventHandlers = Transitioned?.GetInvocationList();
            if (eventHandlers is null)
            {
                continue;
            }

            foreach (EventHandler<PushToTalkTransitionEventArgs> eventHandler in eventHandlers)
            {
                Volatile.Write(ref transitionDispatchThreadId, Environment.CurrentManagedThreadId);
                try
                {
                    eventHandler(this, eventArgs);
                }
                catch
                {
                    // A subscriber cannot be allowed to stop later pedal transitions.
                }
                finally
                {
                    Volatile.Write(ref transitionDispatchThreadId, 0);
                }
            }
        }
    }

    private void EnqueueTransition(PushToTalkTransition? transition)
    {
        if (transition is not null)
        {
            transitionChannel.Writer.TryWrite(transition.Value);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref isDisposed) != 0, this);
    }

    private static class NativeMethods
    {
        internal const int LowLevelKeyboardHook = 13;
        internal const int WindowMessageKeyDown = 0x0100;
        internal const int WindowMessageKeyUp = 0x0101;
        internal const int WindowMessageSystemKeyDown = 0x0104;
        internal const int WindowMessageSystemKeyUp = 0x0105;
        internal const uint WindowMessageQuit = 0x0012;

        internal delegate nint LowLevelKeyboardProcedure(
            int hookCode,
            nint windowMessage,
            nint hookDataPointer);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint SetWindowsHookEx(
            int hookIdentifier,
            LowLevelKeyboardProcedure hookProcedure,
            nint moduleHandle,
            uint threadId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnhookWindowsHookEx(nint hookHandle);

        [DllImport("user32.dll")]
        internal static extern nint CallNextHookEx(
            nint hookHandle,
            int hookCode,
            nint windowMessage,
            nint hookDataPointer);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostThreadMessage(
            uint threadId,
            uint message,
            nint windowParameter,
            nint longParameter);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int GetMessage(
            out Message message,
            nint windowHandle,
            uint minimumMessage,
            uint maximumMessage);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PeekMessage(
            out Message message,
            nint windowHandle,
            uint minimumMessage,
            uint maximumMessage,
            uint removeMessage);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TranslateMessage(ref Message message);

        [DllImport("user32.dll")]
        internal static extern nint DispatchMessage(ref Message message);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint GetModuleHandle(string? moduleName);

        [StructLayout(LayoutKind.Sequential)]
        internal struct LowLevelKeyboardData
        {
            internal uint VirtualKeyCode;
            internal uint ScanCode;
            internal uint Flags;
            internal uint Time;
            internal nuint ExtraInformation;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Message
        {
            internal nint WindowHandle;
            internal uint Value;
            internal nuint WindowParameter;
            internal nint LongParameter;
            internal uint Time;
            internal Point CursorPosition;
            internal uint Private;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point
        {
            internal int X;
            internal int Y;
        }
    }
}
