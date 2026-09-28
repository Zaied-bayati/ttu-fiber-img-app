using Ttu.FiberImgApp.Core.Models;

namespace Ttu.FiberImgApp.Core.Abstractions;

public interface ICameraService
{
    string Status { get; }
    bool IsConnected { get; }
    bool IsLive { get; }
    bool IsPreviewAvailable { get; }

    event EventHandler<CameraFrameEventArgs>? FrameReceived;

    Task<bool> ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task StartLiveAsync(CancellationToken cancellationToken = default);
    Task StopLiveAsync(CancellationToken cancellationToken = default);
    Task<byte[]> CaptureStillAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Single acquisition. The default wraps <see cref="CaptureStillAsync"/> for cameras that only produce a preview.
    /// </summary>
    Task<CameraCapture> CaptureAcquisitionAsync(CancellationToken cancellationToken = default)
    {
        return WrapStillAsync(cancellationToken);

        async Task<CameraCapture> WrapStillAsync(CancellationToken token)
        {
            var png = await CaptureStillAsync(token).ConfigureAwait(false);
            return new CameraCapture { PreviewPng = png };
        }
    }

    Task<byte[]> GetPreviewFrameAsync(CancellationToken cancellationToken = default);
}