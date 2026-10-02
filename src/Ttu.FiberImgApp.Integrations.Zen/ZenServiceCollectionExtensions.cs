using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Ttu.FiberImgApp.Core.Abstractions;
using Ttu.FiberImgApp.Core.Configuration;
using Ttu.FiberImgApp.Core.Models;
using Ttu.FiberImgApp.Infrastructure.Cameras;

namespace Ttu.FiberImgApp.Integrations.Zen;

public static class ZenServiceCollectionExtensions
{
    public static IServiceCollection AddZenIntegration(this IServiceCollection services)
    {
        services.AddSingleton<PlaceholderCameraService>();
        services.AddSingleton<ZenApiCameraService>();
        services.AddSingleton<ICameraService>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ZenOptions>>().Value;
            return options.UseSimulator
                ? sp.GetRequiredService<PlaceholderCameraService>()
                : sp.GetRequiredService<ZenApiCameraService>();
        });
        services.AddSingleton<IZenClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ZenOptions>>().Value;
            if (options.UseSimulator)
                return new SimulatorZenClient();
            return sp.GetRequiredService<ZenApiCameraService>();
        });
        return services;
    }
}

internal sealed class SimulatorZenClient : IZenClient
{
    public string Status => "ZEN simulator mode (camera placeholder)";
    public Task<bool> ConnectAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ZenStreamProbeResult> ProbeStreamAsync(TimeSpan listenFor, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ZenStreamProbeResult
        {
            Summary = "ZEN simulator mode: there is no Gateway to listen to. Turn off \"Use ZEN simulator\", " +
                      "save, restart the app, then run Detect stream channel again.",
        });
}