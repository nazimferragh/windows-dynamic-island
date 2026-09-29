using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using DynamicIsland.Interop;

namespace DynamicIsland.Overlays;

/// <summary>The little preview that follows the cursor while pulling a window back out of the island.</summary>
internal sealed class DragGhost : Window
{
    private const double CardWidth = 240;
    private IntPtr _hwnd;

    public DragGhost(BitmapSource? snapshot, ImageSource? icon, string title)
    {
        double height = snapshot != null ? Math.Clamp(CardWidth * snapshot.PixelHeight / Math.Max(1, snapshot.PixelWidth), 60, 180) : 60;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = CardWidth + 40;
        Height = height + 40;

        var card = new Border
        {
            Width = CardWidth,
            Height = height,
            CornerRadius = new CornerRadius(12),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Background = snapshot != null
                ? new ImageBrush(snapshot) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Top }
                : new SolidColorBrush(Color.FromRgb(28, 28, 30)),
            Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Direction = 270, Opacity = 0.5 },
            Opacity = 0.92,
            Child = snapshot == null
                ? new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new Image { Source = icon, Width = 18, Height = 18, Margin = new Thickness(0, 0, 8, 0) },
                        new TextBlock { Text = title, Foreground = Brushes.White, FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
                    },
                }
                : null,
        };
        Content = card;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            WindowApi.MakeClickThrough(_hwnd);
        };
    }

    /// <summary>Keeps the card's top-center just under the cursor (physical pixels).</summary>
    public void MoveTo(int x, int y)
    {
        if (_hwnd == IntPtr.Zero) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)(Width * dpi.DpiScaleX);
        WindowApi.MoveTopmost(_hwnd, x - w / 2, y - (int)(12 * dpi.DpiScaleY));
    }
}
