namespace Ttu.FiberImgApp.Core.Abstractions;

public interface IThorlabsClient
{
    string Status { get; }
    bool IsConnected { get; }
    bool IsMoving { get; }
    double PositionMm { get; }

    Task<bool> ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task HomeAsync(CancellationToken cancellationToken = default);
    Task MoveAbsoluteAsync(double positionMm, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task ApplyMotionParametersAsync(double velocityMmPerSec, double accelerationMmPerSec2, CancellationToken cancellationToken = default);
    /// <summary>Refresh cached <see cref="PositionMm"/> from hardware (no-op when disconnected).</summary>
    Task RefreshPositionAsync(CancellationToken cancellationToken = default);
    IReadOnlyList<string> ListDevices();
}
