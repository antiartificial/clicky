namespace Clicky.Windows.Capture;

public interface IActiveWindowCaptureService
{
    Task<CaptureResult> CaptureAsync(CancellationToken cancellationToken = default);
}
