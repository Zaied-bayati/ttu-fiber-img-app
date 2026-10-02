using System.Runtime.InteropServices.WindowsRuntime;
using Ttu.FiberImgApp.Core.Imaging;
using Ttu.FiberImgApp.Core.Models;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Ttu.FiberImgApp.Imaging;

/// <summary>CZI compression 4 (JPEG XR) for mono images, decoded with the JPEG XR codec built into Windows.</summary>
public sealed class JpegXrCziTileDecoder : ICziTileDecoder
{
    public bool CanDecode(int compression) => compression == 4;

    public bool TryDecode(
        int compression,
        byte[] data,
        int width,
        int height,
        CameraPixelFormat format,
        out byte[] pixels,
        out string error)
    {
        pixels = [];
        error = string.Empty;

        BitmapPixelFormat target;
        switch (format)
        {
            case CameraPixelFormat.Gray8:
                target = BitmapPixelFormat.Gray8;
                break;
            case CameraPixelFormat.Gray16:
                target = BitmapPixelFormat.Gray16;
                break;
            default:
                error = $"JPEG XR decoding is only implemented for mono (Gray8/Gray16) images, not {format}";
                return false;
        }

        try
        {
            var decoded = Task.Run(() => DecodeAsync(data, target)).GetAwaiter().GetResult();
            var expected = width * height * CziReader.BytesPerPixel(format);
            if (decoded.Length < expected)
            {
                error = $"JPEG XR tile decoded to {decoded.Length} bytes, expected {expected} for {width}x{height} {format}";
                return false;
            }

            pixels = decoded.Length == expected ? decoded : decoded[..expected];
            return true;
        }
        catch (Exception ex)
        {
            error = $"Windows JPEG XR decode failed: {ex.Message}";
            return false;
        }
    }

    private static async Task<byte[]> DecodeAsync(byte[] data, BitmapPixelFormat target)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(data.AsBuffer());
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.JpegXRDecoderId, stream);
        var provider = await decoder.GetPixelDataAsync(
            target,
            BitmapAlphaMode.Ignore,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        return provider.DetachPixelData();
    }
}
