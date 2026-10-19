using Ttu.FiberImgApp.Integrations.Zen;

namespace Ttu.FiberImgApp.Infrastructure.Tests;

public class LiveRateMeterTests
{
    [Fact]
    public void A_single_frame_has_no_rate_yet()
    {
        var meter = new LiveRateMeter();

        Assert.Equal(0, meter.Add(1_000, out var frames));
        Assert.Equal(1, frames);
    }

    [Fact]
    public void Ten_frames_per_second_reads_as_ten()
    {
        var meter = new LiveRateMeter();
        double rate = 0;
        for (var i = 0; i < 20; i++)
            rate = meter.Add(1_000 + (i * 100), out _);

        Assert.Equal(10, rate, precision: 3);
    }

    [Fact]
    public void Old_frames_fall_out_of_the_window_so_a_slowdown_shows()
    {
        var meter = new LiveRateMeter(windowMs: 5_000);
        for (var i = 0; i < 50; i++)
            meter.Add(i * 100, out _); // 10 fps for five seconds

        // Then one frame every two seconds.
        double rate = 0;
        for (var i = 1; i <= 4; i++)
            rate = meter.Add(5_000 + (i * 2_000), out _);

        Assert.InRange(rate, 0.4, 0.6);
    }

    [Fact]
    public void Reset_forgets_earlier_frames()
    {
        var meter = new LiveRateMeter();
        meter.Add(0, out _);
        meter.Add(100, out _);

        meter.Reset();

        Assert.Equal(0, meter.Add(200, out var frames));
        Assert.Equal(1, frames);
    }
}
