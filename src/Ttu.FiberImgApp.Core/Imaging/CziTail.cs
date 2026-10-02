using Ttu.FiberImgApp.Core.Models;

namespace Ttu.FiberImgApp.Core.Imaging;

/// <summary>
/// Follows a CZI that ZEN is still writing: each call returns the newest complete plane that has not been
/// returned before. This is what lets live view show frames as a time-series experiment produces them
/// instead of waiting for the whole run to finish.
/// </summary>
public sealed class CziTail
{
    private long _lastLength = -1;

    /// <summary>File position of the newest plane returned so far (-1 before the first one).</summary>
    public long LastKey { get; private set; } = -1;

    /// <summary>Size of the file the last time it was opened, for deciding when to stop an over-long run.</summary>
    public long FileLength { get; private set; }

    /// <param name="force">Read even if the file has not grown since the last call.</param>
    /// <param name="frame">A new plane, or null when there is nothing newer (the call still succeeded).</param>
    /// <param name="permanent">True when the file is readable but cannot be used, so retrying will not help.</param>
    public bool TryReadNew(
        string path,
        IReadOnlyList<ICziTileDecoder>? extraDecoders,
        bool force,
        out CameraFrame? frame,
        out string error,
        out bool permanent)
    {
        frame = null;
        permanent = false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            FileLength = length;
            if (!force && length == _lastLength)
            {
                error = string.Empty;
                return true;
            }

            var ok = CziReader.TryReadPlane(stream, extraDecoders, latest: true, LastKey, out frame, out var key, out error, out permanent);
            if (!ok)
            {
                _lastLength = -1; // ZEN may still be writing; look again on the next pass
                return false;
            }

            _lastLength = length;
            if (frame is not null)
                LastKey = key;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OverflowException)
        {
            _lastLength = -1;
            error = $"could not read {Path.GetFileName(path)}: {ex.Message}";
            return false;
        }
    }
}
