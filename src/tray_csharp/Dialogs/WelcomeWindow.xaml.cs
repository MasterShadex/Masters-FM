// Stage 7.7B STEP 3: WelcomeWindow first-run hero code-behind.
// Handles waveform bar creation + animation (OnLoaded), AppDialogStyle
// PART_ template wiring (OnApplyTemplate), and two action buttons.
//
// DialogResult semantics:
//   true  = Get Started -> App.xaml.cs opens SetupWizard
//   false = Skip Setup  -> App.xaml.cs does nothing; tray menu only
//
// Waveform: 32 bars created in C# to avoid 300+ lines of XAML repetition.
// Each bar uses DoubleAnimation on Canvas.BottomProperty (simulates grow-from-
// base) via HeightProperty + Canvas.SetBottom(bar, 0), phase-offset with
// a deterministic BeginTime so bars never all peak at once. When
// App.IsReducedMotion is true, bars are placed statically from seed 42.

using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace MastersFM.Tray.Dialogs;

public partial class WelcomeWindow : Window
{
    // Deterministic seed so reduced-motion static heights are consistent
    // across launches (user sees the same "frozen" waveform every time).
    private const int WaveformSeed = 42;
    private const int BarCount    = 28;   // 28 bars fit cleanly inside the 232-px Canvas
    private const int BarWidth    = 4;
    private const int BarGap      = 4;
    private const int BarMinH     = 8;
    private const int BarMaxH     = 48;
    private const int CanvasHeight = 56;

    public WelcomeWindow()
    {
        // Set a valid local Foreground before InitializeComponent() so the
        // style transition (implicit -> AppDialogStyle) never sees UnsetValue.
        // ThemesDictionary Dark may install an implicit Window style that uses
        // unresolved DynamicResource tokens for Foreground; a local value set
        // here has higher WPF property precedence than any style setter, making
        // the old-style invalidation safe. AppDialogStyle's Setter for Foreground
        // (TextPrimary) then takes effect normally via the new style.
        SetValue(ForegroundProperty, SystemColors.WindowTextBrush);

        InitializeComponent();
        Loaded += OnLoaded;
    }

    // -------------------------------------------------------------------------
    // Template parts (AppDialogStyle PART_ wiring)
    // -------------------------------------------------------------------------

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        // PART_TitleBar: enable drag for the WindowStyle=None chrome.
        if (GetTemplateChild("PART_TitleBar") is FrameworkElement titleBar)
        {
            titleBar.MouseLeftButtonDown += OnTitleBarDrag;
        }

        // PART_CloseButton: treat as Skip Setup (DialogResult=false).
        if (GetTemplateChild("PART_CloseButton") is Button closeBtn)
        {
            closeBtn.Click += (_, _) =>
            {
                DialogResult = false;
            };
        }

        // PART_MinimizeButton stays Collapsed (default in AppDialogStyle).
    }

    // -------------------------------------------------------------------------
    // Loaded: build waveform bars
    // -------------------------------------------------------------------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Always build the waveform — operator likes the brand panel visible
        // in patch-notes mode too.  ClipToBounds=True on the Border keeps any
        // bar overflow inside its column.
        BuildWaveform();

        // Stamp the actual running version on the brand-panel caption.
        // Reads AssemblyInformationalVersion (csproj <Version>), stripped of
        // any "+build.metadata", same logic ConfigService uses internally.
        var versionLabel = "v" + GetAppVersion();
        if (VersionText != null) VersionText.Text = versionLabel;

        // Stage 7.12 Issue 6: if opened as patch-notes view, hide the
        // first-run welcome copy + action buttons and show the patch notes
        // panel instead.  Window title becomes "You are currently running
        // version v..." so the operator can see at a glance which build
        // they're looking at.
        if (DataContext is ViewModels.WelcomeViewModel vm && vm.ShowAboutTab)
        {
            WelcomeContentScroller.Visibility = Visibility.Collapsed;
            WelcomeActionButtons.Visibility   = Visibility.Collapsed;
            PatchNotesPanel.Visibility        = Visibility.Visible;
            Title = $"You are currently running version {versionLabel}";
        }
    }

    private static string GetAppVersion()
    {
        var asm  = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            var plus = info.IndexOf('+');
            return plus >= 0 ? info.Substring(0, plus) : info;
        }
        var ver = asm.GetName().Version;
        return ver != null ? ver.ToString() : "0.0.0";
    }

    private void BuildWaveform()
    {
        var canvas = WaveformCanvas;
        if (canvas == null) return;

        // Resolve brand purple glow brush from resources (may not exist in designer).
        Brush barBrush;
        try
        {
            var baseBrush = (SolidColorBrush)FindResource("BrandPurpleGlow");
            barBrush = new SolidColorBrush(baseBrush.Color) { Opacity = 0.7 };
        }
        catch
        {
            barBrush = new SolidColorBrush(Color.FromRgb(0xA7, 0x8B, 0xFA)) { Opacity = 0.7 };
        }

        var rng = new Random(WaveformSeed);

        // Centre the whole bar group horizontally in the Canvas so there's
        // equal breathing room on the left and right edges (rather than the
        // bars being left-anchored and bleeding past the right side).
        double barGroupWidth = BarCount * BarWidth + (BarCount - 1) * BarGap;
        double offsetX = Math.Max(0, (canvas.Width - barGroupWidth) / 2.0);

        for (int i = 0; i < BarCount; i++)
        {
            int startH = rng.Next(BarMinH, BarMaxH + 1);
            int endH   = rng.Next(BarMinH, BarMaxH + 1);

            var bar = new Border
            {
                Width           = BarWidth,
                Height          = startH,
                Background      = barBrush,
                CornerRadius    = new CornerRadius(2, 2, 0, 0),
                VerticalAlignment = VerticalAlignment.Bottom
            };

            // Position bar on canvas: centred bar group, pinned to canvas bottom.
            Canvas.SetLeft(bar, offsetX + i * (BarWidth + BarGap));
            Canvas.SetBottom(bar, 0);
            canvas.Children.Add(bar);

            if (App.IsReducedMotion) continue;

            // Animate Height from startH to endH, auto-reverse, repeat forever.
            // BeginTime phase-offsets each bar so they don't all peak together.
            int durationMs = rng.Next(300, 801);
            int beginMs    = rng.Next(0, 500);

            var anim = new DoubleAnimation
            {
                From            = startH,
                To              = endH,
                Duration        = new Duration(TimeSpan.FromMilliseconds(durationMs)),
                AutoReverse     = true,
                RepeatBehavior  = RepeatBehavior.Forever,
                BeginTime       = TimeSpan.FromMilliseconds(beginMs),
                EasingFunction  = new SineEase { EasingMode = EasingMode.EaseInOut }
            };

            bar.BeginAnimation(HeightProperty, anim);
        }
    }

    // -------------------------------------------------------------------------
    // Action buttons
    // -------------------------------------------------------------------------

    private void OnGetStarted(object sender, RoutedEventArgs e)
    {
        // true -> App.xaml.cs interprets as "open Setup Wizard".
        DialogResult = true;
    }

    private void OnSkipSetup(object sender, RoutedEventArgs e)
    {
        // false -> App.xaml.cs interprets as "go straight to tray menu".
        DialogResult = false;
    }

    // -------------------------------------------------------------------------
    // Drag support (also wired from PART_TitleBar in OnApplyTemplate)
    // -------------------------------------------------------------------------

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    // -------------------------------------------------------------------------
    // Keyboard: Escape closes (Skip Setup semantics)
    // -------------------------------------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
        base.OnKeyDown(e);
    }
}
