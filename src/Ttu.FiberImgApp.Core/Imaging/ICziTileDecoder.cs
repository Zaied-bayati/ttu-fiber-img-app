using Ttu.FiberImgApp.Core.Models;
using ZstdSharp;

namespace Ttu.FiberImgApp.Core.Imaging;

/// <summary>Decodes the pixel payload of one CZI sub-block for the compression modes it supports.</summary>
public interface ICziTileDecoder
{
    bool CanDecode(int compression);

    bool TryDecode(
        int compression,
        byte[] data,
        int width,
        int height,
        CameraPixelFormat format,
        out byte[] pixels,
        out string error);
}

/// <summary>Uncompressed (0) and Zstandard (5 = ZSTD0, 6 = ZSTD1 with optional hi/lo byte packing).</summary>
public sealed class BuiltInCziTileDecoder : ICziTileDecoder
{
    public bool CanDecode(int compression) => compression is 0 or 5 or 6;

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
        var bytesPerPixel = CziReader.BytesPerPixel(format);
        var expected = checked(width * height * bytesPerPixel);

        switch (compression)
        {
            case 0:
                if (data.Length < expected)
                {
                    error = $"uncompressed tile has {data.Length} bytes, expected {expected} for {width}x{height} {format}";
                    return false;
                }

                pixels = data.Length == expected ? data : data[..expected];
                return true;

            case 5:
                return TryUnzstd(data, 0, expected, hiLoPacked: false, bytesPerPixel, out pixels, out error);

            case 6:
                return TryUnzstd1(data, expected, bytesPerPixel, out pixels, out error);

            default:
                error = $"compression {compression} is not handled by the built-in decoder";
                return false;
        }
    }

    // ZSTD1 payload starts with a small header: [size][chunkType][chunkValue]... (size includes itself).
    // Chunk type 1 = hi/lo byte packing flag (bit 0).
    private static bool TryUnzstd1(byte[] data, int expected, int bytesPerPixel, out byte[] pixels, out string error)
    {
        pixels = [];
        error = string.Empty;
        if (data.Length < 1)
        {
            error = "ZSTD1 tile is empty";
            return false;
        }

        var headerSize = data[0];
        if (headerSize < 1 || headerSize > data.Length)
        {
            error = "ZSTD1 tile has an invalid header";
            return false;
        }

        var hiLo = false;
        for (var i = 1; i < headerSize;)
        {
            var chunk = data[i++];
            if (chunk == 1 && i < headerSize)
            {
                hiLo = (data[i] & 1) != 0;
                i++;
            }
            else
            {
                break;
            }
        }

        return TryUnzstd(data, headerSize, expected, hiLo, bytesPerPixel, out pixels, out error);
    }

    private static bool TryUnzstd(
        byte[] data,
        int offset,
        int expected,
        bool hiLoPacked,
        int bytesPerPixel,
        out byte[] pixels,
        out string error)
    {
        pixels = [];
        error = string.Empty;
        try
        {
            var source = new ReadOnlySpan<byte>(data, offset, data.Length - offset);

            // Like libCZI, tolerate a frame whose decoded size differs from the tile size: zero-fill if short, truncate if long.
            var contentSize = 0UL;
            try { contentSize = Decompressor.GetDecompressedSize(source); }
            catch (Exception) { /* fall back to the expected size */ }
            var bufferSize = contentSize is > 0 and <= int.MaxValue ? Math.Max((int)contentSize, expected) : expected;

            var buffer = new byte[bufferSize];
            using var decompressor = new Decompressor();
            var written = decompressor.Unwrap(source, buffer);

            var decoded = new byte[expected];
            Buffer.BlockCopy(buffer, 0, decoded, 0, Math.Min(written, expected));

            pixels = hiLoPacked && bytesPerPixel == 2 ? UnpackHiLo16(decoded) : decoded;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Zstandard decode failed: {ex.Message}";
            return false;
        }
    }

    // Packed layout: every low byte first, then every high byte.
    private static byte[] UnpackHiLo16(byte[] packed)
    {
        var count = packed.Length / 2;
        var result = new byte[packed.Length];
        for (var i = 0; i < count; i++)
        {
            result[2 * i] = packed[i];
            result[(2 * i) + 1] = packed[count + i];
        }

        return result;
    }
}
