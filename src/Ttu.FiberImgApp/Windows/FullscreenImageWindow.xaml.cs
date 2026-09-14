using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Windows.System;

namespace Ttu.FiberImgApp.Windows;

public sealed partial class FullscreenImageWindow : Window
{
    private bool _isPanning;
    private double _panStartX;
    private double _panStartY;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;
    private uint _panPointerId;

    public FullscreenImageWindow()
    {
        InitializeComponent();
        if (Content is FrameworkElement root)
        {
            root.KeyDown += OnKeyDown;
            root.Loaded += (_, _) => root.Focus(FocusState.Programmatic);
        }
    }

    public void SetTitle(string title) => TitleText.Text = title;

    public void SetImageSource(ImageSource? source) => ViewerImage.Source = source;

    public async Task SetPngAsync(byte[] pngBytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(pngBytes.AsBuffer());
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        ViewerImage.Source = bitmap;
    }

    private void ZoomViewer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ZoomViewer);
        if (point.PointerDeviceType == PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isPanning = true;
        _panPointerId = point.PointerId;
        _panStartX = point.Position.X;
        _panStartY = point.Position.Y;
        _panStartHorizontalOffset = ZoomViewer.HorizontalOffset;
        _panStartVerticalOffset = ZoomViewer.VerticalOffset;
        ZoomViewer.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ZoomViewer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPanning || e.Pointer.PointerId != _panPointerId)
        {
            return;
        }

        var point = e.GetCurrentPoint(ZoomViewer);
        var dx = point.Position.X - _panStartX;
        var dy = point.Position.Y - _panStartY;

        var newH = _panStartHorizontalOffset - dx;
        var newV = _panStartVerticalOffset - dy;

        ZoomViewer.ChangeView(newH, newV, null, disableAnimation: true);
        e.Handled = true;
    }

    private void ZoomViewer_PointerReleased(object sender, PointerRoutedEventArgs e) =>
        EndPan(e.Pointer);

    private void ZoomViewer_PointerCanceled(object sender, PointerRoutedEventArgs e) =>
        EndPan(e.Pointer);

    private void ZoomViewer_PointerCaptureLost(object sender, PointerRoutedEventArgs e) =>
        EndPan(e.Pointer);

    private void EndPan(Pointer pointer)
    {
        if (!_isPanning || pointer.PointerId != _panPointerId)
        {
            return;
        }

        _isPanning = false;
        try
        {
            ZoomViewer.ReleasePointerCapture(pointer);
        }
        catch
        {
            // Already released.
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key is VirtualKey.Add or VirtualKey.GamepadRightTrigger)
        {
            ZoomBy(1.25f);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Subtract)
        {
            ZoomBy(0.8f);
            e.Handled = true;
        }
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomBy(1.25f);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomBy(0.8f);
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => _ = ZoomViewer.ChangeView(null, null, 1f);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ZoomBy(float factor)
    {
        var next = Math.Clamp(ZoomViewer.ZoomFactor * factor, ZoomViewer.MinZoomFactor, ZoomViewer.MaxZoomFactor);
        ZoomViewer.ChangeView(null, null, next);
    }
}