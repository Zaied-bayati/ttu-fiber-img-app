using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ttu.FiberImgApp.ViewModels;

namespace Ttu.FiberImgApp.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
        Loaded += (_, _) => ZenTokenBox.Password = ViewModel.ZenApiToken ?? string.Empty;
    }

    private void ZenTokenBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        ViewModel.ZenApiToken = ZenTokenBox.Password;
    }
}