using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicIsland.Interop;
using DynamicIsland.Services;

namespace DynamicIsland.Overlays;

/// <summary>
/// "Join network": a small dark password box that drops down under the island when a secured
/// network the PC doesn't know yet is chosen in the island's Wi‑Fi view. (The island itself never
/// takes keyboard focus, so typing happens here.)
/// </summary>
internal sealed class WifiPasswordWindow : Window
{
    private const double DipWidth = 360, DipHeight = 196;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private static WifiPasswordWindow? _open;

    private readonly WifiNetwork _network;
    private readonly Int32Rect _monitor;
    private readonly PasswordBox _password = new();
    private readonly TextBox _plain = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new();
    private readonly Border _join = new();
    private IntPtr _hwnd;
    private bool _busy, _closing;

    public static void ShowFor(WifiNetwork network, Int32Rect monitor)
    {
        _open?.Close();
        _open = new WifiPasswordWindow(network, monitor);
        _open.Show();
        _open.Topmost = true;
        _open.Activate();
        _open._password.Focus();
    }

    private WifiPasswordWindow(WifiNetwork network, Int32Rect monitor)
    {
        _network = network;
        _monitor = monitor;
        Title = "Join " + network.Ssid;
        Width = DipWidth;
        Height = DipHeight;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x0F));
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Content = BuildLayout();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            int round = 2; // rounded corners on Windows 11; ignored on Windows 10
            try { DwmSetWindowAttribute(_hwnd, 33, ref round, sizeof(int)); } catch { }
            var dpi = VisualTreeHelper.GetDpi(this);
            int w = (int)Math.Round(DipWidth * dpi.DpiScaleX), h = (int)Math.Round(DipHeight * dpi.DpiScaleY);
            WindowApi.PlaceTopmost(_hwnd, _monitor.X + (_monitor.Width - w) / 2, _monitor.Y + (int)Math.Round(40 * dpi.DpiScaleY), w, h);
        };
        Deactivated += (_, _) => { if (!_busy) SafeClose(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) SafeClose();
            else if (e.Key == Key.Enter) _ = JoinAsync();
        };
        Closed += (_, _) => { if (_open == this) _open = null; };
    }

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    private UIElement BuildLayout()
    {
        var white = Brushes.White;
        var muted = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        var root = new StackPanel { Margin = new Thickness(20, 18, 20, 16) };
        root.Children.Add(new TextBlock { Text = $"Join “{_network.Ssid}”", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = white, TextTrimming = TextTrimming.CharacterEllipsis });
        root.Children.Add(new TextBlock { Text = "Enter the network password", FontSize = 12, Foreground = muted, Margin = new Thickness(0, 2, 0, 12) });

        foreach (Control box in new Control[] { _password, _plain })
        {
            box.Height = 34;
            box.FontSize = 14;
            box.Foreground = white;
            box.Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            box.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            box.BorderThickness = new Thickness(1);
            box.Padding = new Thickness(8, 6, 8, 6);
            box.VerticalContentAlignment = VerticalAlignment.Center;
        }
        _password.PasswordChar = '●';
        _password.Foreground = white;
        _plain.CaretBrush = white;
        var field = new Grid { Children = { _password, _plain } };
        root.Children.Add(field);

        var show = new CheckBox { Content = "Show password", Foreground = muted, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };
        show.Checked += (_, _) => { _plain.Text = _password.Password; _plain.Visibility = Visibility.Visible; _password.Visibility = Visibility.Collapsed; _plain.Focus(); _plain.CaretIndex = _plain.Text.Length; };
        show.Unchecked += (_, _) => { _password.Password = _plain.Text; _password.Visibility = Visibility.Visible; _plain.Visibility = Visibility.Collapsed; _password.Focus(); };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Button("Cancel", primary: false);
        cancel.MouseLeftButtonUp += (_, _) => SafeClose();
        StyleButton(_join, "Join", primary: true);
        _join.MouseLeftButtonUp += (_, _) => _ = JoinAsync();
        buttons.Children.Add(cancel);
        buttons.Children.Add(_join);

        var bottom = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        bottom.Children.Add(show);
        bottom.Children.Add(buttons);
        root.Children.Add(bottom);

        _status.FontSize = 12;
        _status.Margin = new Thickness(0, 8, 0, 0);
        _status.Foreground = muted;
        root.Children.Add(_status);
        return root;
    }

    private static Border Button(string text, bool primary)
    {
        var b = new Border();
        StyleButton(b, text, primary);
        return b;
    }

    private static void StyleButton(Border b, string text, bool primary)
    {
        b.Padding = new Thickness(16, 6, 16, 7);
        b.Margin = new Thickness(8, 0, 0, 0);
        b.CornerRadius = new CornerRadius(6);
        b.Cursor = Cursors.Hand;
        b.Background = primary ? new SolidColorBrush(WindowsTheme.Current.AccentOnDark) : new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        b.Child = new TextBlock { Text = text, FontSize = 13, FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal, Foreground = primary ? Brushes.Black : Brushes.White };
    }

    private async Task JoinAsync()
    {
        if (_busy) return;
        var password = _plain.Visibility == Visibility.Visible ? _plain.Text : _password.Password;
        if (password.Length == 0) return;
        _busy = true;
        _status.Text = "Joining…";
        _status.Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        var result = await WifiService.JoinAsync(_network, password);
        _busy = false;
        switch (result)
        {
            case WifiJoinResult.Joined:
                SafeClose();
                break;
            case WifiJoinResult.WrongPassword:
                _status.Text = "That password didn't work. Check it and try again.";
                _status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x60));
                break;
            default:
                _status.Text = "Couldn't join this network. Move closer to the router and try again.";
                _status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x60));
                break;
        }
    }
}
