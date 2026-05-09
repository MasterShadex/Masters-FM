// Stage 7.7B STEP 5: PlatformsWindow code-behind.
// Visual rebuild. Injects NowPlayingViewModel for the right-column Now Playing
// card. PART_ template parts wired in OnApplyTemplate. Platform toggle logic
// unchanged (still stored via PlatformsViewModel.IsEnabled TwoWay binding).

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MastersFM.Tray.ViewModels;

namespace MastersFM.Tray.Dialogs;

public partial class PlatformsWindow : Window
{
    /// <summary>
    /// Now Playing data for the right-column card. Bound in XAML via
    /// RelativeSource AncestorType=Window bindings.
    /// </summary>
    public NowPlayingViewModel NowPlaying { get; }

    public PlatformsWindow(NowPlayingViewModel nowPlaying)
    {
        NowPlaying = nowPlaying;
        InitializeComponent();
    }

    // -------------------------------------------------------------------------
    // Template parts (AppDialogStyle PART_ wiring)
    // -------------------------------------------------------------------------

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (GetTemplateChild("PART_TitleBar") is FrameworkElement titleBar)
            titleBar.MouseLeftButtonDown += OnTitleBarDrag;

        if (GetTemplateChild("PART_CloseButton") is Button closeBtn)
            closeBtn.Click += (_, _) => Close();
    }

    // -------------------------------------------------------------------------
    // Drag
    // -------------------------------------------------------------------------

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }
}
