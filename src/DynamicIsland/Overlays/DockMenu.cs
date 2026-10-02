using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicIsland.Interop;

namespace DynamicIsland.Overlays;

/// <summary>
/// A macOS-style context menu for the dock and the Apps grid: frosted glass, rounded, blue
/// highlight. It never takes focus; a click anywhere else closes it.
/// </summary>
internal sealed class DockMenu : Window
{
    public sealed record Item(string Text, Action? Act, bool Enabled = true)
    {
        public static readonly Item Separator = new("-", null);
        public bool IsSeparator => Text == "-" && Act == null;
    }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);

    public static DockMenu? Current { get; private set; }

    private readonly DispatcherTimer _outside = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly int _anchorX, _anchorY;
    private readonly Int32Rect _monitor;
    private IntPtr _hwnd;
    private bool _armed;

    public static void CloseCurrent() => Current?.Close();

    /// <summary>Opens a menu whose bottom center sits at the given point (physical pixels).</summary>
    public static void ShowAt(IReadOnlyList<Item> items, int x, int y, Int32Rect monitor, bool below = false)
    {
        CloseCurrent();
        if (items.Count == 0) return;
        Current = new DockMenu(items, x, y, monitor, below);
        Current.Show();
    }

    private readonly bool _below;

    private DockMenu(IReadOnlyList<Item> items, int x, int y, Int32Rect monitor, bool below)
    {
        _anchorX = x;
        _anchorY = y;
        _monitor = monitor;
        _below = below;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Left = -10000;
        Top = -10000;

        var stack = new StackPanel { MinWidth = 190 };
        foreach (var item in items)
        {
            if (item.IsSeparator)
            {
                stack.Children.Add(new Border { Height = 1, Margin = new Thickness(10, 5, 10, 5), Background = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255)) });
                continue;
            }
            var text = new TextBlock
            {
                Text = item.Text, FontSize = 13,
                Foreground = item.Enabled ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x66, 255, 255, 255)),
            };
            var row = new Border { Padding = new Thickness(10, 3, 18, 4), CornerRadius = new CornerRadius(6), Child = text, Background = Brushes.Transparent };
            if (item.Enabled && item.Act != null)
            {
                row.MouseEnter += (_, _) => row.Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
                row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
                var act = item.Act;
                row.MouseLeftButtonUp += (_, _) =>
                {
                    Close();
                    try { act(); }
                    catch (Exception ex) { Services.Log.Error("Dock menu action failed", ex); }
                };
            }
            stack.Children.Add(row);
        }
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x9E, 0x24, 0x24, 0x28)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255)),
            BorderThickness = new Thickness(0.5),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(5),
            Child = stack,
        };
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.MakeOverlayWindow(_hwnd);
            GlassBlur.Enable(_hwnd);
        };
        ContentRendered += (_, _) => Place();
        _outside.Tick += (_, _) => CheckOutsideClick();
        _outside.Start();
        Closed += (_, _) =>
        {
            _outside.Stop();
            if (Current == this) Current = null;
        };
    }

    private void Place()
    {
        double s = WindowApi.ScaleAt(_anchorX, _anchorY);
        int w = (int)Math.Round(ActualWidth * s), h = (int)Math.Round(ActualHeight * s);
        int x = Math.Clamp(_anchorX - w / 2, _monitor.X + 6, _monitor.X + _monitor.Width - w - 6);
        int y = _below ? _anchorY : _anchorY - h;
        y = Math.Clamp(y, _monitor.Y + 6, _monitor.Y + _monitor.Height - h - 6);
        WindowApi.PlaceTopmost(_hwnd, x, y, w, h);
        GlassBlur.SetRoundedRegion(_hwnd, w, h, (int)Math.Round(12 * s));
    }

    private void CheckOutsideClick()
    {
        bool down = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;
        if (!down) { _armed = true; return; } // ignore the click that opened it
        if (!_armed || _hwnd == IntPtr.Zero) return;
        WindowApi.GetCursorPos(out var p);
        WindowApi.GetWindowRect(_hwnd, out var r);
        if (p.X < r.Left || p.X >= r.Right || p.Y < r.Top || p.Y >= r.Bottom) Close();
    }
}
