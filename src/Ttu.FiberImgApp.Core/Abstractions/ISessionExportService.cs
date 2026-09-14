using Ttu.FiberImgApp.Core.Models;

namespace Ttu.FiberImgApp.Core.Abstractions;

public interface ISessionExportService
{
    Task<string> SaveSessionAsync(SamplingSession session, CancellationToken cancellationToken = default);
}