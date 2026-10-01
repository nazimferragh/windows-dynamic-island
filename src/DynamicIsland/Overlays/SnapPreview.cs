using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicIsland.Interop;

namespace DynamicIsland.Overlays;

/// <summary>
/// The outline that shows where a dragged window will land when dropped on a snap zone in the
/// island. A click-through overlay over one monitor's work area; the outline glides between zones.
/// </summary>
internal sealed class SnapPreview : Window
{
    private static readonly Duration Glide = TimeSpan.FromMilliseconds(200);

    private readonly WindowApi.RECT _work;
    private readonly Canvas _canvas = new();
    private readonly Border _box = new() { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(2), Opacity = 0 };
    private IntPtr _hwnd;
    private bool _shown;

    public SnapPreview(WindowApi.RECT work, Color accent)
    {
        _work = work;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true; // tool-window style below hides the button; keeps it on the current desktop
        ShowActivated = false;
        Topmost = true;
        _box.BorderBrush = new SolidColorBrush(accent);
        _box.Background = new SolidColorBrush(Color.FromArgb(0x38, accent.R, accent.G, accent.B));
        _box.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Opacity = 0.35 };
        _canvas.Children.Add(_box);
        Content = _canvas;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            WindowApi.MakeClickThrough(_hwnd);
            WindowApi.PlaceTopmost(_hwnd, _work.Left, _work.Top, _work.Width, _work.Height);
        };
    }

    private double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>Shows (or moves) the outline to a zone, physical pixels.</summary>
    public void ShowZone(WindowApi.RECT zone)
    {
        if (!IsVisible) Show();
        const double inset = 6; // a little breathing room, like Windows' own preview
        double s = Scale;
        double x = (zone.Left - _work.Left) / s + inset, y = (zone.Top - _work.Top) / s + inset;
        double w = Math.Max(10, zone.Width / s - inset * 2), h = Math.Max(10, zone.Height / s - inset * 2);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        if (!_shown)
        {
            // First zone: grow in from slightly smaller, fading in.
            Canvas.SetLeft(_box, x + w * 0.04);
            Canvas.SetTop(_box, y + h * 0.04);
            _box.Width = w * 0.92;
            _box.Height = h * 0.92;
            _shown = true;
        }
        _box.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(x, Glide) { EasingFunction = ease });
        _box.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(y, Glide) { EasingFunction = ease });
        _box.BeginAnimation(WidthProperty, new DoubleAnimation(w, Glide) { EasingFunction = ease });
        _box.BeginAnimation(HeightProperty, new DoubleAnimation(h, Glide) { EasingFunction = ease });
        _box.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Glide));
    }

    /// <summary>Fades the outline out (the window stays ready for the next zone).</summary>
    public void HideZone()
    {
        if (!_shown) return;
        _shown = false;
        _box.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(140)));
    }
}
