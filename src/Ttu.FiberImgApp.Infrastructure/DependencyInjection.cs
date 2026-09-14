using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;
using Ttu.FiberImgApp.Core.Abstractions;
using Ttu.FiberImgApp.Core.Configuration;
using Ttu.FiberImgApp.Infrastructure.Data;
using Ttu.FiberImgApp.Infrastructure.Sampling;

namespace Ttu.FiberImgApp.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Ensure native SQLite is loaded before first EF use (avoids opaque type-init failures).
        SQLitePCL.Batteries_V2.Init();

        services.Configure<ZenOptions>(configuration.GetSection(ZenOptions.SectionName));
        services.Configure<ThorlabsOptions>(configuration.GetSection(ThorlabsOptions.SectionName));
        services.Configure<CaptureOptions>(configuration.GetSection(CaptureOptions.SectionName));
        services.Configure<UpdatesOptions>(configuration.GetSection(UpdatesOptions.SectionName));
        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));
        services.Configure<LoggingOptions>(configuration.GetSection(LoggingOptions.SectionName));

        var databaseOptions = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()
            ?? new DatabaseOptions();
        var dbPath = ResolveDatabasePath(databaseOptions.Path);

        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        services.AddSingleton<AppSettingsState>(sp =>
        {
            var state = new AppSettingsState();
            var thorlabs = configuration.GetSection(ThorlabsOptions.SectionName).Get<ThorlabsOptions>() ?? new ThorlabsOptions();
            var capture = configuration.GetSection(CaptureOptions.SectionName).Get<CaptureOptions>() ?? new CaptureOptions();
            state.LoadFrom(thorlabs, capture);
            return state;
        });
        services.AddSingleton<ISamplingService, SamplingService>();
        services.AddSingleton<ISessionExportService, SessionExportService>();

        return services;
    }

    public static void ConfigureSerilog(LoggerConfiguration loggerConfiguration, IConfiguration configuration)
    {
        var logDir = Path.Combine(GetAppDataDirectory(), "logs");
        Directory.CreateDirectory(logDir);

        var minimumLevel = configuration.GetSection(LoggingOptions.SectionName)["MinimumLevel"]
            ?? "Information";

        try
        {
            loggerConfiguration.ReadFrom.Configuration(configuration);
        }
        catch
        {
            // Ignore invalid Serilog config sections; file sink below is enough.
        }

        loggerConfiguration
            .MinimumLevel.Is(ParseLevel(minimumLevel))
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(logDir, "fiberimg-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14);
    }

    public static string GetAppDataDirectory()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TTU",
            "FiberImgApp");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string ResolveDatabasePath(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var directory = Path.GetDirectoryName(configuredPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            return configuredPath;
        }

        return Path.Combine(GetAppDataDirectory(), "fiberimg.db");
    }

    private static LogEventLevel ParseLevel(string level) =>
        Enum.TryParse<LogEventLevel>(level, ignoreCase: true, out var parsed)
            ? parsed
            : LogEventLevel.Information;
}
