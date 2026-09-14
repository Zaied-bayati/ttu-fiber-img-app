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
    Task<byte[]> GetPreviewFrameAsync(CancellationToken cancellationToken = default);
}