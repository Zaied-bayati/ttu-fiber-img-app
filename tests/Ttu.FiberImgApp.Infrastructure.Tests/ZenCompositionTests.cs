using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ttu.FiberImgApp.Core.Abstractions;
using Ttu.FiberImgApp.Core.Configuration;
using Ttu.FiberImgApp.Core.Imaging;
using Ttu.FiberImgApp.Core.Models;
using Ttu.FiberImgApp.Integrations.Zen;

namespace Ttu.FiberImgApp.Infrastructure.Tests;

/// <summary>Resolves the real ZEN camera through the same registration the app uses, so a wiring mistake fails here and not on the lab PC.</summary>
public class ZenCompositionTests
{
    [Fact]
    public async Task The_real_zen_camera_resolves_with_the_jpeg_xr_decoder_registered()
    {
        await using var provider = Build(realZen: true, extraDecoder: true);

        var camera = provider.GetRequiredService<ICameraService>();

        Assert.IsType<ZenApiCameraService>(camera);
        Assert.Same(camera, provider.GetRequiredService<IZenClient>());
    }

    [Fact]
    public async Task The_real_zen_camera_resolves_when_no_extra_decoder_is_registered()
    {
        await using var provider = Build(realZen: true, extraDecoder: false);

        Assert.IsType<ZenApiCameraService>(provider.GetRequiredService<ICameraService>());
    }

    [Fact]
    public async Task Simulator_mode_probe_explains_there_is_no_gateway()
    {
        await using var provider = Build(realZen: false, extraDecoder: false);

        var result = await provider.GetRequiredService<IZenClient>().ProbeStreamAsync(TimeSpan.FromSeconds(1));

        Assert.Contains("simulator", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Detecting_the_channel_before_connecting_fails_clearly_instead_of_hanging()
    {
        await using var provider = Build(realZen: true, extraDecoder: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GetRequiredService<IZenClient>().ProbeStreamAsync(TimeSpan.FromSeconds(1)));

        Assert.Contains("Connect", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Starting_live_before_connecting_fails_clearly()
    {
        await using var provider = Build(realZen: true, extraDecoder: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GetRequiredService<ICameraService>().StartLiveAsync());
    }

    private static ServiceProvider Build(bool realZen, bool extraDecoder)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IActivityLog, SilentLog>();
        services.AddSingleton(Options.Create(new ZenOptions { UseSimulator = !realZen }));
        if (extraDecoder)
            services.AddSingleton<ICziTileDecoder, StubDecoder>();
        services.AddZenIntegration();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private sealed class StubDecoder : ICziTileDecoder
    {
        public bool CanDecode(int compression) => compression == 4;

        public bool TryDecode(int compression, byte[] data, int width, int height, CameraPixelFormat format, out byte[] pixels, out string error)
        {
            pixels = [];
            error = "stub";
            return false;
        }
    }

    private sealed class SilentLog : IActivityLog
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public IReadOnlyList<string> Lines => [];
        public string Text => string.Empty;
        public void Write(string message) { }
        public void Write(string category, string message) { }
        public void Clear() { }
    }
}
