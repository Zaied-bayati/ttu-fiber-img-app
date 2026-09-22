using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using Ttu.FiberImgApp.Core.Abstractions;
using Ttu.FiberImgApp.Core.Configuration;
using Ttu.FiberImgApp.Infrastructure;

namespace Ttu.FiberImgApp.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IThorlabsClient _stage;
    private readonly ICameraService _camera;
    private readonly AppSettingsState _settings;
    private readonly ThorlabsOptions _thorlabsOptions;
    private readonly CaptureOptions _captureOptions;
    private readonly ZenOptions _zenOptions;
    private readonly IActivityLog _log;

    public SettingsViewModel(
        IThorlabsClient stage,
        ICameraService camera,
        AppSettingsState settings,
        IOptions<ThorlabsOptions> thorlabsOptions,
        IOptions<CaptureOptions> captureOptions,
        IOptions<ZenOptions> zenOptions,
        IActivityLog log)
    {
        _stage = stage;
        _camera = camera;
        _settings = settings;
        _thorlabsOptions = thorlabsOptions.Value;
        _captureOptions = captureOptions.Value;
        _zenOptions = zenOptions.Value;
        _log = log;
        _settings.LoadFrom(_thorlabsOptions, _captureOptions);
        LoadFromState();
        StageStatus = _stage.Status;
        CameraStatus = _camera.Status;
    }

    [ObservableProperty]
    public partial bool UseSimulator { get; set; }

    [ObservableProperty]
    public partial string SerialNumber { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double Channel { get; set; } = 1;

    [ObservableProperty]
    public partial double MinMm { get; set; }

    [ObservableProperty]
    public partial double MaxMm { get; set; }

    [ObservableProperty]
    public partial double VelocityMmPerSec { get; set; }

    [ObservableProperty]
    public partial double AccelerationMmPerSec2 { get; set; }

    [ObservableProperty]
    public partial double JogStepMm { get; set; }

    [ObservableProperty]
    public partial string SessionsRootPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double GoToMm { get; set; }

    [ObservableProperty]
    public partial string StageStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CameraStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Message { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool ZenUseSimulator { get; set; } = true;

    [ObservableProperty]
    public partial string ZenHost { get; set; } = "localhost";

    [ObservableProperty]
    public partial double ZenPort { get; set; } = 5002;

    [ObservableProperty]
    public partial string ZenExperimentName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ZenApiToken { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ZenCertificatePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ZenAllowUntrustedCertificate { get; set; }

    private void LoadFromState()
    {
        UseSimulator = _settings.UseSimulator;
        SerialNumber = _settings.SerialNumber;
        Channel = _settings.Channel;
        MinMm = _settings.MinMm;
        MaxMm = _settings.MaxMm;
        VelocityMmPerSec = _settings.VelocityMmPerSec;
        AccelerationMmPerSec2 = _settings.AccelerationMmPerSec2;
        JogStepMm = _settings.JogStepMm;
        SessionsRootPath = string.IsNullOrWhiteSpace(_settings.SessionsRootPath)
            ? AppSettingsState.GetDefaultSessionsRoot()
            : _settings.SessionsRootPath;
        GoToMm = _settings.MinMm;

        ZenUseSimulator = _zenOptions.UseSimulator;
        ZenHost = _zenOptions.Host;
        ZenPort = _zenOptions.Port;
        ZenExperimentName = _zenOptions.ExperimentName;
        ZenApiToken = _zenOptions.ApiToken;
        ZenCertificatePath = _zenOptions.CertificatePath;
        ZenAllowUntrustedCertificate = _zenOptions.AllowUntrustedCertificate;
    }

    private void PushToState()
    {
        _settings.UseSimulator = UseSimulator;
        _settings.SerialNumber = SerialNumber;
        _settings.Channel = (int)Math.Round(Channel);
        _settings.MinMm = MinMm;
        _settings.MaxMm = MaxMm;
        _settings.VelocityMmPerSec = VelocityMmPerSec;
        _settings.AccelerationMmPerSec2 = AccelerationMmPerSec2;
        _settings.JogStepMm = JogStepMm;
        _settings.SessionsRootPath = SessionsRootPath;
        _settings.CopyTo(_thorlabsOptions, _captureOptions);

        _zenOptions.UseSimulator = ZenUseSimulator;
        _zenOptions.Host = ZenHost;
        _zenOptions.Port = (int)Math.Round(ZenPort);
        _zenOptions.ExperimentName = ZenExperimentName;
        _zenOptions.ApiToken = ZenApiToken;
        _zenOptions.CertificatePath = ZenCertificatePath;
        _zenOptions.AllowUntrustedCertificate = ZenAllowUntrustedCertificate;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        PushToState();
        try
        {
            await _stage.ApplyMotionParametersAsync(VelocityMmPerSec, AccelerationMmPerSec2);
            await WriteLocalSettingsFileAsync();
            Message = "Settings saved to LocalAppData. Restart the app after changing simulator / ZEN host options.";
            _log.Write("settings", Message);
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }

        StageStatus = _stage.Status;
        CameraStatus = _camera.Status;
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        PushToState();
        IsBusy = true;
        Message = string.Empty;
        _log.Write("settings", "Connect stage...");
        try
        {
            await _stage.ApplyMotionParametersAsync(VelocityMmPerSec, AccelerationMmPerSec2);
            var ok = await _stage.ConnectAsync();
            Message = ok ? "Stage connected." : "Stage connect failed — see status.";
            _log.Write("settings", $"{Message} | {_stage.Status}");
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
        finally
        {
            IsBusy = false;
            StageStatus = _stage.Status;
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await _stage.DisconnectAsync();
        StageStatus = _stage.Status;
        Message = "Stage disconnected.";
    }

    [RelayCommand]
    private async Task ConnectCameraAsync()
    {
        PushToState();
        IsBusy = true;
        _log.Write("settings", "Connect camera...");
        try
        {
            var ok = await _camera.ConnectAsync();
            Message = ok ? "Camera connected." : "Camera connect failed — check ZEN settings / restart if you flipped simulator.";
            _log.Write("settings", $"{Message} | {_camera.Status}");
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
        finally
        {
            IsBusy = false;
            CameraStatus = _camera.Status;
        }
    }

    [RelayCommand]
    private async Task DisconnectCameraAsync()
    {
        await _camera.DisconnectAsync();
        CameraStatus = _camera.Status;
        Message = "Camera disconnected.";
    }

    [RelayCommand]
    private async Task HomeAsync()
    {
        IsBusy = true;
        try
        {
            await _stage.HomeAsync();
            Message = $"Homed to {_stage.PositionMm:0.###} mm";
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
        finally
        {
            IsBusy = false;
            StageStatus = _stage.Status;
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        await _stage.StopAsync();
        Message = "Stop requested.";
        StageStatus = _stage.Status;
    }

    [RelayCommand]
    private async Task JogAsync(string? direction)
    {
        PushToState();
        var delta = string.Equals(direction, "-", StringComparison.Ordinal) ? -JogStepMm : JogStepMm;
        var target = Math.Clamp(_stage.PositionMm + delta, MinMm, MaxMm);
        await MoveToAsync(target);
    }

    [RelayCommand]
    private async Task GoToAsync()
    {
        PushToState();
        await MoveToAsync(Math.Clamp(GoToMm, MinMm, MaxMm));
    }

    private async Task MoveToAsync(double mm)
    {
        IsBusy = true;
        Message = string.Empty;
        try
        {
            await _stage.ApplyMotionParametersAsync(VelocityMmPerSec, AccelerationMmPerSec2);
            await _stage.MoveAbsoluteAsync(mm);
            Message = $"At {_stage.PositionMm:0.###} mm";
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
        finally
        {
            IsBusy = false;
            StageStatus = _stage.Status;
        }
    }

    public async Task WriteLocalSettingsFileAsync()
    {
        PushToState();
        var dir = DependencyInjection.GetAppDataDirectory();
        var path = Path.Combine(dir, "appsettings.Local.json");
        var doc = new
        {
            Zen = new
            {
                UseSimulator = _zenOptions.UseSimulator,
                Host = _zenOptions.Host,
                Port = _zenOptions.Port,
                ApiToken = _zenOptions.ApiToken,
                ExperimentName = _zenOptions.ExperimentName,
                CertificatePath = _zenOptions.CertificatePath,
                ChannelIndex = _zenOptions.ChannelIndex,
                AllowUntrustedCertificate = _zenOptions.AllowUntrustedCertificate,
            },
            Thorlabs = new
            {
                _settings.UseSimulator,
                _settings.SerialNumber,
                _settings.Channel,
                _settings.MinMm,
                _settings.MaxMm,
                _settings.VelocityMmPerSec,
                _settings.AccelerationMmPerSec2,
                _settings.JogStepMm
            },
            Capture = new
            {
                SessionsRootPath = _settings.SessionsRootPath
            }
        };

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
    }
}