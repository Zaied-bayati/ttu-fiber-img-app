using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Ttu.FiberImgApp.Core.Abstractions;
using Ttu.FiberImgApp.Core.Configuration;
using Ttu.FiberImgApp.Core.Imaging;
using Ttu.FiberImgApp.Core.Models;
using Ttu.FiberImgApp.Core.Sampling;
using Ttu.FiberImgApp.Windows;
using Windows.Storage.Streams;

namespace Ttu.FiberImgApp.ViewModels;

public partial class CaptureViewModel : ObservableObject
{
    private readonly ICameraService _camera;
    private readonly IThorlabsClient _stage;
    private readonly ISamplingService _sampling;
    private readonly ISessionExportService _export;
    private readonly AppSettingsState _settings;
    private readonly DispatcherQueue _dispatcher;
    private CancellationTokenSource? _runCts;
    private WriteableBitmap? _liveBitmap;
    private DateTime _lastUiFrameUtc = DateTime.MinValue;
    private FullscreenImageWindow? _liveFullscreen;
    private bool _subscribed;

    public CaptureViewModel(
        ICameraService camera,
        IThorlabsClient stage,
        ISamplingService sampling,
        ISessionExportService export,
        AppSettingsState settings,
        IOptions<ThorlabsOptions> thorlabsOptions,
        IOptions<CaptureOptions> captureOptions)
    {
        _camera = camera;
        _stage = stage;
        _sampling = sampling;
        _export = export;
        _settings = settings;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _settings.LoadFrom(thorlabsOptions.Value, captureOptions.Value);
        EnsureFrameSubscription();
        RefreshStatus();
    }

    public ObservableCollection<ReviewImageItem> ReviewImages { get; } = new();

    [ObservableProperty]
    public partial ImageSource? PreviewImage { get; set; }

    [ObservableProperty]
    public partial string StageStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CameraStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProgressMessage { get; set; } = "Ready when you are.";

    [ObservableProperty]
    public partial string BannerMessage { get; set; } = string.Empty;

    public bool HasBanner => !string.IsNullOrWhiteSpace(BannerMessage);

    partial void OnBannerMessageChanged(string value) => OnPropertyChanged(nameof(HasBanner));

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsReviewVisible { get; set; }

    [ObservableProperty]
    public partial bool IsLive { get; set; }

    [ObservableProperty]
    public partial bool ShowPlaceholderOverlay { get; set; } = true;

    [ObservableProperty]
    public partial double ImageCount { get; set; } = 10;

    [ObservableProperty]
    public partial string SpacingSummary { get; set; } = string.Empty;

    public SamplingSession? CurrentSession { get; private set; }

    partial void OnImageCountChanged(double value) => UpdateSpacingSummary();

    private void EnsureFrameSubscription()
    {
        if (_subscribed) return;
        _camera.FrameReceived += OnFrameReceived;
        _subscribed = true;
    }

    public void RefreshStatus()
    {
        StageStatus = _stage.Status;
        CameraStatus = _camera.Status;
        IsLive = _camera.IsLive;
        ShowPlaceholderOverlay = !_camera.IsLive && PreviewImage is null;
        UpdateSpacingSummary();
    }

    private void UpdateSpacingSummary()
    {
        try
        {
            var count = Math.Max(2, (int)Math.Round(ImageCount));
            var positions = SamplingCalculator.ComputePositions(_settings.MinMm, _settings.MaxMm, count);
            var step = positions.Count > 1 ? positions[1] - positions[0] : 0;
            SpacingSummary =
                $"Will take {count} images from {_settings.MinMm:0.###} to {_settings.MaxMm:0.###} mm " +
                $"(about {step:0.###} mm apart).";
        }
        catch (Exception ex)
        {
            SpacingSummary = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ConnectCameraAsync()
    {
        BannerMessage = string.Empty;
        IsBusy = true;
        try
        {
            var ok = await _camera.ConnectAsync();
            BannerMessage = ok ? "Camera connected." : "Camera connect failed. Check Settings (ZEN).";
        }
        catch (Exception ex)
        {
            BannerMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            RefreshStatus();
        }
    }

    [RelayCommand]
    private async Task StartLiveAsync()
    {
        BannerMessage = string.Empty;
        IsBusy = true;
        try
        {
            if (!_camera.IsConnected)
            {
                var ok = await _camera.ConnectAsync();
                if (!ok)
                {
                    BannerMessage = "Could not connect camera.";
                    return;
                }
            }

            await _camera.StartLiveAsync();
            ShowPlaceholderOverlay = false;
        }
        catch (Exception ex)
        {
            BannerMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            RefreshStatus();
        }
    }

    [RelayCommand]
    private async Task StopLiveAsync()
    {
        try
        {
            await _camera.StopLiveAsync();
        }
        catch (Exception ex)
        {
            BannerMessage = ex.Message;
        }
        finally
        {
            RefreshStatus();
        }
    }

    [RelayCommand]
    private async Task LoadPreviewAsync()
    {
        try
        {
            if (!_camera.IsConnected)
                await _camera.ConnectAsync();

            if (_camera.IsLive)
            {
                ShowPlaceholderOverlay = false;
                return;
            }

            var bytes = await _camera.GetPreviewFrameAsync();
            PreviewImage = await ToBitmapAsync(bytes);
            ShowPlaceholderOverlay = false;
            CameraStatus = _camera.Status;
        }
        catch
        {
            // Simulator needs Start Live first for continuous frames; ignore cold start.
        }
    }

    [RelayCommand]
    private void OpenLiveFullscreen()
    {
        if (PreviewImage is null)
        {
            BannerMessage = "No live image yet. Start live first.";
            return;
        }

        _liveFullscreen ??= new FullscreenImageWindow();
        _liveFullscreen.SetTitle("Live camera");
        _liveFullscreen.SetImageSource(PreviewImage);
        _liveFullscreen.Activate();
        _liveFullscreen.Closed += (_, _) => _liveFullscreen = null;
    }

    [RelayCommand]
    private void OpenReviewFullscreen(ReviewImageItem? item)
    {
        if (item is null) return;
        var window = new FullscreenImageWindow();
        window.SetTitle(item.Caption);
        window.SetImageSource(item.Thumbnail);
        window.Activate();
    }

    [RelayCommand]
    private async Task ConnectStageAsync()
    {
        BannerMessage = string.Empty;
        IsBusy = true;
        try
        {
            await _stage.ApplyMotionParametersAsync(_settings.VelocityMmPerSec, _settings.AccelerationMmPerSec2);
            var ok = await _stage.ConnectAsync();
            BannerMessage = ok ? "Stage connected." : "Could not connect. Check Settings / simulator.";
        }
        catch (Exception ex)
        {
            BannerMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            RefreshStatus();
        }
    }

    [RelayCommand]
    private async Task HomeStageAsync()
    {
        BannerMessage = string.Empty;
        IsBusy = true;
        try
        {
            await _stage.HomeAsync();
            ProgressMessage = $"Homed. Position {_stage.PositionMm:0.###} mm";
        }
        catch (Exception ex)
        {
            BannerMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            RefreshStatus();
        }
    }

    [RelayCommand]
    private async Task BeginSamplingAsync()
    {
        BannerMessage = string.Empty;
        if (!_stage.IsConnected)
        {
            BannerMessage = "Connect the stage first (button above or Settings).";
            return;
        }

        if (!_camera.IsConnected)
        {
            var ok = await _camera.ConnectAsync();
            if (!ok)
            {
                BannerMessage = "Connect the camera first.";
                return;
            }
        }

        if (!_camera.IsLive)
        {
            try { await _camera.StartLiveAsync(); }
            catch (Exception ex)
            {
                BannerMessage = $"Need live camera before sampling: {ex.Message}";
                return;
            }
        }

        var count = Math.Max(2, (int)Math.Round(ImageCount));
        IsBusy = true;
        IsReviewVisible = false;
        ReviewImages.Clear();
        CurrentSession = null;
        _runCts = new CancellationTokenSource();
        var progress = new Progress<SamplingProgress>(p =>
        {
            ProgressMessage = p.Message;
            RefreshStatus();
        });

        try
        {
            await _stage.ApplyMotionParametersAsync(_settings.VelocityMmPerSec, _settings.AccelerationMmPerSec2);
            CurrentSession = await _sampling.RunAsync(
                count,
                _settings.MinMm,
                _settings.MaxMm,
                progress,
                _runCts.Token);

            foreach (var image in CurrentSession.Images)
            {
                ReviewImages.Add(new ReviewImageItem(image, await ToBitmapAsync(image.PngBytes)));
            }

            IsReviewVisible = true;
            ProgressMessage = $"Done — {CurrentSession.Images.Count} images. Double-click a tile to fullscreen.";
        }
        catch (OperationCanceledException)
        {
            ProgressMessage = "Sampling cancelled.";
        }
        catch (Exception ex)
        {
            BannerMessage = ex.Message;
            ProgressMessage = "Sampling stopped due to an error.";
        }
        finally
        {
            IsBusy = false;
            RefreshStatus();
        }
    }

    [RelayCommand]
    private void CancelSampling()
    {
        _runCts?.Cancel();
        _ = _stage.StopAsync();
    }

    [RelayCommand]
    private void DeleteReviewImage(ReviewImageItem? item)
    {
        if (item is null) return;
        item.Model.Keep = false;
        ReviewImages.Remove(item);
    }

    [RelayCommand]
    private async Task SaveSessionAsync()
    {
        if (CurrentSession is null)
        {
            BannerMessage = "Nothing to save yet.";
            return;
        }

        foreach (var item in ReviewImages)
            item.Model.Keep = true;

        IsBusy = true;
        try
        {
            var keptIds = ReviewImages.Select(r => r.Model.Id).ToHashSet();
            foreach (var image in CurrentSession.Images)
                image.Keep = keptIds.Contains(image.Id);

            var folder = await _export.SaveSessionAsync(CurrentSession);
            BannerMessage = $"Saved to: {folder}";
            ProgressMessage = "Session saved.";
        }
        catch (Exception ex)
        {
            BannerMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void DiscardSession()
    {
        CurrentSession = null;
        ReviewImages.Clear();
        IsReviewVisible = false;
        ProgressMessage = "Session discarded.";
        BannerMessage = string.Empty;
    }

    private void OnFrameReceived(object? sender, CameraFrameEventArgs e)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastUiFrameUtc).TotalMilliseconds < 70)
            return;
        _lastUiFrameUtc = now;

        var frame = e.Frame;
        _dispatcher.TryEnqueue(() =>
        {
            try
            {
                UpdateLiveBitmap(frame);
                ShowPlaceholderOverlay = false;
                IsLive = true;
                CameraStatus = _camera.Status;
                _liveFullscreen?.SetImageSource(PreviewImage);
            }
            catch (Exception ex)
            {
                BannerMessage = $"Live frame error: {ex.Message}";
            }
        });
    }

    private void UpdateLiveBitmap(CameraFrame frame)
    {
        var bgra = MonoFrameEncoder.ToBgra32(frame);
        if (_liveBitmap is null || _liveBitmap.PixelWidth != frame.Width || _liveBitmap.PixelHeight != frame.Height)
        {
            _liveBitmap = new WriteableBitmap(frame.Width, frame.Height);
            PreviewImage = _liveBitmap;
        }

        using (var stream = _liveBitmap.PixelBuffer.AsStream())
        {
            stream.Write(bgra, 0, bgra.Length);
        }

        _liveBitmap.Invalidate();
    }

    private static async Task<BitmapImage> ToBitmapAsync(byte[] pngBytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(pngBytes.AsBuffer());
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }
}

public sealed partial class ReviewImageItem : ObservableObject
{
    public ReviewImageItem(CapturedImage model, BitmapImage thumbnail)
    {
        Model = model;
        Thumbnail = thumbnail;
        Caption = $"#{model.Index}  @ {model.PositionMm:0.###} mm";
    }

    public CapturedImage Model { get; }
    public BitmapImage Thumbnail { get; }

    [ObservableProperty]
    public partial string Caption { get; set; }
}