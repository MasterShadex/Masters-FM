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
    private const int BarCount    = 32;
    private const int BarWidth    = 4;
    private const int BarGap      = 4;
    private const int BarMinH     = 8;
    private const int BarMaxH     = 48;
    private const int CanvasHeight = 56;

    public WelcomeWindow()
    {
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
        BuildWaveform();
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

            // Position bar on canvas: left edge + gap, pinned to canvas bottom.
            Canvas.SetLeft(bar, i * (BarWidth + BarGap));
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
}
