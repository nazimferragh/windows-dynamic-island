using System;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.UI.ViewManagement;

namespace DynamicIsland.Services;

/// <summary>
/// The user's Windows colors: the accent color (Settings › Personalization › Colors) and whether
/// Windows and apps are in light or dark mode. Raises <see cref="Changed"/> on the UI thread the
/// moment either changes. Same APIs on Windows 10 and 11.
/// </summary>
public sealed class WindowsTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static WindowsTheme Current { get; } = new();

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly UISettings? _ui;

    /// <summary>The accent as Windows shows it on dark surfaces (the island is always black).</summary>
    public Color AccentOnDark { get; private set; } = Color.FromRgb(0x4C, 0xC2, 0xFF);

    /// <summary>The accent as Windows shows it on light surfaces.</summary>
    public Color AccentOnLight { get; private set; } = Color.FromRgb(0x00, 0x67, 0xC0);

    /// <summary>Taskbar/Start ("Windows mode") is light.</summary>
    public bool SystemLight { get; private set; }

    /// <summary>Apps ("app mode") are light.</summary>
    public bool AppsLight { get; private set; }

    public event Action? Changed;

    private WindowsTheme()
    {
        try
        {
            _ui = new UISettings();
            _ui.ColorValuesChanged += (_, _) => _dispatcher.InvokeAsync(Refresh);
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't read Windows colors; using defaults", ex);
        }
        // Light/dark switches also arrive here (and are the only signal on some Windows 10 builds).
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                _dispatcher.InvokeAsync(Refresh);
        };
        Read();
    }

    private void Refresh()
    {
        var before = (AccentOnDark, AccentOnLight, SystemLight, AppsLight);
        Read();
        if (before != (AccentOnDark, AccentOnLight, SystemLight, AppsLight)) Changed?.Invoke();
    }

    private void Read()
    {
        try
        {
            if (_ui != null)
            {
                AccentOnDark = ToWpf(_ui.GetColorValue(UIColorType.AccentLight2));
                AccentOnLight = ToWpf(_ui.GetColorValue(UIColorType.AccentDark1));
            }
        }
        catch { }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            // Missing values mean the Windows default: light (Windows 10 before 1903 had no light taskbar).
            SystemLight = key?.GetValue("SystemUsesLightTheme") is int s ? s != 0 : false;
            AppsLight = key?.GetValue("AppsUseLightTheme") is int a ? a != 0 : true;
        }
        catch { }
    }

    private static Color ToWpf(Windows.UI.Color c) => Color.FromArgb(c.A, c.R, c.G, c.B);
}
