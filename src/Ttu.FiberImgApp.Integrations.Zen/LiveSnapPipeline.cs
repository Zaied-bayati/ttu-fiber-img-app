using System.Diagnostics;
using System.Threading.Channels;
using Ttu.FiberImgApp.Core.Models;

namespace Ttu.FiberImgApp.Integrations.Zen;

/// <summary>A failure that retrying cannot fix (for example ZEN refusing API control); live view stops at once.</summary>
internal sealed class LiveSnapFatalException(string message) : Exception(message);

/// <summary>
/// Live view as two decoupled halves joined by a small bounded queue of image files:
/// the producer asks ZEN for a snap, and as soon as that returns (ZEN has finished and written the CZI) queues the
/// file's path and asks for the next snap straight away; the consumer takes paths in order, reads the CZI, shows
/// the frame and deletes the file. Reading and displaying therefore never hold up the next acquisition, and a
/// file is only ever read after ZEN has finished the snap that wrote it.
///
/// The queue is bounded: when the consumer falls behind, the producer waits instead of filling the disk.
/// Order is preserved and no frame is dropped. Every file that was queued is deleted exactly once, including
/// the ones still waiting when live stops.
/// </summary>
internal sealed class LiveSnapPipeline
{
    private readonly Func<CancellationToken, Task<string>> _acquire;
    private readonly Func<string, CancellationToken, Task<(CameraFrame? Frame, string Error)>> _read;
    private readonly Action<CameraFrame> _publish;
    private readonly Func<string, bool> _delete;
    private readonly int _capacity;
    private readonly int _maxConsecutiveFailures;
    private readonly TimeSpan _retryDelay;
    private readonly List<string> _undeleted = [];

    private long _acquireMs;
    private long _readMs;
    private int _queued;
    private string? _failureReason;

    /// <param name="acquire">Asks ZEN for one snap and returns the path of the CZI it wrote. Throws on failure.</param>
    /// <param name="read">Reads a CZI into a frame (waiting for it to become readable). Frame is null on failure.</param>
    /// <param name="publish">Shows a frame.</param>
    /// <param name="delete">Deletes a file; false if it could not be deleted yet (it is retried later).</param>
    public LiveSnapPipeline(
        Func<CancellationToken, Task<string>> acquire,
        Func<string, CancellationToken, Task<(CameraFrame? Frame, string Error)>> read,
        Action<CameraFrame> publish,
        Func<string, bool> delete,
        int capacity = 4,
        int maxConsecutiveFailures = 3,
        TimeSpan? retryDelay = null)
    {
        _acquire = acquire;
        _read = read;
        _publish = publish;
        _delete = delete;
        _capacity = Math.Max(1, capacity);
        _maxConsecutiveFailures = Math.Max(1, maxConsecutiveFailures);
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(500);
    }

    /// <summary>Why the pipeline gave up (null if it ended because it was cancelled).</summary>
    public string? FailureReason => Volatile.Read(ref _failureReason);

    /// <summary>How long the last ZEN snap took, in milliseconds.</summary>
    public long LastAcquireMs => Interlocked.Read(ref _acquireMs);

    /// <summary>How long the last CZI read and display took, in milliseconds.</summary>
    public long LastReadMs => Interlocked.Read(ref _readMs);

    /// <summary>Files waiting to be read.</summary>
    public int QueueDepth => Volatile.Read(ref _queued);

    /// <summary>Runs until <paramref name="token"/> is cancelled or the pipeline gives up. Never throws for those.</summary>
    public async Task RunAsync(CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(_capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });

        try
        {
            await Task.WhenAll(ProduceAsync(channel.Writer, cts), ConsumeAsync(channel.Reader, cts)).ConfigureAwait(false);
        }
        finally
        {
            channel.Writer.TryComplete();
            while (channel.Reader.TryRead(out var path))
            {
                Interlocked.Decrement(ref _queued);
                Discard(path);
            }

            _undeleted.RemoveAll(p => _delete(p));
        }
    }

    private async Task ProduceAsync(ChannelWriter<string> writer, CancellationTokenSource cts)
    {
        var failures = 0;
        try
        {
            while (!cts.IsCancellationRequested)
            {
                string path;
                var timer = Stopwatch.StartNew();
                try
                {
                    path = await _acquire(cts.Token).ConfigureAwait(false);
                }
                catch (Exception) when (cts.IsCancellationRequested)
                {
                    return;
                }
                catch (LiveSnapFatalException ex)
                {
                    Fail(ex.Message);
                    return;
                }
                catch (Exception ex)
                {
                    if (++failures >= _maxConsecutiveFailures)
                    {
                        Fail(ex.Message);
                        return;
                    }

                    try { await Task.Delay(_retryDelay, cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    continue;
                }

                failures = 0;
                Interlocked.Exchange(ref _acquireMs, timer.ElapsedMilliseconds);

                Interlocked.Increment(ref _queued);
                try
                {
                    await writer.WriteAsync(path, cts.Token).ConfigureAwait(false); // waits here when the queue is full
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Decrement(ref _queued);
                    Discard(path);
                    return;
                }
            }
        }
        finally
        {
            cts.Cancel(); // the consumer stops too; whatever is still queued is cleaned up by RunAsync
        }
    }

    private async Task ConsumeAsync(ChannelReader<string> reader, CancellationTokenSource cts)
    {
        var failures = 0;
        try
        {
            await foreach (var path in reader.ReadAllAsync(cts.Token).ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _queued);
                var timer = Stopwatch.StartNew();
                try
                {
                    var (frame, error) = await _read(path, cts.Token).ConfigureAwait(false);
                    if (frame is not null)
                    {
                        failures = 0;
                        _publish(frame);
                        Interlocked.Exchange(ref _readMs, timer.ElapsedMilliseconds);
                    }
                    else if (++failures >= _maxConsecutiveFailures)
                    {
                        Fail(string.IsNullOrWhiteSpace(error) ? "the image could not be read" : error);
                        return;
                    }
                }
                catch (Exception) when (cts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (++failures >= _maxConsecutiveFailures)
                    {
                        Fail(ex.Message);
                        return;
                    }
                }
                finally
                {
                    Discard(path);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
        finally
        {
            cts.Cancel();
        }
    }

    private void Fail(string reason) => Interlocked.CompareExchange(ref _failureReason, reason, null);

    private void Discard(string path)
    {
        // Retry earlier stragglers (a file ZEN still had open) whenever another file goes.
        lock (_undeleted)
        {
            _undeleted.RemoveAll(p => _delete(p));
            if (!_delete(path))
                _undeleted.Add(path);
        }
    }
}
