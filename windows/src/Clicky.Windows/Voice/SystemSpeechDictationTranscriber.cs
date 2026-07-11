using System.Runtime.InteropServices;
using System.Speech.Recognition;

namespace Clicky.Windows.Voice;

public sealed class SystemSpeechDictationTranscriber : IDictationTranscriber
{
    private readonly object stateLock = new();
    private readonly List<string> recognizedPhrases = [];

    private SpeechRecognitionEngine? speechRecognizer;
    private TaskCompletionSource<RecognitionCompletion>? activeRecognitionCompletion;
    private bool isDisposed;

    public bool IsTranscribing
    {
        get
        {
            lock (stateLock)
            {
                return activeRecognitionCompletion is not null;
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (stateLock)
        {
            ThrowIfDisposed();
            if (activeRecognitionCompletion is not null)
            {
                throw new InvalidOperationException("A dictation session is already active.");
            }

            var recognizer = GetOrCreateSpeechRecognizer();
            recognizedPhrases.Clear();
            activeRecognitionCompletion = new TaskCompletionSource<RecognitionCompletion>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                recognizer.RecognizeAsync(RecognizeMode.Multiple);
            }
            catch (Exception exception) when (IsSetupException(exception))
            {
                activeRecognitionCompletion = null;
                throw new DictationUnavailableException(
                    DictationUnavailableReason.Microphone,
                    "Windows Speech Recognition could not start microphone capture.",
                    exception);
            }
        }

        return Task.CompletedTask;
    }

    public async Task<string> StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task<RecognitionCompletion> completionTask;
        lock (stateLock)
        {
            ThrowIfDisposed();
            if (activeRecognitionCompletion is null || speechRecognizer is null)
            {
                throw new InvalidOperationException("No dictation session is active.");
            }

            completionTask = activeRecognitionCompletion.Task;
            speechRecognizer.RecognizeAsyncStop();
        }

        try
        {
            var completion = await completionTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (completion.Error is not null)
            {
                throw new InvalidOperationException(
                    "Windows Speech Recognition could not finalize dictation.",
                    completion.Error);
            }

            return completion.Transcript;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RequestRecognitionCancellation();
            throw;
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task<RecognitionCompletion>? completionTask;
        lock (stateLock)
        {
            ThrowIfDisposed();
            completionTask = activeRecognitionCompletion?.Task;
            if (completionTask is null || speechRecognizer is null)
            {
                return;
            }

            speechRecognizer.RecognizeAsyncCancel();
        }

        await completionTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        SpeechRecognitionEngine? recognizerToDispose;
        TaskCompletionSource<RecognitionCompletion>? completionToCancel;

        lock (stateLock)
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;
            recognizerToDispose = speechRecognizer;
            completionToCancel = activeRecognitionCompletion;
            speechRecognizer = null;
            activeRecognitionCompletion = null;
            recognizedPhrases.Clear();

            if (recognizerToDispose is not null)
            {
                recognizerToDispose.SpeechRecognized -= HandleSpeechRecognized;
                recognizerToDispose.RecognizeCompleted -= HandleRecognitionCompleted;
                try
                {
                    recognizerToDispose.RecognizeAsyncCancel();
                }
                catch (InvalidOperationException)
                {
                    // No recognition operation remains to cancel.
                }
            }
        }

        completionToCancel?.TrySetCanceled();
        recognizerToDispose?.Dispose();
        GC.SuppressFinalize(this);
    }

    private SpeechRecognitionEngine GetOrCreateSpeechRecognizer()
    {
        if (speechRecognizer is not null)
        {
            return speechRecognizer;
        }

        SpeechRecognitionEngine recognizer;
        try
        {
            if (SpeechRecognitionEngine.InstalledRecognizers().Count == 0)
            {
                throw new DictationUnavailableException(
                    DictationUnavailableReason.SpeechRecognizer,
                    "No Windows Speech Recognition language is installed.");
            }

            recognizer = new SpeechRecognitionEngine();
            recognizer.LoadGrammar(new DictationGrammar());
        }
        catch (DictationUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (IsSetupException(exception))
        {
            throw new DictationUnavailableException(
                DictationUnavailableReason.SpeechRecognizer,
                "Windows Speech Recognition is not available for dictation.",
                exception);
        }

        try
        {
            recognizer.SetInputToDefaultAudioDevice();
        }
        catch (Exception exception) when (IsSetupException(exception))
        {
            recognizer.Dispose();
            throw new DictationUnavailableException(
                DictationUnavailableReason.Microphone,
                "No usable default microphone is available for dictation.",
                exception);
        }

        recognizer.SpeechRecognized += HandleSpeechRecognized;
        recognizer.RecognizeCompleted += HandleRecognitionCompleted;
        speechRecognizer = recognizer;
        return recognizer;
    }

    private void HandleSpeechRecognized(object? sender, SpeechRecognizedEventArgs eventArgs)
    {
        var recognizedText = eventArgs.Result.Text.Trim();
        if (recognizedText.Length == 0)
        {
            return;
        }

        lock (stateLock)
        {
            if (!isDisposed && activeRecognitionCompletion is not null)
            {
                recognizedPhrases.Add(recognizedText);
            }
        }
    }

    private void HandleRecognitionCompleted(object? sender, RecognizeCompletedEventArgs eventArgs)
    {
        TaskCompletionSource<RecognitionCompletion>? completion;
        RecognitionCompletion result;

        lock (stateLock)
        {
            if (isDisposed || sender != speechRecognizer || activeRecognitionCompletion is null)
            {
                return;
            }

            completion = activeRecognitionCompletion;
            activeRecognitionCompletion = null;
            result = new RecognitionCompletion(
                string.Join(' ', recognizedPhrases),
                eventArgs.Error);
            recognizedPhrases.Clear();
        }

        completion.TrySetResult(result);
    }

    private void RequestRecognitionCancellation()
    {
        lock (stateLock)
        {
            if (isDisposed || activeRecognitionCompletion is null || speechRecognizer is null)
            {
                return;
            }

            try
            {
                speechRecognizer.RecognizeAsyncCancel();
            }
            catch (InvalidOperationException)
            {
                // Recognition completed while cancellation was being requested.
            }
        }
    }

    private static bool IsSetupException(Exception exception) =>
        exception is InvalidOperationException
            or ArgumentException
            or PlatformNotSupportedException
            or COMException;

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
    }

    private sealed record RecognitionCompletion(string Transcript, Exception? Error);
}
