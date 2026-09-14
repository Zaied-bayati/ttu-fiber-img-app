namespace Ttu.FiberImgApp.Core.Sampling;

public static class SamplingCalculator
{
    public static IReadOnlyList<double> ComputePositions(double minMm, double maxMm, int imageCount)
    {
        if (imageCount < 2)
            throw new ArgumentOutOfRangeException(nameof(imageCount), "At least 2 images are required.");
        if (maxMm < minMm)
            throw new ArgumentException("Max travel must be greater than or equal to min travel.");

        var positions = new double[imageCount];
        if (Math.Abs(maxMm - minMm) < 1e-9)
        {
            for (var i = 0; i < imageCount; i++) positions[i] = minMm;
            return positions;
        }

        var step = (maxMm - minMm) / (imageCount - 1);
        for (var i = 0; i < imageCount; i++)
            positions[i] = Math.Round(minMm + (i * step), 4);
        return positions;
    }
}