using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DynamicIsland.Services;

/// <summary>
/// While the island's own edge snapping is on, Windows' drag-to-edge docking (its own previews
/// and snaps when a window is dragged to the left, right or top edge) is turned off, so the two
/// never fight. Only for this sign-in session (never written to the user's saved settings), and
/// given back when the island quits or the setting is switched off. Win+arrow keys keep working.
/// Works the same on Windows 10 and 11.
/// </summary>
public static class EdgeSnapping
{
    private const uint SPI_GETDOCKMOVING = 0x0090, SPI_SETDOCKMOVING = 0x0091, SPIF_SENDCHANGE = 0x2;
    private const string OurKey = @"Software\DynamicIsland";
    /// <summary>Set while we have Windows' docking turned off, so a restarted island still knows to give it back.</summary>
    private const string FlagValue = "DockMovingTakenOver";

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint uiParam, ref int pvParam, uint winIni);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint uiParam, IntPtr pvParam, uint winIni);

    private static bool _taken;

    /// <summary>True when the island handles dragging to the edges (Windows' docking is off).</summary>
    public static bool Active => _taken;

    /// <summary>Turns Windows' docking off (when <paramref name="on"/>) or gives it back.</summary>
    public static void Apply(bool on)
    {
        try
        {
            if (on) Take();
            else Release();
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't change Windows' drag-to-edge snapping", ex);
        }
    }

    private static void Take()
    {
        if (_taken) return;
        int current = 0;
        if (!SystemParametersInfo(SPI_GETDOCKMOVING, 0, ref current, 0)) return;
        if (current != 0)
        {
            Set(false);
            using var ours = Registry.CurrentUser.CreateSubKey(OurKey);
            ours.SetValue(FlagValue, 1, RegistryValueKind.DWord);
            Log.Info("Windows' drag-to-edge snapping paused while the island handles edges and corners");
        }
        _taken = true;
    }

    /// <summary>Gives Windows' docking back if we turned it off (also after a crash or restart).</summary>
    public static void Release()
    {
        _taken = false;
        using var ours = Registry.CurrentUser.CreateSubKey(OurKey);
        if (ours.GetValue(FlagValue) is not int flag || flag == 0) return;
        Set(true);
        ours.DeleteValue(FlagValue, throwOnMissingValue: false);
        Log.Info("Windows' drag-to-edge snapping given back");
    }

    // Documented as taking the value in pvParam; pass it in both so either reading works.
    private static void Set(bool on) => SystemParametersInfo(SPI_SETDOCKMOVING, on ? 1u : 0u, new IntPtr(on ? 1 : 0), SPIF_SENDCHANGE);
}
