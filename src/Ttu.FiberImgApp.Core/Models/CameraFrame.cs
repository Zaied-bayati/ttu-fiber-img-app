namespace Ttu.FiberImgApp.Core.Models;

public enum CameraPixelFormat
{
    Gray8 = 1,
    Gray16 = 2,
    Bgr24 = 4,
    Bgr48 = 5,
}

public sealed class CameraFrame
{
    public required byte[] RawPixels { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required CameraPixelFormat PixelFormat { get; init; }
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Real sensor bit depth when known (e.g. 12 or 14 stored in a 16-bit container). Lets 8-bit exports use the
    /// full 0-255 range with the same scaling for every image instead of a per-image stretch.
    /// </summary>
    public int? SignificantBits { get; init; }
}

public sealed class CameraFrameEventArgs : EventArgs
{
    public CameraFrameEventArgs(CameraFrame frame) => Frame = frame;
    public CameraFrame Frame { get; }
}