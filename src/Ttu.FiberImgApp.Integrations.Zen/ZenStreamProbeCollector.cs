using System.Globalization;
using System.Text;
using Ttu.FiberImgApp.Core.Models;
using ZenApi.Acquisition.V1Beta;

namespace Ttu.FiberImgApp.Integrations.Zen;

/// <summary>
/// Tallies the metadata of every stream message heard during a probe (channel, size, pixel type, bytes)
/// without decoding pixels, then explains the outcome in plain language.
/// </summary>
internal sealed class ZenStreamProbeCollector
{
    private readonly object _gate = new();
    private readonly Dictionary<Group, int> _groups = new();
    private readonly List<string> _notes = [];
    private int _total;

    private readonly record struct Group(string Source, int? Channel, int Width, int Height, PixelType Type, long Bytes);

    public void Add(string source, FrameData? data)
    {
        if (data is null)
            return;

        var group = new Group(
            source,
            data.FramePosition?.C,
            data.FrameSize?.Width ?? 0,
            data.FrameSize?.Height ?? 0,
            data.PixelData?.PixelType ?? PixelType.Unspecified,
            data.PixelData?.RawData.Length ?? 0);

        lock (_gate)
        {
            _total++;
            _groups[group] = _groups.GetValueOrDefault(group) + 1;
        }
    }

    public void AddNote(string note)
    {
        lock (_gate) _notes.Add(note);
    }

    public ZenStreamProbeResult Build(TimeSpan listened)
    {
        lock (_gate)
        {
            var withPixels = _groups.Where(g => g.Key.Bytes > 0).ToList();
            var pixelMessages = withPixels.Sum(g => g.Value);
            var channels = withPixels
                .Select(g => g.Key.Channel)
                .Where(c => c.HasValue)
                .Select(c => c!.Value)
                .Distinct()
                .Order()
                .ToList();

            var text = new StringBuilder();
            var seconds = listened.TotalSeconds.ToString("0", CultureInfo.InvariantCulture);
            if (_total == 0)
            {
                text.Append($"No stream messages arrived in {seconds}s. ZEN is not sending pixels to this app at all. ")
                    .Append("Make sure the image is updating in ZEN (Live running), Unsupervised API Mode is on ")
                    .Append("(ZEN: Tools > Options > ZEN API), and the experiment's active camera is the Axiocam.");
            }
            else
            {
                text.Append($"Heard {_total} stream messages in {seconds}s ({pixelMessages} carried pixels).");
                foreach (var (group, count) in withPixels.OrderBy(g => g.Key.Channel).ThenBy(g => g.Key.Source))
                {
                    var channel = group.Channel?.ToString(CultureInfo.InvariantCulture) ?? "n/a";
                    text.Append($"{Environment.NewLine}Channel {channel}: {count} frame(s), {group.Width}x{group.Height}, ")
                        .Append($"{group.Type} ({(int)group.Type}), {group.Bytes:N0} bytes each ({group.Source}).");
                }

                if (pixelMessages == 0)
                    text.Append($"{Environment.NewLine}Messages arrived but none carried pixel data.");
                else if (channels.Count == 1)
                    text.Append($"{Environment.NewLine}Pixels are arriving on channel {channels[0]}. Leave Stream all channels on, ")
                        .Append($"or turn it off and set the channel to {channels[0]}.");
                else if (channels.Count > 1)
                    text.Append($"{Environment.NewLine}Pixels arrive on channels {string.Join(", ", channels)}. ")
                        .Append("Pick the one that belongs to the Axiocam, or leave Stream all channels on.");
            }

            foreach (var note in _notes)
                text.Append($"{Environment.NewLine}{note}");

            return new ZenStreamProbeResult
            {
                TotalMessages = _total,
                PixelMessages = pixelMessages,
                Channels = channels,
                Summary = text.ToString(),
            };
        }
    }
}
