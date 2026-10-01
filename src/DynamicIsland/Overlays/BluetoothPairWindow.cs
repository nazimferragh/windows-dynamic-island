using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DynamicIsland.Interop;
using DynamicIsland.Services;

namespace DynamicIsland.Overlays;

/// <summary>
/// The step a Bluetooth device asks for while pairing, as a small dark box under the island:
/// confirm that a code matches, type a code, or note a code to type on the device.
/// </summary>
internal sealed class BluetoothPairWindow : Window
{
    private const double DipWidth = 360, DipHeight = 200;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private readonly TaskCompletionSource<(bool, string)> _result = new();
    private readonly Int32Rect _monitor;
    private readonly TextBox _pinBox = new();
    private IntPtr _hwnd;

    /// <summary>Shows the prompt and waits for the user's answer (Ok, typed PIN).</summary>
    public static Task<(bool Ok, string Pin)> AskAsync(string deviceName, BtPairPrompt prompt, string pin, Int32Rect monitor)
    {
        var window = new BluetoothPairWindow(deviceName, prompt, pin, monitor);
        window.Show();
        window.Topmost = true;
        window.Activate();
        if (prompt == BtPairPrompt.EnterPin) window._pinBox.Focus();
        return window._result.Task;
    }

    private BluetoothPairWindow(string deviceName, BtPairPrompt prompt, string pin, Int32Rect monitor)
    {
        _monitor = monitor;
        Title = "Pair " + deviceName;
        Width = DipWidth;
        Height = DipHeight;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x0F));
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Content = BuildLayout(deviceName, prompt, pin);
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            int round = 2;
            try { DwmSetWindowAttribute(_hwnd, 33, ref round, sizeof(int)); } catch { }
            var dpi = VisualTreeHelper.GetDpi(this);
            int w = (int)Math.Round(DipWidth * dpi.DpiScaleX), h = (int)Math.Round(DipHeight * dpi.DpiScaleY);
            WindowApi.PlaceTopmost(_hwnd, _monitor.X + (_monitor.Width - w) / 2, _monitor.Y + (int)Math.Round(40 * dpi.DpiScaleY), w, h);
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Finish(false);
            else if (e.Key == Key.Enter) Finish(true);
        };
        Closed += (_, _) => _result.TrySetResult((false, ""));
    }

    private void Finish(bool ok)
    {
        _result.TrySetResult((ok, _pinBox.Text.Trim()));
        Close();
    }

    private UIElement BuildLayout(string name, BtPairPrompt prompt, string pin)
    {
        var muted = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        var root = new StackPanel { Margin = new Thickness(20, 18, 20, 16) };
        root.Children.Add(new TextBlock { Text = $"Pair with “{name}”", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
        string message = prompt switch
        {
            BtPairPrompt.ConfirmPin => "Check that this code matches the one on the device:",
            BtPairPrompt.ShowPin => "Type this code on the device, then press Enter on it:",
            BtPairPrompt.EnterPin => "Enter the code shown on the device (often 0000 or 1234):",
            _ => "Pair this device?",
        };
        root.Children.Add(new TextBlock { Text = message, FontSize = 12, Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) });

        if (prompt is BtPairPrompt.ConfirmPin or BtPairPrompt.ShowPin)
            root.Children.Add(new TextBlock { Text = pin, FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, FontFamily = new FontFamily("Cascadia Mono, Consolas"), Margin = new Thickness(0, 0, 0, 6) });
        if (prompt == BtPairPrompt.EnterPin)
        {
            _pinBox.Height = 34;
            _pinBox.FontSize = 16;
            _pinBox.Foreground = Brushes.White;
            _pinBox.CaretBrush = Brushes.White;
            _pinBox.Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            _pinBox.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            _pinBox.Padding = new Thickness(8, 4, 8, 4);
            _pinBox.VerticalContentAlignment = VerticalAlignment.Center;
            root.Children.Add(_pinBox);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        if (prompt != BtPairPrompt.ShowPin) buttons.Children.Add(Button("Cancel", false, () => Finish(false)));
        buttons.Children.Add(Button(prompt == BtPairPrompt.ShowPin ? "Done" : "Pair", true, () => Finish(true)));
        root.Children.Add(buttons);
        return root;
    }

    private static Border Button(string text, bool primary, Action click)
    {
        var b = new Border
        {
            Padding = new Thickness(16, 6, 16, 7),
            Margin = new Thickness(8, 0, 0, 0),
            CornerRadius = new CornerRadius(6),
            Cursor = Cursors.Hand,
            Background = primary ? new SolidColorBrush(WindowsTheme.Current.AccentOnDark) : new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
            Child = new TextBlock { Text = text, FontSize = 13, FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal, Foreground = primary ? Brushes.Black : Brushes.White },
        };
        b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }
}
