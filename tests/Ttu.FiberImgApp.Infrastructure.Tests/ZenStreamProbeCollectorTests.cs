using Google.Protobuf;
using Ttu.FiberImgApp.Core.Configuration;
using Ttu.FiberImgApp.Integrations.Zen;
using ZenApi.Acquisition.V1Beta;
using ZenApi.Common.V1;

namespace Ttu.FiberImgApp.Infrastructure.Tests;

public class ZenStreamProbeCollectorTests
{
    [Fact]
    public void No_messages_explains_that_zen_is_not_sending_pixels()
    {
        var result = new ZenStreamProbeCollector().Build(TimeSpan.FromSeconds(10));

        Assert.Equal(0, result.TotalMessages);
        Assert.Empty(result.Channels);
        Assert.Contains("not sending pixels", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Frames_on_one_channel_report_that_channel()
    {
        var collector = new ZenStreamProbeCollector();
        collector.Add("MonitorAllExperiments", Frame(channel: 3, width: 4, height: 2, bytes: 16));
        collector.Add("MonitorAllExperiments", Frame(channel: 3, width: 4, height: 2, bytes: 16));

        var result = collector.Build(TimeSpan.FromSeconds(10));

        Assert.Equal(2, result.PixelMessages);
        Assert.Equal(new[] { 3 }, result.Channels);
        Assert.Contains("channel 3", result.Summary, StringComparison.Ordinal);
        Assert.Contains("4x2", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Frames_on_several_channels_list_them_all_in_order()
    {
        var collector = new ZenStreamProbeCollector();
        collector.Add("MonitorExperiment", Frame(channel: 2, width: 2, height: 2, bytes: 8));
        collector.Add("MonitorExperiment", Frame(channel: 0, width: 2, height: 2, bytes: 8));

        var result = collector.Build(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { 0, 2 }, result.Channels);
        Assert.Contains("0, 2", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_messages_count_but_do_not_produce_a_channel()
    {
        var collector = new ZenStreamProbeCollector();
        collector.Add("MonitorExperiment", Frame(channel: 1, width: 2, height: 2, bytes: 0));

        var result = collector.Build(TimeSpan.FromSeconds(10));

        Assert.Equal(1, result.TotalMessages);
        Assert.Equal(0, result.PixelMessages);
        Assert.Empty(result.Channels);
        Assert.Contains("none carried pixel data", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Notes_are_appended_to_the_summary()
    {
        var collector = new ZenStreamProbeCollector();
        collector.AddNote("StartLive failed: nope");

        Assert.Contains("StartLive failed: nope", collector.Build(TimeSpan.FromSeconds(10)).Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Stream_all_channels_sends_no_filter_and_pinning_sends_the_channel()
    {
        var options = new ZenOptions { ChannelIndex = 4 };

        Assert.True(options.StreamAllChannels);
        Assert.Null(options.EffectiveChannelIndex);

        options.StreamAllChannels = false;
        Assert.Equal(4, options.EffectiveChannelIndex);
    }

    private static FrameData Frame(int channel, int width, int height, int bytes) =>
        new()
        {
            FramePosition = new FramePosition { C = channel },
            FrameSize = new IntSize { Width = width, Height = height },
            PixelData = new FramePixelData
            {
                PixelType = PixelType.Gray8,
                RawData = ByteString.CopyFrom(new byte[bytes]),
            },
        };
}
