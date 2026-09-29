using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using DynamicIsland.Interop;

namespace DynamicIsland.Overlays;

/// <summary>
/// A click-through overlay that sits exactly on top of a window's snapshot and then swirls it
/// into the notch, like it's being pulled into a black hole. The real window is hidden underneath.
/// </summary>
internal sealed class AbsorbAnimation : Window
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(460);

    private readonly WindowApi.RECT _windowRect;
    private readonly WindowApi.RECT _bounds;
    private readonly Point _target;
    private readonly FrameworkElement _visual;
    private IntPtr _hwnd;

    /// <param name="windowRect">Visible bounds of the window, physical pixels.</param>
    /// <param name="target">Point to swallow it into (the notch), physical pixels.</param>
    /// <param name="dpiScale">Scale of the monitor it's on, used for the initial size.</param>
    public AbsorbAnimation(BitmapSource? snapshot, WindowApi.RECT windowRect, Point target, double dpiScale)
    {
        _windowRect = windowRect;
        _target = target;
        const int pad = 40;
        _bounds = new WindowApi.RECT
        {
            Left = (int)Math.Min(windowRect.Left, target.X) - pad,
            Top = (int)Math.Min(windowRect.Top, target.Y) - pad,
            Right = (int)Math.Max(windowRect.Right, target.X) + pad,
            Bottom = (int)Math.Max(windowRect.Bottom, target.Y) + pad,
        };

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = _bounds.Width / dpiScale;
        Height = _bounds.Height / dpiScale;

        _visual = snapshot != null
            ? new Image { Source = snapshot, Stretch = Stretch.Fill }
            : new Border { Background = new SolidColorBrush(Color.FromRgb(28, 28, 30)), CornerRadius = new CornerRadius(12) };
        Content = new Canvas { Children = { _visual } };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            WindowApi.MakeClickThrough(_hwnd);
            Layout();
        };
    }

    public event Action? Finished;

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Layout();
    }

    private void Layout()
    {
        if (_hwnd == IntPtr.Zero) return;
        WindowApi.PlaceTopmost(_hwnd, _bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height);
        double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Canvas.SetLeft(_visual, (_windowRect.Left - _bounds.Left) / s);
        Canvas.SetTop(_visual, (_windowRect.Top - _bounds.Top) / s);
        _visual.Width = _windowRect.Width / s;
        _visual.Height = _windowRect.Height / s;
    }

    public void Play()
    {
        // Shrink toward the notch: the transform origin is the notch, expressed relative to the snapshot.
        _visual.RenderTransformOrigin = new Point(
            (_target.X - _windowRect.Left) / Math.Max(1, _windowRect.Width),
            (_target.Y - _windowRect.Top) / Math.Max(1, _windowRect.Height));
        var scale = new ScaleTransform(1, 1);
        var rotate = new RotateTransform(0);
        _visual.RenderTransform = new TransformGroup { Children = { scale, rotate } };

        // Accelerating ease: slow at first, then it gets sucked in.
        var suck = new PowerEase { EasingMode = EasingMode.EaseIn, Power = 3 };
        var shrinkX = new DoubleAnimation(1, 0.02, Duration) { EasingFunction = suck };
        var shrinkY = new DoubleAnimation(1, 0.02, Duration) { EasingFunction = new PowerEase { EasingMode = EasingMode.EaseIn, Power = 2.4 } };
        var spin = new DoubleAnimation(0, 14, Duration) { EasingFunction = suck };
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180)) { BeginTime = Duration - TimeSpan.FromMilliseconds(180) };
        shrinkX.Completed += (_, _) =>
        {
            Finished?.Invoke();
            Close();
        };

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrinkX);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrinkY);
        rotate.BeginAnimation(RotateTransform.AngleProperty, spin);
        _visual.BeginAnimation(OpacityProperty, fade);
    }
}
