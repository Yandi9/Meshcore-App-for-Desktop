using Microsoft.Win32;
using MC1.Core.Services;

namespace MC1.Windows.Platform.Windows;

/// <summary>
/// "Start with Windows": a per-user Run entry (no admin rights needed). Windows' own Startup-apps switch
/// (Task Manager / Settings ▸ Apps ▸ Startup) is respected and reset when the user turns the option on here.
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "MeshCore";
    public const string Argument = "--autostart";

    private static string? Command => Environment.ProcessPath is { } exe ? $"\"{exe}\" {Argument}" : null;

    public static bool IsEnabled()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is not string) return false;
            // An odd first byte means the user switched it off in Windows' Startup apps list.
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } b || (b[0] & 1) == 0;
        }
        catch { return false; }
    }

    public static void Set(bool enabled, MeshApp core)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Command is { } cmd)
            {
                run.SetValue(ValueName, cmd, RegistryValueKind.String);
                using var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey);
                approved.SetValue(ValueName, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
            }
            else
            {
                run.DeleteValue(ValueName, false);
                using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, true);
                approved?.DeleteValue(ValueName, false);
            }
            core.Log.Info("App", enabled ? "Start with Windows: on" : "Start with Windows: off");
        }
        catch (Exception ex) { core.Log.Warn("App", "Couldn't change start-with-Windows: " + ex.Message); }
    }

    /// <summary>Keeps the entry pointing at this program if it was moved or renamed.</summary>
    public static void RefreshPath(MeshApp core)
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (run?.GetValue(ValueName) is string current && Command is { } cmd && !string.Equals(current, cmd, StringComparison.OrdinalIgnoreCase))
                run.SetValue(ValueName, cmd, RegistryValueKind.String);
        }
        catch (Exception ex) { core.Log.Debug("App", "Start-with-Windows path check failed: " + ex.Message); }
    }
}
