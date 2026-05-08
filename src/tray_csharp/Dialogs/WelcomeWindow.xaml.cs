// Stage 7.7 Surface 04 + Surface 10. Welcome dialog code-behind. Hosts
// AboutViewModel as a sub-context for the embedded About tab. Continue
// button persists welcome_seen if appropriate.

using System.Windows;
using System.Windows.Controls;
using MastersFM.Tray.Services;
using MastersFM.Tray.ViewModels;

namespace MastersFM.Tray.Dialogs;

public partial class WelcomeWindow : Window
{
    private readonly ILogger _logger;
    private readonly IConfigService _config;
    public AboutViewModel About { get; }

    public WelcomeWindow(ILogger logger, IConfigService config, AboutViewModel about)
    {
        _logger = logger;
        _config = config;
        About = about;
        InitializeComponent();
        // Activate About tab when VM signals showAboutTab=true
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (DataContext is WelcomeViewModel vm && vm.ShowAboutTab)
        {
            // Find TabControl and select About tab (index 1)
            if (FindVisualChild<TabControl>(this) is TabControl tc)
            {
                tc.SelectedIndex = 1;
            }
        }
    }

    private void OnContinueClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is WelcomeViewModel vm)
            {
                if (vm.IsFirstRun && vm.DontShowAgain)
                {
                    _config.SetWelcomeSeen(true);
                    _logger.Log("welcome_seen persisted via Continue + DontShowAgain", "Welcome");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogErr("Continue click", ex, "Welcome");
        }
        Close();
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var nested = FindVisualChild<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }
}
