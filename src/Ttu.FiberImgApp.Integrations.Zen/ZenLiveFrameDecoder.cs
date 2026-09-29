using Ttu.FiberImgApp.Core.Models;
using ZenApi.Acquisition.V1Beta;

namespace Ttu.FiberImgApp.Integrations.Zen;

/// <summary>
/// Turns ZEN <see cref="FrameData"/> into a displayable <see cref="CameraFrame"/>.
/// Matches zenapi_streaming.py: complete frames (<c>enable_raw_data</c> false) are shaped
/// with <c>FrameSize</c>; partial frames are shaped with <c>PixelData.Size</c> and stitched
/// when a packet is only part of the full frame.
/// </summary>
internal sealed class ZenLiveFrameDecoder
{
    private byte[] _pixels = [];
    private int _width;
    private int _height;
    private CameraPixelFormat _format;
    private int _bpp;
    private long _lastEmitTick;

    public void Reset()
    {
        _pixels = [];
        _width = 0;
        _height = 0;
        _bpp = 0;
        _format = default;
        _lastEmitTick = 0;
    }

    public CameraFrame? Add(FrameData? data, bool enableRawData, out string skipReason)
    {
        skipReason = string.Empty;
        if (data?.PixelData is null)
        {
            skipReason = "PixelData is null";
            return null;
        }

        var raw = data.PixelData.RawData.ToByteArray();
        if (raw.Length == 0)
        {
            skipReason = "RawData is empty";
            return null;
        }

        var fullW = data.FrameSize?.Width ?? 0;
        var fullH = data.FrameSize?.Height ?? 0;
        var tileW = data.PixelData.Size?.Width ?? 0;
        var tileH = data.PixelData.Size?.Height ?? 0;

        if (!enableRawData)
        {
            // zenapi_streaming.py reshapes complete frames with frame_size, not the partial tile.
            if (TryTakeComplete(raw, data.PixelData.PixelType, fullW, fullH, out var fullFrame, out var fullDiag))
            {
                Reset();
                if (!string.IsNullOrEmpty(fullDiag)) skipReason = fullDiag;
                return fullFrame;
            }

            if (TryTakeComplete(raw, data.PixelData.PixelType, tileW, tileH, out var tileFrame, out var tileDiag)
                && (fullW <= 0 || fullH <= 0 || (tileW == fullW && tileH == fullH)))
            {
                Reset();
                if (!string.IsNullOrEmpty(tileDiag)) skipReason = tileDiag;
                return tileFrame;
            }
        }

        if (!TryResolveFormat(data.PixelData.PixelType, raw.Length, fullW, fullH, tileW, tileH, out var format, out var bpp, out var resolveDiag))
        {
            skipReason = $"Unsupported pixel type {data.PixelData.PixelType}";
            return null;
        }

        if (enableRawData)
            InferPartialSize(raw.Length, bpp, ref tileW, ref tileH, fullW, fullH);
        else
        {
            if (tileW <= 0) tileW = fullW;
            if (tileH <= 0) tileH = fullH;
        }

        if (fullW <= 0) fullW = tileW;
        if (fullH <= 0) fullH = tileH;
        if (tileW <= 0 || tileH <= 0 || fullW <= 0 || fullH <= 0)
        {
            skipReason = "Width/Height missing on frame";
            return null;
        }

        if (!BufferMatches(tileW, tileH, bpp, raw.Length))
        {
            var expected = tileW * (long)tileH * bpp;
            skipReason = $"Got {raw.Length} bytes, expected {expected} for {tileW}x{tileH} {format} (frame {fullW}x{fullH})";
            return null;
        }

        var startX = data.PixelData.StartPosition?.X ?? 0;
        var startY = data.PixelData.StartPosition?.Y ?? 0;
        if (data.PixelData.StartPosition is null && (tileW < fullW || tileH < fullH))
        {
            startX = data.FramePosition?.X ?? 0;
            startY = data.FramePosition?.Y ?? 0;
        }

        if (startX == 0 && startY == 0 && tileW == fullW && tileH == fullH)
        {
            Reset();
            if (!string.IsNullOrEmpty(resolveDiag)) skipReason = resolveDiag;
            return CreateFrame(raw, fullW, fullH, format);
        }

        EnsureCanvas(fullW, fullH, format, bpp);
        Blit(raw, startX, startY, tileW, tileH);

        var now = Environment.TickCount64;
        var completed = startY + tileH >= _height && startX + tileW >= _width;
        if (!completed && now - _lastEmitTick < 120)
        {
            skipReason = "buffering scan lines";
            return null;
        }

        _lastEmitTick = now;
        if (!string.IsNullOrEmpty(resolveDiag)) skipReason = resolveDiag;
        return CreateFrame((byte[])_pixels.Clone(), _width, _height, _format);
    }

    private static bool TryTakeComplete(
        byte[] raw,
        PixelType pixelType,
        int width,
        int height,
        out CameraFrame? frame,
        out string diagnostic)
    {
        frame = null;
        if (!TryFormatForSize(pixelType, width, height, raw.Length, out var format, out _, out diagnostic))
            return false;

        frame = CreateFrame(raw, width, height, format);
        return true;
    }

    private static void InferPartialSize(int byteLength, int bpp, ref int tileW, ref int tileH, int fullW, int fullH)
    {
        if (tileW <= 0 && fullW > 0 && byteLength >= fullW * bpp && byteLength % (fullW * bpp) == 0)
        {
            tileW = fullW;
            tileH = byteLength / (fullW * bpp);
            return;
        }

        if (tileW > 0 && tileH <= 0 && byteLength >= tileW * bpp && byteLength % (tileW * bpp) == 0)
            tileH = byteLength / (tileW * bpp);

        if (tileW <= 0) tileW = fullW;
        if (tileH <= 0) tileH = fullH;
    }

    private static bool TryResolveFormat(
        PixelType pixelType,
        int byteLength,
        int fullW,
        int fullH,
        int tileW,
        int tileH,
        out CameraPixelFormat format,
        out int bpp,
        out string diagnostic)
    {
        var width = tileW > 0 ? tileW : fullW;
        var height = tileH > 0 ? tileH : fullH;
        if (TryFormatForSize(pixelType, width, height, byteLength, out format, out bpp, out diagnostic))
            return true;

        format = InferFormat(byteLength, width, height);
        bpp = BytesPerPixel(format);
        return bpp > 0;
    }

    /// <summary>
    /// Resolves the pixel format for a buffer of the given size. When ZEN's declared
    /// <paramref name="pixelType"/> is one this app recognizes, it's tried first - but if the buffer
    /// doesn't actually match that format's expected byte length, this still falls back to scanning the
    /// other known formats by byte length rather than giving up immediately. That fallback also catches
    /// the case where <c>pixel_type.proto</c>'s hand-transcribed enum numbering doesn't match what this
    /// specific ZEN Gateway/Axiocam build actually sends on the wire: <paramref name="diagnostic"/> is set
    /// (non-fatal - the frame is still decoded) whenever the byte-length match picks a format different
    /// from the one ZEN declared, which is the strongest signal we can get without vendor proto access.
    /// </summary>
    private static bool TryFormatForSize(
        PixelType pixelType,
        int width,
        int height,
        int byteLength,
        out CameraPixelFormat format,
        out int bpp,
        out string diagnostic)
    {
        diagnostic = string.Empty;
        var declared = pixelType is PixelType.Gray8 or PixelType.Gray16 or PixelType.Bgr24 or PixelType.Bgr48;
        if (declared)
        {
            format = Map(pixelType);
            bpp = BytesPerPixel(format);
            if (bpp > 0 && BufferMatches(width, height, bpp, byteLength))
                return true;
        }

        foreach (var candidate in new[]
                 {
                     CameraPixelFormat.Gray16,
                     CameraPixelFormat.Gray8,
                     CameraPixelFormat.Bgr48,
                     CameraPixelFormat.Bgr24,
                 })
        {
            bpp = BytesPerPixel(candidate);
            if (!BufferMatches(width, height, bpp, byteLength))
                continue;

            format = candidate;
            if (declared && Map(pixelType) != candidate)
            {
                diagnostic = $"ZEN declared PixelType={pixelType} ({(int)pixelType}) but the {width}x{height} " +
                    $"buffer ({byteLength} bytes) matches {candidate} instead - check pixel_type.proto's enum " +
                    "numbering against the real ZEN API.";
            }

            return true;
        }

        format = default;
        bpp = 0;
        return false;
    }

    private static bool BufferMatches(int width, int height, int bpp, int byteLength) =>
        width > 0 && height > 0 && bpp > 0
        && byteLength % bpp == 0
        && byteLength / bpp == (long)width * height;

    private static CameraFrame CreateFrame(byte[] raw, int width, int height, CameraPixelFormat format) =>
        new()
        {
            RawPixels = raw,
            Width = width,
            Height = height,
            PixelFormat = format,
            CapturedAt = DateTimeOffset.UtcNow,
        };

    private void EnsureCanvas(int width, int height, CameraPixelFormat format, int bpp)
    {
        if (_width == width && _height == height && _format == format && _pixels.Length == width * height * bpp)
            return;

        _width = width;
        _height = height;
        _format = format;
        _bpp = bpp;
        _pixels = new byte[checked(width * height * bpp)];
        _lastEmitTick = 0;
    }

    private void Blit(byte[] src, int startX, int startY, int tileW, int tileH)
    {
        var srcStride = tileW * _bpp;
        var dstStride = _width * _bpp;
        for (var row = 0; row < tileH; row++)
        {
            var dy = startY + row;
            if ((uint)dy >= (uint)_height) continue;
            var dx = Math.Max(0, startX);
            var srcOffset = (row * srcStride) + ((dx - startX) * _bpp);
            var copy = Math.Min(srcStride - ((dx - startX) * _bpp), (_width - dx) * _bpp);
            if (copy <= 0 || srcOffset < 0 || srcOffset + copy > src.Length) continue;
            Buffer.BlockCopy(src, srcOffset, _pixels, (dy * dstStride) + (dx * _bpp), copy);
        }
    }

    private static CameraPixelFormat Map(PixelType pixelType) => pixelType switch
    {
        PixelType.Gray8 => CameraPixelFormat.Gray8,
        PixelType.Gray16 => CameraPixelFormat.Gray16,
        PixelType.Bgr24 => CameraPixelFormat.Bgr24,
        PixelType.Bgr48 => CameraPixelFormat.Bgr48,
        _ => default,
    };

    private static CameraPixelFormat InferFormat(int byteLength, int width, int height)
    {
        if (width <= 0 || height <= 0) return CameraPixelFormat.Gray8;

        var pixels = width * (long)height;
        if (byteLength >= pixels * 6) return CameraPixelFormat.Bgr48;
        if (byteLength >= pixels * 3) return CameraPixelFormat.Bgr24;
        if (byteLength >= pixels * 2) return CameraPixelFormat.Gray16;
        return CameraPixelFormat.Gray8;
    }

    private static int BytesPerPixel(CameraPixelFormat format) => format switch
    {
        CameraPixelFormat.Gray8 => 1,
        CameraPixelFormat.Gray16 => 2,
        CameraPixelFormat.Bgr24 => 3,
        CameraPixelFormat.Bgr48 => 6,
        _ => 0,
    };
}
