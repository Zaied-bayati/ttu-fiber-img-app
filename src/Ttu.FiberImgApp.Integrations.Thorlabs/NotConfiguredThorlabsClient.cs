using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Ttu.FiberImgApp.Core.Abstractions;
using Ttu.FiberImgApp.Core.Configuration;

namespace Ttu.FiberImgApp.Integrations.Thorlabs;

public static class ThorlabsServiceCollectionExtensions
{
    public static IServiceCollection AddThorlabsIntegration(this IServiceCollection services)
    {
        services.AddSingleton<SimulatedThorlabsClient>();
        services.AddSingleton<XaThorlabsClient>();
        services.AddSingleton<IThorlabsClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ThorlabsOptions>>().Value;
            return options.UseSimulator
                ? sp.GetRequiredService<SimulatedThorlabsClient>()
                : sp.GetRequiredService<XaThorlabsClient>();
        });
        return services;
    }
}
