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
}

public sealed class CameraFrameEventArgs : EventArgs
{
    public CameraFrameEventArgs(CameraFrame frame) => Frame = frame;
    public CameraFrame Frame { get; }
}