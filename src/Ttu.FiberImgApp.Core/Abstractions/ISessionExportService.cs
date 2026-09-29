using Ttu.FiberImgApp.Core.Models;

namespace Ttu.FiberImgApp.Core.Abstractions;

public interface ISessionExportService
{
    /// <summary>
    /// Saves the session under a folder derived from <paramref name="customName"/> when it sanitizes to a
    /// non-empty value, otherwise falls back to the default timestamp-based name. Collisions are resolved
    /// with a numeric suffix rather than throwing.
    /// </summary>
    Task<string> SaveSessionAsync(SamplingSession session, string? customName = null, CancellationToken cancellationToken = default);
}