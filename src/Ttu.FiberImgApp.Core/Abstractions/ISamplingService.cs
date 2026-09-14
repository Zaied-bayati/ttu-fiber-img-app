using Ttu.FiberImgApp.Core.Models;

namespace Ttu.FiberImgApp.Core.Abstractions;

public interface ISamplingService
{
    Task<SamplingSession> RunAsync(
        int imageCount,
        double minMm,
        double maxMm,
        IProgress<SamplingProgress>? progress = null,
        CancellationToken cancellationToken = default);
}