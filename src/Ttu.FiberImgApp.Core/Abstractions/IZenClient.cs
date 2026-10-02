using Ttu.FiberImgApp.Core.Models;

namespace Ttu.FiberImgApp.Core.Abstractions;

public interface IZenClient
{
    string Status { get; }
    Task<bool> ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Listens to the ZEN pixel stream with no channel filter for <paramref name="listenFor"/> and reports
    /// which channels, sizes, and pixel types arrive. Answers "what is ZEN actually streaming?".
    /// </summary>
    Task<ZenStreamProbeResult> ProbeStreamAsync(TimeSpan listenFor, CancellationToken cancellationToken = default);
}
