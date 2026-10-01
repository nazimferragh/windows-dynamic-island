using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace DynamicIsland.Controls;

/// <summary>The little bouncing "now playing" bars.</summary>
public sealed class Equalizer : StackPanel
{
    private const double IdleScale = 0.3;
    private static readonly double[] PeriodsMs = { 430, 560, 370, 510 };

    private readonly List<Rectangle> _bars = new();
    private readonly List<ScaleTransform> _scales = new();
    private Brush? _brush;
    private bool _playing;

    public Equalizer()
    {
        Orientation = Orientation.Horizontal;
    }

    public double BarWidth { get; set; } = 3;
    public double BarHeight { get; set; } = 14;
    public double BarSpacing { get; set; } = 2.5;

    public Brush? BarBrush
    {
        get => _brush;
        set
        {
            _brush = value;
            foreach (var bar in _bars) bar.Fill = value;
        }
    }

    public bool IsPlaying
    {
        get => _playing;
        set
        {
            if (_playing == value) return;
            _playing = value;
            Animate();
        }
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        for (int i = 0; i < PeriodsMs.Length; i++)
        {
            var scale = new ScaleTransform(1, IdleScale);
            var bar = new Rectangle
            {
                Width = BarWidth,
                Height = BarHeight,
                RadiusX = BarWidth / 2,
                RadiusY = BarWidth / 2,
                Fill = _brush,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = scale,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(i == 0 ? 0 : BarSpacing, 0, 0, 0),
            };
            _bars.Add(bar);
            _scales.Add(scale);
            Children.Add(bar);
        }
    }

    private void Animate()
    {
        for (int i = 0; i < _scales.Count; i++)
        {
            AnimationTimeline animation;
            if (_playing)
            {
                animation = new DoubleAnimation(IdleScale, 1.0, TimeSpan.FromMilliseconds(PeriodsMs[i]))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromMilliseconds(i * 90),
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                };
                // The bars don't need 60+ fps; every frame redraws the island window, so keep it low.
                Timeline.SetDesiredFrameRate(animation, 24);
            }
            else
            {
                animation = new DoubleAnimation(IdleScale, TimeSpan.FromMilliseconds(200));
            }
            _scales[i].BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }
    }
}
