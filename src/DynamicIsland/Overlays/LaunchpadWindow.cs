using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicIsland.Interop;
using DynamicIsland.Services;

namespace DynamicIsland.Overlays;

/// <summary>
/// The dock's Apps screen (macOS Launchpad / "Apps"): every installed app in a grid over the
/// blurred desktop, with a search field. Click to open; right-click to keep it in the dock.
/// Esc or a click on empty space closes it.
/// </summary>
internal sealed class LaunchpadWindow : Window
{
    private static List<InstalledApp>? _cache;
    private static LaunchpadWindow? _open;

    private readonly DockModel _model;
    private readonly Int32Rect _monitor;
    private readonly WrapPanel _grid = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBox _search;
    private readonly Grid _content = new();
    private readonly ScaleTransform _zoom = new(0.94, 0.94);
    private List<InstalledApp> _apps = new();
    private IntPtr _hwnd;
    private bool _closing;

    public static void Toggle(DockModel model, Int32Rect monitor)
    {
        if (_open != null) { _open.FadeOut(); return; }
        _open = new LaunchpadWindow(model, monitor);
        _open.Show();
    }

    private LaunchpadWindow(DockModel model, Int32Rect monitor)
    {
        _model = model;
        _monitor = monitor;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(0x52, 0x0A, 0x0A, 0x14));
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; // a short-lived window that needs focus for typing
        Topmost = true;
        Title = "Apps";
        Opacity = 0;

        _search = new TextBox
        {
            Width = 260, Height = 30, FontSize = 13, Foreground = Brushes.White, CaretBrush = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0x29, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x4D, 255, 255, 255)), BorderThickness = new Thickness(0.5),
            VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(12, 0, 12, 0),
        };
        _search.Template = SearchTemplate();
        _search.TextChanged += (_, _) => Fill();

        var scroller = new ScrollViewer
        {
            Content = _grid, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 40, 0, 0),
        };
        _content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _content.RowDefinitions.Add(new RowDefinition());
        _content.Margin = new Thickness(0, 70, 0, 90);
        _content.Children.Add(_search);
        Grid.SetRow(scroller, 1);
        _content.Children.Add(scroller);
        _content.RenderTransformOrigin = new Point(0.5, 0.5);
        _content.RenderTransform = _zoom;
        Content = _content;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            GlassBlur.Enable(_hwnd);
            WindowApi.PlaceTopmost(_hwnd, _monitor.X, _monitor.Y, _monitor.Width, _monitor.Height);
        };
        Loaded += async (_, _) =>
        {
            WindowApi.ForceForeground(_hwnd);
            Activate();
            _search.Focus();
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            _zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            _zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            _apps = _cache ??= await ListOnSta();
            Fill();
            // Refresh in the background for next time (apps installed since).
            _ = Task.Run(async () => _cache = await ListOnSta());
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) FadeOut(); };
        MouseLeftButtonUp += (_, e) => { if (e.OriginalSource == this || e.OriginalSource is Grid or ScrollViewer or WrapPanel) FadeOut(); };
        Deactivated += (_, _) => { if (DockMenu.Current == null) FadeOut(); };
        Closed += (_, _) => { if (_open == this) _open = null; };
    }

    private static Task<List<InstalledApp>> ListOnSta()
    {
        var done = new TaskCompletionSource<List<InstalledApp>>();
        var t = new Thread(() =>
        {
            try { done.SetResult(PinnedApps.ListInstalled()); }
            catch (Exception ex) { done.SetException(ex); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        return done.Task;
    }

    private void Fill()
    {
        string q = _search.Text.Trim();
        var shown = q.Length == 0 ? _apps : _apps.Where(a => a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        _grid.Children.Clear();
        _grid.MaxWidth = 8 * 132;
        foreach (var app in shown.Take(400))
        {
            var img = new Image { Width = 76, Height = 76 };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var label = new TextBlock
            {
                Text = app.Name, Foreground = Brushes.White, FontSize = 12.5, TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 120, Margin = new Thickness(0, 8, 0, 0),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 0.5 },
            };
            var tile = new StackPanel { Width = 132, Margin = new Thickness(0, 0, 0, 26), Background = Brushes.Transparent, Cursor = Cursors.Arrow };
            tile.Children.Add(img);
            tile.Children.Add(label);
            var a = app;
            tile.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                FadeOut();
                System.Diagnostics.Process.Start("explorer.exe", @"shell:AppsFolder\" + a.Id)?.Dispose();
            };
            tile.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                var p = tile.PointToScreen(new Point(66, 76));
                DockMenu.ShowAt(new List<DockMenu.Item>
                {
                    new("Open", () => { FadeOut(); System.Diagnostics.Process.Start("explorer.exe", @"shell:AppsFolder\" + a.Id)?.Dispose(); }),
                    new("Keep in Dock", () => _model.KeepInstalled(a)),
                }, (int)p.X, (int)p.Y, _monitor, below: true);
            };
            _grid.Children.Add(tile);
            // Icons load a few at a time so the grid shows at once.
            Dispatcher.InvokeAsync(() => img.Source = DockIcons.ForInstalled(a)?.Source, DispatcherPriority.Background);
        }
    }

    private void FadeOut()
    {
        if (_closing) return;
        _closing = true;
        DockMenu.CloseCurrent();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
        _zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, TimeSpan.FromMilliseconds(150)));
        _zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, TimeSpan.FromMilliseconds(150)));
    }

    /// <summary>A pill-shaped search field (the default TextBox template is a square box).</summary>
    private static ControlTemplate SearchTemplate()
    {
        var t = new ControlTemplate(typeof(TextBox));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(15));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var host = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
        host.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        host.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 12, 0));
        border.AppendChild(host);
        t.VisualTree = border;
        return t;
    }
}
