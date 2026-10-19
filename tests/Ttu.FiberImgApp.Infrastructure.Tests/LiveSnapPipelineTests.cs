using System.Collections.Concurrent;
using Ttu.FiberImgApp.Core.Models;
using Ttu.FiberImgApp.Integrations.Zen;

namespace Ttu.FiberImgApp.Infrastructure.Tests;

public class LiveSnapPipelineTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Frames_are_shown_in_the_order_they_were_acquired_and_each_file_is_deleted()
    {
        var h = new Harness { AcquireLimit = 3 };
        using var cts = new CancellationTokenSource();
        var run = h.Pipeline().RunAsync(cts.Token);

        await Eventually(() => h.Published.Count == 3);
        cts.Cancel();
        await run.WaitAsync(Limit);

        Assert.Equal([1, 2, 3], h.Published.ToArray());
        Assert.Equal(["snap1.czi", "snap2.czi", "snap3.czi"], h.Deleted.OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Acquisition_keeps_going_while_the_reader_is_busy_but_stops_at_the_queue_capacity()
    {
        var h = new Harness { BlockReads = true };
        using var cts = new CancellationTokenSource();
        var run = h.Pipeline(capacity: 2).RunAsync(cts.Token);

        // One file is being read, two are queued, and the fourth snap is waiting for room.
        await Eventually(() => h.Acquired == 4);
        await Task.Delay(200);
        Assert.Equal(4, h.Acquired);

        cts.Cancel();
        await run.WaitAsync(Limit);
    }

    [Fact]
    public async Task Stopping_deletes_every_file_that_was_queued_exactly_once()
    {
        var h = new Harness { BlockReads = true };
        using var cts = new CancellationTokenSource();
        var run = h.Pipeline(capacity: 2).RunAsync(cts.Token);
        await Eventually(() => h.Acquired == 4);

        cts.Cancel();
        await run.WaitAsync(Limit);

        Assert.Equal(["snap1.czi", "snap2.czi", "snap3.czi", "snap4.czi"], h.Deleted.OrderBy(x => x).ToArray());
        Assert.Empty(h.Published);
    }

    [Fact]
    public async Task Three_acquisition_failures_in_a_row_stop_live_with_the_last_error()
    {
        var h = new Harness { AcquireError = "RunSnap boom" };

        var pipeline = h.Pipeline();
        await pipeline.RunAsync(CancellationToken.None).WaitAsync(Limit);

        Assert.Equal("RunSnap boom", pipeline.FailureReason);
        Assert.Equal(3, h.Acquired);
    }

    [Fact]
    public async Task A_fatal_acquisition_error_stops_at_once()
    {
        var h = new Harness { FatalError = "API control is locked" };

        var pipeline = h.Pipeline();
        await pipeline.RunAsync(CancellationToken.None).WaitAsync(Limit);

        Assert.Equal("API control is locked", pipeline.FailureReason);
        Assert.Equal(1, h.Acquired);
    }

    [Fact]
    public async Task Three_unreadable_files_in_a_row_stop_live_and_all_files_are_cleaned_up()
    {
        var h = new Harness { ReadError = "unsupported compression" };

        var pipeline = h.Pipeline();
        await pipeline.RunAsync(CancellationToken.None).WaitAsync(Limit);

        Assert.Equal("unsupported compression", pipeline.FailureReason);
        Assert.Empty(h.Published);
        Assert.Equal(h.Acquired, h.Deleted.Count); // nothing left behind
        Assert.Equal(h.Deleted.Count, h.Deleted.Distinct().Count());
    }

    [Fact]
    public async Task An_occasional_unreadable_file_does_not_stop_live()
    {
        var h = new Harness { FailEveryOtherRead = true };
        using var cts = new CancellationTokenSource();
        var pipeline = h.Pipeline();
        var run = pipeline.RunAsync(cts.Token);

        await Eventually(() => h.Published.Count >= 3);
        cts.Cancel();
        await run.WaitAsync(Limit);

        Assert.Null(pipeline.FailureReason);
    }

    [Fact]
    public async Task Cancelling_while_waiting_for_ZEN_ends_quietly()
    {
        var h = new Harness { BlockAcquire = true };
        using var cts = new CancellationTokenSource();
        var pipeline = h.Pipeline();
        var run = pipeline.RunAsync(cts.Token);
        await Eventually(() => h.Acquired == 1);

        cts.Cancel();
        await run.WaitAsync(Limit);

        Assert.Null(pipeline.FailureReason);
    }

    [Fact]
    public async Task A_file_that_cannot_be_deleted_yet_is_retried_until_it_goes()
    {
        var h = new Harness { AcquireLimit = 3, StubbornFile = "snap1.czi" };
        using var cts = new CancellationTokenSource();
        var run = h.Pipeline().RunAsync(cts.Token);

        await Eventually(() => h.Published.Count == 3);
        cts.Cancel();
        await run.WaitAsync(Limit);

        Assert.Contains("snap1.czi", h.Deleted);
        Assert.True(h.DeleteAttempts["snap1.czi"] >= 2);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Limit;
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("condition was not met in time");
            await Task.Delay(10);
        }
    }

    private static CameraFrame Frame(int tag) => new()
    {
        RawPixels = [0],
        Width = tag,
        Height = 1,
        PixelFormat = CameraPixelFormat.Gray8,
        CapturedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Fakes for the ZEN snap, the CZI read, the display and the file system.</summary>
    private sealed class Harness
    {
        private int _acquired;
        private int _reads;

        public int AcquireLimit { get; init; } = int.MaxValue;
        public bool BlockReads { get; init; }
        public bool BlockAcquire { get; init; }
        public string? AcquireError { get; init; }
        public string? FatalError { get; init; }
        public string? ReadError { get; init; }
        public bool FailEveryOtherRead { get; init; }
        public string? StubbornFile { get; init; }

        public int Acquired => Volatile.Read(ref _acquired);
        public ConcurrentQueue<int> Published { get; } = new();
        public ConcurrentBag<string> Deleted { get; } = [];
        public ConcurrentDictionary<string, int> DeleteAttempts { get; } = new();

        public LiveSnapPipeline Pipeline(int capacity = 4) => new(
            acquire: async ct =>
            {
                var n = Interlocked.Increment(ref _acquired);
                if (FatalError is not null)
                    throw new LiveSnapFatalException(FatalError);
                if (AcquireError is not null)
                    throw new InvalidOperationException(AcquireError);
                if (BlockAcquire || n > AcquireLimit)
                    await Task.Delay(Timeout.Infinite, ct);
                return $"snap{n}.czi";
            },
            read: async (path, ct) =>
            {
                if (BlockReads)
                    await Task.Delay(Timeout.Infinite, ct);

                var n = Interlocked.Increment(ref _reads);
                if (ReadError is not null || (FailEveryOtherRead && n % 2 == 1))
                    return (null, ReadError ?? "flaky");

                await Task.Yield();
                return (Frame(int.Parse(path.Replace("snap", string.Empty).Replace(".czi", string.Empty))), string.Empty);
            },
            publish: frame => Published.Enqueue(frame.Width),
            delete: path =>
            {
                var attempts = DeleteAttempts.AddOrUpdate(path, 1, (_, c) => c + 1);
                if (path == StubbornFile && attempts == 1)
                    return false;
                Deleted.Add(path);
                return true;
            },
            capacity: capacity,
            maxConsecutiveFailures: 3,
            retryDelay: TimeSpan.FromMilliseconds(1));
    }
}
