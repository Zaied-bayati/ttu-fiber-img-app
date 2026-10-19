namespace Ttu.FiberImgApp.Integrations.Zen;

/// <summary>Frames per second over a sliding window, so the live status can say how fast the view really is.</summary>
internal sealed class LiveRateMeter(int windowMs = 5_000)
{
    private readonly Queue<long> _stamps = new();
    private readonly object _gate = new();

    public void Reset()
    {
        lock (_gate) _stamps.Clear();
    }

    /// <summary>Records a frame at <paramref name="nowMs"/> and returns the current rate (0 until two frames are in the window).</summary>
    public double Add(long nowMs, out int framesInWindow)
    {
        lock (_gate)
        {
            _stamps.Enqueue(nowMs);
            while (_stamps.Count > 1 && nowMs - _stamps.Peek() > windowMs)
                _stamps.Dequeue();

            framesInWindow = _stamps.Count;
            if (_stamps.Count < 2)
                return 0;

            var span = nowMs - _stamps.Peek();
            return span <= 0 ? 0 : (_stamps.Count - 1) * 1000.0 / span;
        }
    }
}
