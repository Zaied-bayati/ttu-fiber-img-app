using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using Ttu.FiberImgApp.Core.Abstractions;
using Ttu.FiberImgApp.Infrastructure;
using Ttu.FiberImgApp.Integrations.Thorlabs;
using Ttu.FiberImgApp.Integrations.Zen;
using Ttu.FiberImgApp.ViewModels;

namespace Ttu.FiberImgApp;

public partial class App : Application
{
    private Window? _window;
    private IHost? _host;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    public static IServiceProvider Services { get; private set; } = null!;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _host = BuildHost();
            Services = _host.Services;
            Services.GetRequiredService<IActivityLog>().Write("shell", "App started. Open Serial monitor for step-by-step status.");

            await using (var scope = Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
                await db.EnsureCreatedAsync();
            }

            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            var detail = FlattenException(ex);
            try
            {
                Log.Fatal(ex, "Startup failed");
                Log.CloseAndFlush();
            }
            catch
            {
                // Logging may be unavailable if host build failed.
            }

            ShowStartupError(detail);
        }
    }

    private static IHost BuildHost()
    {
        var appData = DependencyInjection.GetAppDataDirectory();
        var localOverride = Path.Combine(appData, "appsettings.Local.json");

        return Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((_, config) =>
            {
                config.SetBasePath(AppContext.BaseDirectory);
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                config.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
                config.AddJsonFile(localOverride, optional: true, reloadOnChange: true);
            })
            .UseSerilog((context, _, loggerConfiguration) =>
                DependencyInjection.ConfigureSerilog(loggerConfiguration, context.Configuration))
            .ConfigureServices((context, services) =>
            {
                services.AddInfrastructure(context.Configuration);
                services.AddZenIntegration();
                services.AddThorlabsIntegration();
                services.AddSingleton<CaptureViewModel>();
                services.AddTransient<SettingsViewModel>();
            })
            .Build();
    }

    private void ShowStartupError(string detail)
    {
        _window = new Window
        {
            Title = "Fiber Img App - Startup Error"
        };

        var scroll = new ScrollViewer
        {
            Padding = new Thickness(24),
            Content = new TextBlock
            {
                Text = detail,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                FontSize = 13
            }
        };

        _window.Content = scroll;
        _window.Activate();
    }

    private static string FlattenException(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            parts.Add($"{e.GetType().FullName}: {e.Message}");
        }

        return string.Join(Environment.NewLine + "--> ", parts)
               + Environment.NewLine + Environment.NewLine
               + ex;
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "Unhandled exception");
        Log.CloseAndFlush();
    }
}
