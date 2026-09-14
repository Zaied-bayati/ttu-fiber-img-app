using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Ttu.FiberImgApp.ViewModels;

namespace Ttu.FiberImgApp.Pages;

public sealed partial class CapturePage : Page
{
    public CaptureViewModel ViewModel { get; }

    public CapturePage()
    {
        ViewModel = App.Services.GetRequiredService<CaptureViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CaptureViewModel.IsReviewVisible))
            {
                ReviewPanel.Visibility = ViewModel.IsReviewVisible ? Visibility.Visible : Visibility.Collapsed;
            }

            if (e.PropertyName is nameof(CaptureViewModel.ShowPlaceholderOverlay))
            {
                PlaceholderOverlay.Visibility = ViewModel.ShowPlaceholderOverlay
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        };
        ReviewPanel.Visibility = Visibility.Collapsed;
        PlaceholderOverlay.Visibility = Visibility.Visible;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshStatus();
        PlaceholderOverlay.Visibility = ViewModel.ShowPlaceholderOverlay
            ? Visibility.Visible
            : Visibility.Collapsed;
        await ViewModel.LoadPreviewCommand.ExecuteAsync(null);
    }

    private void DeleteImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ReviewImageItem item })
        {
            ViewModel.DeleteReviewImageCommand.Execute(item);
        }
    }

    private void Preview_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        ViewModel.OpenLiveFullscreenCommand.Execute(null);
    }

    private void ReviewItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ReviewImageItem item })
        {
            ViewModel.OpenReviewFullscreenCommand.Execute(item);
        }
    }
}