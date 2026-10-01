using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
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
/// "Pin an app": a dark, searchable list of every installed app (desktop programs and Store apps),
/// dropped down under the island. Click one to pin it. Closes on click-away or Esc.
/// </summary>
internal sealed class AppPickerWindow : Window
{
    private const double DipWidth = 420, DipHeight = 500;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private static AppPickerWindow? _open;

    private readonly PinnedApps _pins;
    private readonly Int32Rect _monitor;
    private readonly TextBox _search = new();
    private readonly StackPanel _list = new();
    private readonly TextBlock _status = new();
    private readonly List<(InstalledApp App, FrameworkElement Row, Image Icon)> _rows = new();
    private IntPtr _hwnd;
    private bool _closing;

    public static void ShowFor(PinnedApps pins, Int32Rect monitor)
    {
        _open?.Close();
        _open = new AppPickerWindow(pins, monitor);
        _open.Show();
        _open.Activate();
        _open._search.Focus();
    }

    private AppPickerWindow(PinnedApps pins, Int32Rect monitor)
    {
        _pins = pins;
        _monitor = monitor;
        Title = "Pin an app";
        Width = DipWidth;
        Height = DipHeight;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x0F));
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Content = BuildLayout();

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            int round = 2; // Windows 11 rounded corners; ignored on Windows 10
            try { DwmSetWindowAttribute(_hwnd, 33, ref round, sizeof(int)); } catch { }
            Place();
        };
        ContentRendered += (_, _) => Dispatcher.BeginInvoke(LoadApps, DispatcherPriority.Background);
        Deactivated += (_, _) => SafeClose();
        PreviewKeyDown += OnKey;
        Closed += (_, _) => { if (_open == this) _open = null; };
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.BeginInvoke(Place, DispatcherPriority.Loaded);
    }

    private void Place()
    {
        if (_hwnd == IntPtr.Zero) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Round(DipWidth * dpi.DpiScaleX), h = (int)Math.Round(DipHeight * dpi.DpiScaleY);
        WindowApi.PlaceTopmost(_hwnd, _monitor.X + (_monitor.Width - w) / 2, _monitor.Y + (int)Math.Round(40 * dpi.DpiScaleY), w, h);
    }

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    private UIElement BuildLayout()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Search box, same look as the island's.
        var box = new Border
        {
            Margin = new Thickness(14, 14, 14, 10),
            Height = 34,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
        };
        var boxGrid = new Grid();
        boxGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        boxGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        boxGrid.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 6.5,0 A 6.5,6.5 0 1 0 6.5,13 A 6.5,6.5 0 1 0 6.5,0 M 11.5,11.5 L 17,17"),
            Stroke = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA2)),
            StrokeThickness = 1.6,
            Width = 13,
            Height = 13,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(11, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        _search.Background = Brushes.Transparent;
        _search.BorderThickness = new Thickness(0);
        _search.Foreground = Brushes.White;
        _search.CaretBrush = Brushes.White;
        _search.FontSize = 13;
        _search.VerticalAlignment = VerticalAlignment.Center;
        _search.TextChanged += (_, _) => Filter();
        Grid.SetColumn(_search, 1);
        boxGrid.Children.Add(_search);
        box.Child = boxGrid;
        root.Children.Add(box);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, // the wheel still scrolls; no bright system scrollbar
            Margin = new Thickness(6, 0, 6, 8),
            Content = new StackPanel { Children = { _status, _list } },
        };
        _status.Text = "Loading apps…";
        _status.Foreground = new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));
        _status.Margin = new Thickness(12, 8, 0, 0);
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        return root;
    }

    private void LoadApps()
    {
        var apps = PinnedApps.ListInstalled();
        _status.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (apps.Count == 0) _status.Text = "No apps found.";
        foreach (var app in apps)
        {
            var (row, icon) = CreateRow(app);
            _rows.Add((app, row, icon));
            _list.Children.Add(row);
        }
        Filter();
        LoadIconsInBackground(_rows.Select(r => (r.App.Id, r.Icon)).ToList());
    }

    private (FrameworkElement Row, Image Icon) CreateRow(InstalledApp app)
    {
        bool pinned = _pins.IsPinned("app", app.Id);
        var icon = new Image { Width = 28, Height = 28, Margin = new Thickness(0, 0, 12, 0) };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        var name = new TextBlock
        {
            Text = app.Name,
            Foreground = Brushes.White,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var tag = new TextBlock
        {
            Text = pinned ? "Pinned" : "",
            Foreground = new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(icon);
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        Grid.SetColumn(tag, 2);
        grid.Children.Add(tag);

        var row = new Border
        {
            Padding = new Thickness(10, 6, 12, 6),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Cursor = pinned ? Cursors.Arrow : Cursors.Hand,
            Child = grid,
        };
        var hover = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
        row.MouseEnter += (_, _) => row.Background = hover;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) =>
        {
            if (pinned) return;
            Pick(app);
        };
        return (row, icon);
    }

    private void Pick(InstalledApp app)
    {
        _pins.PinInstalled(app);
        SafeClose();
    }

    private void Filter()
    {
        var q = _search.Text.Trim();
        foreach (var (app, row, _) in _rows)
            row.Visibility = q.Length == 0 || app.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            SafeClose();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var first = _rows.FirstOrDefault(r => r.Row.Visibility == Visibility.Visible && !_pins.IsPinned("app", r.App.Id));
            if (first.App != null) Pick(first.App);
        }
    }

    /// <summary>Shell icons need an STA thread; fetch them one by one off the UI thread.</summary>
    private void LoadIconsInBackground(List<(string Id, Image Target)> items)
    {
        var dispatcher = Dispatcher;
        var thread = new Thread(() =>
        {
            foreach (var (id, target) in items)
            {
                if (_closing) return;
                var bmp = ShellIcons.Get(@"shell:AppsFolder\" + id, 64);
                if (bmp != null) dispatcher.BeginInvoke(() => target.Source = bmp, DispatcherPriority.Background);
            }
        })
        { IsBackground = true, Name = "AppIcons" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
}
