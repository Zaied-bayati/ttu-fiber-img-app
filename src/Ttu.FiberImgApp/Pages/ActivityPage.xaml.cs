using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ttu.FiberImgApp.Core.Abstractions;
using Windows.ApplicationModel.DataTransfer;

namespace Ttu.FiberImgApp.Pages;

public sealed partial class ActivityPage : Page
{
    private readonly IActivityLog _activityLog;
    private readonly DispatcherQueue _dispatcher;

    public ActivityPage()
    {
        _activityLog = App.Services.GetRequiredService<IActivityLog>();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ActivityLogBox.Text = _activityLog.Text;
        ScrollToEnd();
        _activityLog.Changed += OnActivityLogChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _activityLog.Changed -= OnActivityLogChanged;
    }

    private void OnActivityLogChanged(object? sender, EventArgs e)
    {
        _dispatcher.TryEnqueue(() =>
        {
            ActivityLogBox.Text = _activityLog.Text;
            ScrollToEnd();
        });
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        var text = string.IsNullOrWhiteSpace(ActivityLogBox.SelectedText)
            ? _activityLog.Text
            : ActivityLogBox.SelectedText;

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        _activityLog.Write("shell", "Copied activity log to clipboard.");
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _activityLog.Clear();
        _activityLog.Write("shell", "Log cleared.");
    }

    private void ScrollToEnd()
    {
        ActivityLogBox.Select(ActivityLogBox.Text.Length, 0);
    }
}
