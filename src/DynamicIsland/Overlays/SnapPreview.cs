using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicIsland.Interop;

namespace DynamicIsland.Overlays;

/// <summary>
/// The outline that shows where a dragged window will land when dropped on a snap zone in the
/// island. A click-through overlay over one monitor's work area. The outline follows the chosen
/// zone on a spring (so sweeping across zones never restarts or hitches), and the layout's other
/// zones show faintly: that's where the most recent windows will go.
/// </summary>
internal sealed class SnapPreview : Window
{
    private const double Inset = 6; // a little breathing room, like Windows' own preview

    private readonly WindowApi.RECT _work;
    private readonly Canvas _canvas = new();
    private readonly Border _box = new() { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(2), Opacity = 0 };
    private readonly List<Border> _others = new();
    private readonly Color _accent;
    private readonly Axis _x = new(), _y = new(), _w = new(), _h = new();
    private IntPtr _hwnd;
    private bool _shown, _running;
    private TimeSpan _lastFrame;

    public SnapPreview(WindowApi.RECT work, Color accent)
    {
        _work = work;
        _accent = accent;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true; // tool-window style below hides the button; keeps it on the current desktop
        ShowActivated = false;
        Topmost = true;
        _box.BorderBrush = new SolidColorBrush(accent);
        _box.Background = new SolidColorBrush(Color.FromArgb(0x40, accent.R, accent.G, accent.B));
        _box.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Opacity = 0.35, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance };
        _canvas.Children.Add(_box);
        Content = _canvas;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            WindowApi.MakeClickThrough(_hwnd);
            WindowApi.PlaceTopmost(_hwnd, _work.Left, _work.Top, _work.Width, _work.Height);
        };
        Closed += (_, _) => StopLoop();
    }

    private double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private Rect ToLocal(WindowApi.RECT zone)
    {
        double s = Scale;
        double x = (zone.Left - _work.Left) / s + Inset, y = (zone.Top - _work.Top) / s + Inset;
        return new Rect(x, y, Math.Max(10, zone.Width / s - Inset * 2), Math.Max(10, zone.Height / s - Inset * 2));
    }

    /// <summary>Shows (or moves) the outline to a zone; <paramref name="others"/> are the layout's other zones. Physical pixels.</summary>
    public void ShowZone(WindowApi.RECT zone, IReadOnlyList<WindowApi.RECT> others)
    {
        if (!IsVisible) Show();
        var r = ToLocal(zone);

        if (!_shown)
        {
            // First zone: grow in from slightly smaller around its center, fading in.
            _x.Snap(r.X + r.Width * 0.06);
            _y.Snap(r.Y + r.Height * 0.06);
            _w.Snap(r.Width * 0.88);
            _h.Snap(r.Height * 0.88);
            _shown = true;
        }
        _x.Target = r.X;
        _y.Target = r.Y;
        _w.Target = r.Width;
        _h.Target = r.Height;
        _box.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        ShowOthers(others);
        StartLoop();
    }

    /// <summary>The rest of the layout, drawn faintly behind the outline.</summary>
    private void ShowOthers(IReadOnlyList<WindowApi.RECT> others)
    {
        foreach (var old in _others)
        {
            var o = old;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
            fade.Completed += (_, _) => _canvas.Children.Remove(o);
            o.BeginAnimation(OpacityProperty, fade);
        }
        _others.Clear();
        foreach (var zone in others)
        {
            var r = ToLocal(zone);
            var ghost = new Border
            {
                Width = r.Width,
                Height = r.Height,
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1.5),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x70, _accent.R, _accent.G, _accent.B)),
                Background = new SolidColorBrush(Color.FromArgb(0x14, _accent.R, _accent.G, _accent.B)),
                Opacity = 0,
            };
            Canvas.SetLeft(ghost, r.X);
            Canvas.SetTop(ghost, r.Y);
            _canvas.Children.Insert(0, ghost); // under the main outline
            ghost.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)) { BeginTime = TimeSpan.FromMilliseconds(60) });
            _others.Add(ghost);
        }
    }

    /// <summary>Fades the outline out (the window stays ready for the next zone).</summary>
    public void HideZone()
    {
        if (!_shown) return;
        _shown = false;
        _box.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(140)));
        ShowOthers(Array.Empty<WindowApi.RECT>());
    }

    private void StartLoop()
    {
        if (_running) return;
        _running = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopLoop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastFrame) return;
        double dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Min((now - _lastFrame).TotalSeconds, 1 / 30.0);
        _lastFrame = now;
        bool settled = _x.Step(dt) & _y.Step(dt) & _w.Step(dt) & _h.Step(dt);
        Canvas.SetLeft(_box, _x.Value);
        Canvas.SetTop(_box, _y.Value);
        _box.Width = Math.Max(1, _w.Value);
        _box.Height = Math.Max(1, _h.Value);
        if (settled) StopLoop();
    }

    /// <summary>A critically damped spring (≈ 0.25 s): quick, no wobble, and retargetable mid-flight.</summary>
    private sealed class Axis
    {
        private const double Omega = 22;
        public double Value, Target, Velocity;

        public void Snap(double v)
        {
            Value = Target = v;
            Velocity = 0;
        }

        public bool Step(double dt)
        {
            // Exact solution for a critically damped spring over dt: stable at any frame rate.
            double x = Value - Target;
            double e = Math.Exp(-Omega * dt);
            double tmp = (Velocity + Omega * x) * dt;
            Velocity = (Velocity - Omega * tmp) * e;
            Value = Target + (x + tmp) * e;
            if (Math.Abs(Value - Target) > 0.3 || Math.Abs(Velocity) > 2) return false;
            Value = Target;
            Velocity = 0;
            return true;
        }
    }
}
