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
    private readonly IActivityLog _log;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _positionTimer;
    private CancellationTokenSource? _runCts;
    private WriteableBitmap? _liveBitmap;
    private FullscreenImageWindow? _liveFullscreen;
    private int _previewBusy;
    private bool _subscribed;
    private int _positionPollInFlight;

    public CaptureViewModel(
        ICameraService camera,
        IThorlabsClient stage,
        ISamplingService sampling,
        ISessionExportService export,
        AppSettingsState settings,
        IOptions<ThorlabsOptions> thorlabsOptions,
        IOptions<CaptureOptions> captureOptions,
        IActivityLog log)
    {
        _camera = camera;
        _stage = stage;
        _sampling = sampling;
        _export = export;
        _settings = settings;
        _log = log;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _settings.LoadFrom(thorlabsOptions.Value, captureOptions.Value);
        EnsureFrameSubscription();
        RefreshStatus();

        _positionTimer = _dispatcher.CreateTimer();
        _positionTimer.Interval = TimeSpan.FromMilliseconds(200);
        _positionTimer.IsRepeating = true;
        _positionTimer.Tick += OnPositionTimerTick;
        _positionTimer.Start();
    }

    public ObservableCollection<ReviewImageItem> ReviewImages { get; } = new();

    [ObservableProperty]
    public partial ImageSource? PreviewImage { get; set; }

    [ObservableProperty]
    public partial string StageStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StagePositionDisplay { get; set; } = "NRT100: —";

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

    private void Trace(string message) => _log.Write("capture", message);

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
        UpdateStagePositionDisplay();
        UpdateSpacingSummary();
    }

    private void UpdateStagePositionDisplay()
    {
        StagePositionDisplay = _stage.IsConnected
            ? $"NRT100: {_stage.PositionMm:0.###} mm"
            : "NRT100: —";
    }

    private async void OnPositionTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (Interlocked.CompareExchange(ref _positionPollInFlight, 1, 0) != 0)
            return;

        try
        {
            if (_stage.IsConnected)
            {
                await _stage.RefreshPositionAsync().ConfigureAwait(true);
            }

            UpdateStagePositionDisplay();
            StageStatus = _stage.Status;
        }
        catch
        {
            // Keep last known display; transient XA reads should not surface as banners.
        }
        finally
        {
            Interlocked.Exchange(ref _positionPollInFlight, 0);
        }
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
        Trace("Connect camera...");
        try
        {
            var ok = await _camera.ConnectAsync();
            Trace(_camera.Status);
            BannerMessage = ok ? "Camera connected." : "Camera connect failed. Check Settings (ZEN).";
            if (!ok) Trace("Camera connect failed.");
        }
        catch (Exception ex)
        {
            BannerMessage = ex.Message;
            Trace($"Camera connect error: {ex.Message}");
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
        Trace("Start live...");
        try
        {
            if (!_camera.IsConnected)
            {
                Trace("Camera not connected; connecting first...");
                var ok = await _camera.ConnectAsync();
                Trace(_camera.Status);
                if (!ok)
                {
                    BannerMessage = "Could not connect camera.";
                    Trace("Camera connect failed before live.");
                    return;
                }
            }

            await _camera.StartLiveAsync();
            Trace(_camera.Status);
            ShowPlaceholderOverlay = false;

            // StartLive can succeed while the pixel stream never delivers frames.
            if (PreviewImage is null)
            {
                for (var i = 0; i < 20 && PreviewImage is null; i++)
                    await Task.Delay(100);

                if (PreviewImage is null)
                {
                    BannerMessage =
                        string.IsNullOrWhiteSpace(_camera.Status)
                            ? "Live started but no image yet. Check Activity log / stop Live in ZEN first."
                            : _camera.Status;
                    Trace($"No preview frame after Start live. Status={_camera.Status}");
                }
            }
        }
        catch (Exception ex)
        {
            BannerMessage = ex.Message;
            Trace($"Start live error: {ex.Message}");
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
            Trace("Stop live...");
            await _camera.StopLiveAsync();
            Trace(_camera.Status);
        }
        catch (Exception ex)
        {
            BannerMessage = ex.Message;
            Trace($"Stop live error: {ex.Message}");
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
        await Task.Yield();
        Trace("Connect stage...");
        try
        {
            await _stage.ApplyMotionParametersAsync(_settings.VelocityMmPerSec, _settings.AccelerationMmPerSec2).ConfigureAwait(true);
            var ok = await _stage.ConnectAsync().ConfigureAwait(true);
            Trace(_stage.Status);
            BannerMessage = ok ? "Stage connected." : "Could not connect. Check Settings / simulator.";
            if (!ok) Trace("Stage connect failed.");
        }
        catch (Exception ex)
        {
            BannerMessage = ex.Message;
            Trace($"Stage connect error: {ex.Message}");
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
        await Task.Yield();
        Trace("Home stage...");
        try
        {
            await _stage.HomeAsync().ConfigureAwait(true);
            ProgressMessage = $"Homed. Position {_stage.PositionMm:0.###} mm";
            Trace(ProgressMessage);
        }
        catch (Exception ex)
        {
            BannerMessage = ex.Message;
            Trace($"Home error: {ex.Message}");
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
        await Task.Yield();
        IsReviewVisible = false;
        ReviewImages.Clear();
        CurrentSession = null;
        Trace($"Begin sampling ({count} images, {_settings.MinMm:0.###}-{_settings.MaxMm:0.###} mm)...");
        _runCts = new CancellationTokenSource();
        var progress = new Progress<SamplingProgress>(p =>
        {
            ProgressMessage = p.Message;
            Trace(p.Message);
            RefreshStatus();
        });

        try
        {
            await _stage.ApplyMotionParametersAsync(_settings.VelocityMmPerSec, _settings.AccelerationMmPerSec2).ConfigureAwait(true);
            CurrentSession = await _sampling.RunAsync(
                count,
                _settings.MinMm,
                _settings.MaxMm,
                progress,
                _runCts.Token).ConfigureAwait(true);

            await ShowReviewAsync(CurrentSession).ConfigureAwait(true);
            ProgressMessage = $"Done — {CurrentSession.Images.Count} images. Double-click a tile to fullscreen.";
        }
        catch (SamplingIncompleteException ex)
        {
            CurrentSession = ex.Session;
            await ShowReviewAsync(ex.Session).ConfigureAwait(true);
            if (ex.InnerException is not OperationCanceledException)
                BannerMessage = ex.InnerException?.Message ?? ex.Message;
            ProgressMessage =
                $"{ex.Message} Double-click a tile to fullscreen — you can still save what was captured.";
        }
        catch (OperationCanceledException)
        {
            ProgressMessage = "Sampling cancelled.";
            Trace("Sampling cancelled.");
        }
        catch (Exception ex)
        {
            BannerMessage = ex.Message;
            ProgressMessage = "Sampling stopped due to an error.";
            Trace($"Sampling error: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            RefreshStatus();
        }
    }

    private async Task ShowReviewAsync(SamplingSession session)
    {
        ReviewImages.Clear();
        foreach (var image in session.Images)
        {
            ReviewImages.Add(new ReviewImageItem(image, await ToBitmapAsync(image.PngBytes).ConfigureAwait(true)));
        }

        IsReviewVisible = true;
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
            Trace(BannerMessage);
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
        if (Interlocked.Exchange(ref _previewBusy, 1) == 1)
            return;

        var frame = e.Frame;
        _ = Task.Run(() =>
        {
            MonoFrameEncoder.PreviewBitmap preview;
            try
            {
                preview = MonoFrameEncoder.ToPreviewBgra32(frame);
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _previewBusy, 0);
                _dispatcher.TryEnqueue(() => BannerMessage = $"Live frame error: {ex.Message}");
                return;
            }

            var queued = _dispatcher.TryEnqueue(() =>
            {
                try
                {
                    UpdateLiveBitmap(preview.Bgra, preview.Width, preview.Height);
                    ShowPlaceholderOverlay = false;
                    IsLive = true;
                    CameraStatus = _camera.Status;
                    _liveFullscreen?.SetImageSource(PreviewImage);
                }
                catch (Exception ex)
                {
                    BannerMessage = $"Live frame error: {ex.Message}";
                }
                finally
                {
                    Interlocked.Exchange(ref _previewBusy, 0);
                }
            });

            if (!queued)
                Interlocked.Exchange(ref _previewBusy, 0);
        });
    }

    private void UpdateLiveBitmap(byte[] bgra, int width, int height)
    {
        if (_liveBitmap is null || _liveBitmap.PixelWidth != width || _liveBitmap.PixelHeight != height)
        {
            _liveBitmap = new WriteableBitmap(width, height);
            PreviewImage = _liveBitmap;
        }

        using (var stream = _liveBitmap.PixelBuffer.AsStream())
        {
            stream.Write(bgra, 0, Math.Min(bgra.Length, (int)stream.Length));
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