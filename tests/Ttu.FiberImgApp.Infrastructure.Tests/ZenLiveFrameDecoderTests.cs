using Google.Protobuf;
using Ttu.FiberImgApp.Core.Models;
using Ttu.FiberImgApp.Integrations.Zen;
using ZenApi.Acquisition.V1Beta;
using ZenApi.Common.V1;

namespace Ttu.FiberImgApp.Infrastructure.Tests;

public class ZenLiveFrameDecoderTests
{
    [Fact]
    public void Complete_frame_uses_frame_size_when_pixel_size_is_missing()
    {
        var raw = new byte[] { 0, 0, 0xE8, 0x03, 0x01, 0x00, 0xFF, 0xFF };
        var decoder = new ZenLiveFrameDecoder();

        var frame = decoder.Add(Frame(raw, PixelType.Gray16, frameW: 2, frameH: 2), enableRawData: false, out var skip);

        Assert.NotNull(frame);
        Assert.Equal(string.Empty, skip);
        Assert.Equal(2, frame.Width);
        Assert.Equal(2, frame.Height);
        Assert.Equal(CameraPixelFormat.Gray16, frame.PixelFormat);
        Assert.Equal(raw, frame.RawPixels);
    }

    [Fact]
    public void Short_buffer_is_not_treated_as_a_full_frame()
    {
        var decoder = new ZenLiveFrameDecoder();

        var frame = decoder.Add(
            Frame([1, 2, 3, 4], PixelType.Gray8, frameW: 4, frameH: 4),
            enableRawData: false,
            out var skip);

        Assert.Null(frame);
        Assert.Contains("expected", skip, StringComparison.Ordinal);
    }

    [Fact]
    public void Partial_scan_lines_stitch_into_the_full_frame()
    {
        var decoder = new ZenLiveFrameDecoder();
        var first = decoder.Add(
            Line([1, 2, 3, 4], y: 0),
            enableRawData: true,
            out _);
        var second = decoder.Add(
            Line([5, 6, 7, 8], y: 1),
            enableRawData: true,
            out var skip);

        Assert.NotNull(first);
        Assert.Equal(string.Empty, skip);
        Assert.NotNull(second);
        Assert.Equal(4, second.Width);
        Assert.Equal(2, second.Height);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, second.RawPixels);
    }

    private static FrameData Line(byte[] raw, int y) =>
        new()
        {
            FrameSize = new IntSize { Width = 4, Height = 2 },
            PixelData = new FramePixelData
            {
                PixelType = PixelType.Gray8,
                RawData = ByteString.CopyFrom(raw),
                Size = new IntSize { Width = 4, Height = 1 },
                StartPosition = new IntPoint { X = 0, Y = y },
            },
        };

    private static FrameData Frame(byte[] raw, PixelType pixelType, int frameW, int frameH) =>
        new()
        {
            FrameSize = new IntSize { Width = frameW, Height = frameH },
            PixelData = new FramePixelData
            {
                PixelType = pixelType,
                RawData = ByteString.CopyFrom(raw),
            },
        };
}
