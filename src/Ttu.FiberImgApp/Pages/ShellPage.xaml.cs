using Microsoft.UI.Xaml.Controls;

namespace Ttu.FiberImgApp.Pages;

public sealed partial class ShellPage : Page
{
    public ShellPage()
    {
        InitializeComponent();
        NavView.SelectedItem = NavView.MenuItems[0];
        ContentFrame.Navigate(typeof(CapturePage));
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            switch (tag)
            {
                case "capture":
                    ContentFrame.Navigate(typeof(CapturePage));
                    break;
                case "settings":
                    ContentFrame.Navigate(typeof(SettingsPage));
                    break;
                case "activity":
                    ContentFrame.Navigate(typeof(ActivityPage));
                    break;
            }
        }
    }
}
