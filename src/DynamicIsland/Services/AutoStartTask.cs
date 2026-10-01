using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace DynamicIsland.Services;

/// <summary>
/// Starts the island with Windows through a Task Scheduler task (works the same on Windows 10 and 11):
///  - at sign-in, before regular startup apps (the Run key is deliberately delayed by Windows);
///  - every minute (a separate timer, so it works from the moment it's registered), as a safety net: if the island isn't running (both it and its
///    watchdog were killed at once), it comes back; if it is, the extra launch exits immediately;
///  - in "high priority" mode it runs with the user's highest rights (one UAC prompt, at install,
///    like Wallpaper Engine) and above-normal CPU priority.
/// </summary>
public static class AutoStartTask
{
    public const string TaskName = "DynamicIsland";
    public const string AutostartArg = "--autostart";
    public const string RegisterArg = "--register-autostart";   // + "high" | "normal"
    public const string UnregisterArg = "--unregister-autostart";

    /// <summary>
    /// The entry Windows lists under Settings › Apps › Startup and Task Manager › Startup apps. It only
    /// hands off to the task (so the island keeps the rights the user chose); its on/off switch there
    /// is honored by every automatic start.
    /// </summary>
    public const string StartupEntryArg = "--startup-entry";
    private const string StartupApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "DynamicIsland";
    private const string OurKey = @"Software\DynamicIsland";

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>The registered task's settings, or null if there is none.</summary>
    public static (bool High, string Command, bool Repeats)? Query()
    {
        var (code, output) = RunSchtasks($"/query /tn \"{TaskName}\" /xml ONE");
        if (code != 0 || output.Length == 0) return null;
        bool high = output.Contains("<RunLevel>HighestAvailable</RunLevel>", StringComparison.OrdinalIgnoreCase);
        int start = output.IndexOf("<Command>", StringComparison.OrdinalIgnoreCase);
        int end = output.IndexOf("</Command>", StringComparison.OrdinalIgnoreCase);
        string command = start >= 0 && end > start ? System.Net.WebUtility.HtmlDecode(output[(start + 9)..end]) : "";
        bool repeats = output.Contains("<TimeTrigger>", StringComparison.OrdinalIgnoreCase);
        return (high, command.Trim().Trim('"'), repeats);
    }

    /// <summary>
    /// Registers (or replaces) the task. High priority needs admin rights: schtasks is started
    /// elevated, which shows Windows' permission prompt. Returns false if that was declined/failed.
    /// </summary>
    public static bool Register(bool high)
    {
        if (Environment.ProcessPath is not { } exe) return false;
        var xmlPath = Path.Combine(Path.GetTempPath(), $"DynamicIsland-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(xmlPath, BuildXml(exe, high), Encoding.Unicode);
            var args = $"/create /tn \"{TaskName}\" /xml \"{xmlPath}\" /f";
            // Admin rights are needed to create a high-priority task, and to replace one.
            bool needsAdmin = (high || Query() is { High: true }) && !IsElevated;
            bool ok = needsAdmin ? RunSchtasksElevated(args) : RunSchtasks(args).Code == 0;
            if (ok)
            {
                EnsureRunKey(); // the Startup apps entry; it defers to the task
                using var ours = Registry.CurrentUser.CreateSubKey(OurKey);
                ours.SetValue("HighPriority", high ? 1 : 0, RegistryValueKind.DWord);
                Log.Info($"Start-with-Windows task registered ({(high ? "high priority" : "normal")})");
            }
            else
            {
                Log.Info($"Start-with-Windows task not registered ({(high ? "high priority" : "normal")})");
            }
            return ok;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to register the start-with-Windows task", ex);
            return false;
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { }
        }
    }

    /// <summary>Removes the task (asks for admin rights if it was registered with them).</summary>
    public static void Unregister()
    {
        var args = $"/delete /tn \"{TaskName}\" /f";
        if (RunSchtasks(args).Code != 0 && Query() != null) RunSchtasksElevated(args);
        RemoveRunKey();
    }

    /// <summary>Starts the island through the task (inherits its rights and priority, and has no parent process).</summary>
    public static bool RunNow() => RunSchtasks($"/run /tn \"{TaskName}\"").Code == 0;

    /// <summary>
    /// Called on every start: makes sure something will start the island with Windows. Keeps a
    /// high-priority task as it is; otherwise (re)registers a normal task pointing at this exe,
    /// falling back to the Run key if Task Scheduler isn't usable.
    /// </summary>
    public static void EnsureRegistered()
    {
        try
        {
            var task = Query();
            if (task is { High: true }) // set up by the installer with admin rights; leave it
            {
                EnsureRunKey();
                return;
            }
            if (task is { } t && t.Repeats && string.Equals(t.Command, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            {
                EnsureRunKey();
                return;
            }
            Log.Info(task is { } old
                ? $"Start-with-Windows task needs updating (points at '{old.Command}', every-minute check: {old.Repeats})"
                : "No start-with-Windows task yet; registering one");
            if (!Register(high: false)) EnsureRunKey();
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't check the start-with-Windows task", ex);
            EnsureRunKey();
        }
    }

    // ---------------------------------------------------------------- Windows' own startup switch

    /// <summary>
    /// True if the user turned Dynamic Island off in Settings › Apps › Startup (or Task Manager).
    /// Windows stores that per entry: the first byte is 2 (on) or 3 (off).
    /// </summary>
    public static bool DisabledInWindowsStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedKey);
            return key?.GetValue(RunValue) is byte[] { Length: > 0 } data && (data[0] & 1) == 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Flips the same switch Windows shows in its Startup apps list (used by our Settings window).</summary>
    public static void SetWindowsStartupEnabled(bool enabled)
    {
        try
        {
            var data = new byte[12];
            data[0] = (byte)(enabled ? 2 : 3);
            if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
            using var key = Registry.CurrentUser.CreateSubKey(StartupApprovedKey);
            key.SetValue(RunValue, data, RegistryValueKind.Binary);
            EnsureRunKey();
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't change the startup setting", ex);
        }
    }

    // ---------------------------------------------------------------- "quit" that sticks

    /// <summary>Remembers that the user quit during this Windows session, so the every-minute check doesn't undo it.</summary>
    public static void RememberQuit()
    {
        try
        {
            using var ours = Registry.CurrentUser.CreateSubKey(OurKey);
            ours.SetValue("QuitAtBoot", BootTime().Ticks.ToString(), RegistryValueKind.String);
        }
        catch { }
    }

    public static void ForgetQuit()
    {
        try
        {
            using var ours = Registry.CurrentUser.CreateSubKey(OurKey);
            ours.DeleteValue("QuitAtBoot", throwOnMissingValue: false);
        }
        catch { }
    }

    /// <summary>True if the user quit since Windows last started (a restart clears it).</summary>
    public static bool QuitThisBoot()
    {
        try
        {
            using var ours = Registry.CurrentUser.OpenSubKey(OurKey);
            if (ours?.GetValue("QuitAtBoot") is not string s || !long.TryParse(s, out var ticks)) return false;
            return Math.Abs((BootTime() - new DateTime(ticks, DateTimeKind.Utc)).TotalMinutes) < 2;
        }
        catch
        {
            return false;
        }
    }

    private static DateTime BootTime() => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    // ---------------------------------------------------------------- helpers

    private static string BuildXml(string exe, bool high)
    {
        string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>Dynamic Island</Author>
                <Description>Starts Dynamic Island when you sign in and brings it back if it stops.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                </LogonTrigger>
                <TimeTrigger>
                  <Enabled>true</Enabled>
                  <StartBoundary>{DateTime.Now.AddMinutes(1):yyyy-MM-ddTHH:mm:ss}</StartBoundary>
                  <Repetition>
                    <Interval>PT1M</Interval>
                    <StopAtDurationEnd>false</StopAtDurationEnd>
                  </Repetition>
                </TimeTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>{(high ? "HighestAvailable" : "LeastPrivilege")}</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>{(high ? 2 : 5)}</Priority>
                <RestartOnFailure>
                  <Interval>PT1M</Interval>
                  <Count>999</Count>
                </RestartOnFailure>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>"{SecurityElement.Escape(exe)}"</Command>
                  <Arguments>{AutostartArg}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static (int Code, string Output) RunSchtasks(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            })!;
            var output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return (p.ExitCode, output);
        }
        catch (Exception ex)
        {
            Log.Error("schtasks failed", ex);
            return (-1, "");
        }
    }

    /// <summary>Runs schtasks with admin rights (Windows shows its permission prompt). False if declined.</summary>
    private static bool RunSchtasksElevated(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            })!;
            p.WaitForExit(60000);
            return p.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false; // the user said no to the permission prompt
        }
        catch (Exception ex)
        {
            Log.Error("Elevated schtasks failed", ex);
            return false;
        }
    }

    private static void EnsureRunKey()
    {
        try
        {
            if (Environment.ProcessPath is not { } path) return;
            var command = $"\"{path}\" {StartupEntryArg}";
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            if (run.GetValue(RunValue) as string != command) run.SetValue(RunValue, command);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to register autostart", ex);
        }
    }

    private static void RemoveRunKey()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            run?.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch { }
    }
}
