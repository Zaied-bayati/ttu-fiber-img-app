using Ttu.FiberImgApp.Core.Imaging;
using Ttu.FiberImgApp.Core.Models;

namespace Ttu.FiberImgApp.Infrastructure.Tests;

public class MonoFrameEncoderTests
{
    [Fact]
    public void Preview_downsamples_long_edge_and_keeps_sampled_gray()
    {
        var pixels = new byte[8 * 4];
        pixels[0] = 10;
        pixels[2] = 20;
        pixels[(2 * 8) + 0] = 30;
        pixels[(2 * 8) + 2] = 40;

        var preview = MonoFrameEncoder.ToPreviewBgra32(new CameraFrame
        {
            RawPixels = pixels,
            Width = 8,
            Height = 4,
            PixelFormat = CameraPixelFormat.Gray8,
        }, maxEdge: 4);

        Assert.Equal(4, preview.Width);
        Assert.Equal(2, preview.Height);
        Assert.Equal(10, preview.Bgra[0]);
        Assert.Equal(20, preview.Bgra[4]);
        Assert.Equal(30, preview.Bgra[(1 * 4 * 4) + 0]);
        Assert.Equal(40, preview.Bgra[(1 * 4 * 4) + 4]);
        Assert.Equal(255, preview.Bgra[3]);
    }

    [Fact]
    public void Preview_stretches_gray16_samples()
    {
        var pixels = new byte[2 * 2];
        pixels[0] = 0;
        pixels[1] = 0;
        pixels[2] = 0xE8;
        pixels[3] = 0x03; // 1000

        var preview = MonoFrameEncoder.ToPreviewBgra32(new CameraFrame
        {
            RawPixels = pixels,
            Width = 2,
            Height = 1,
            PixelFormat = CameraPixelFormat.Gray16,
        }, maxEdge: 1600);

        Assert.Equal(2, preview.Width);
        Assert.Equal(1, preview.Height);
        Assert.Equal(0, preview.Bgra[0]);
        Assert.Equal(255, preview.Bgra[4]);
    }
}
