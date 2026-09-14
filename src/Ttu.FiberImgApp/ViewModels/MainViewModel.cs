using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using Ttu.FiberImgApp.Core.Abstractions;
using Ttu.FiberImgApp.Core.Configuration;

namespace Ttu.FiberImgApp.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IZenClient _zenClient;
    private readonly IThorlabsClient _thorlabsClient;
    private readonly UpdatesOptions _updatesOptions;

    public MainViewModel(
        IZenClient zenClient,
        IThorlabsClient thorlabsClient,
        IOptions<UpdatesOptions> updatesOptions)
    {
        _zenClient = zenClient;
        _thorlabsClient = thorlabsClient;
        _updatesOptions = updatesOptions.Value;
        RefreshStatus();
    }

    public string Title { get; } = "Fiber Img App";

    [ObservableProperty]
    public partial string ZenStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ThorlabsStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string UpdateUri { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Use Capture for live camera + sampling. Configure ZEN in Settings.";

    [RelayCommand]
    private void RefreshStatus()
    {
        ZenStatus = _zenClient.Status;
        ThorlabsStatus = _thorlabsClient.Status;
        UpdateUri = _updatesOptions.AppInstallerUri;
    }

    [RelayCommand]
    private async Task ConnectZenAsync()
    {
        StatusMessage = "Connecting to ZEN...";
        var ok = await _zenClient.ConnectAsync();
        StatusMessage = ok ? "ZEN connected." : "ZEN not connected (check Settings / Gateway).";
        RefreshStatus();
    }

    [RelayCommand]
    private async Task ConnectThorlabsAsync()
    {
        StatusMessage = "Connecting to Thorlabs...";
        var ok = await _thorlabsClient.ConnectAsync();
        StatusMessage = ok ? "Thorlabs connected." : "Thorlabs not connected (check Settings / simulator).";
        RefreshStatus();
    }
}