using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace DynamicIsland.Services;

/// <summary>
/// Opens a notification the way clicking it in Windows would (the screenshot opens in the editor,
/// a message opens its chat, a web notification opens its page). Apps can't activate each other's
/// notifications directly, so this clicks the same notification in Windows' notification center
/// through UI Automation: the panel opens, the matching entry is invoked, and Windows closes the
/// panel itself as it launches the target. If the entry can't be found, the sending app is opened.
/// </summary>
public static class NotificationActivator
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

    private const byte VK_ESCAPE = 0x1B;
    private const uint KEYEVENTF_KEYUP = 0x2;

    private static int _busy;

    public static void Activate(NotificationInfo info)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return; // one click at a time
        Task.Run(() =>
        {
            try
            {
                if (!InvokeInNotificationCenter(info)) OpenApp(info.AppId);
            }
            catch (Exception ex)
            {
                Log.Error("Opening a notification failed", ex);
                OpenApp(info.AppId);
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        });
    }

    private static bool InvokeInNotificationCenter(NotificationInfo info)
    {
        Process.Start(new ProcessStartInfo("ms-actioncenter:") { UseShellExecute = true })?.Dispose();

        var panel = WaitForPanel(TimeSpan.FromSeconds(2));
        if (panel == null)
        {
            Log.Info("Notification center didn't open; opening the app instead");
            return false;
        }

        // Entries are named "<title>. <body>. . Received at …", newest first.
        string title = Normalize(info.Title), body = Normalize(info.Body);
        var items = panel.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
        var match = items.Cast<AutomationElement>().FirstOrDefault(item =>
        {
            var name = Normalize(item.Current.Name);
            return title.Length > 0 && name.StartsWith(title) && (body.Length == 0 || name.Contains(body[..Math.Min(body.Length, 40)]));
        }) ?? items.Cast<AutomationElement>().FirstOrDefault(item => title.Length > 0 && Normalize(item.Current.Name).StartsWith(title));

        if (match == null || !match.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
        {
            Log.Info($"Couldn't find '{info.Title}' in the notification center; opening the app instead");
            keybd_event(VK_ESCAPE, 0, 0, IntPtr.Zero); // close the panel again
            keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
            return false;
        }

        ((InvokePattern)pattern).Invoke();
        Log.Info($"Opened notification '{info.Title}' from {info.AppName}");
        return true;
    }

    /// <summary>The notification center is a shell CoreWindow that takes the foreground when it opens.</summary>
    private static AutomationElement? WaitForPanel(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                var hwnd = GetForegroundWindow();
                if (hwnd != IntPtr.Zero)
                {
                    var element = AutomationElement.FromHandle(hwnd);
                    if (element.Current.ClassName == "Windows.UI.Core.CoreWindow" && IsShell(element.Current.ProcessId))
                        return element;
                }
            }
            catch
            {
                // The window can vanish mid-query; just look again.
            }
            Thread.Sleep(25);
        }
        return null;
    }

    private static bool IsShell(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string Normalize(string s) => Regex.Replace(s ?? "", @"\s+", " ").Trim().ToLowerInvariant();

    /// <summary>Fallback: bring up the app that sent the notification.</summary>
    private static void OpenApp(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{appId}") { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't open {appId}", ex);
        }
    }
}
