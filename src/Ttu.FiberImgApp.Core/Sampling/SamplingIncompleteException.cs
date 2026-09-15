using Ttu.FiberImgApp.Core.Models;

namespace Ttu.FiberImgApp.Core.Sampling;

/// <summary>
/// Thrown when sampling stops early but one or more images were already captured.
/// Callers should present the Session for review/save instead of discarding it.
/// </summary>
public sealed class SamplingIncompleteException : Exception
{
    public SamplingIncompleteException(SamplingSession session, Exception innerException)
        : base(BuildMessage(session, innerException), innerException)
    {
        Session = session;
    }

    public SamplingSession Session { get; }

    private static string BuildMessage(SamplingSession session, Exception innerException)
    {
        var captured = session.Images.Count;
        var requested = session.RequestedCount;
        if (innerException is OperationCanceledException)
        {
            return $"Sampling cancelled after {captured} of {requested} images.";
        }

        return $"Sampling stopped after {captured} of {requested} images: {innerException.Message}";
    }
}