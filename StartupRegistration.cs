// StartupRegistration.cs
//
// Manages a value under HKCU\Software\Microsoft\Windows\CurrentVersion\Run
// so Windows launches this app on user sign-in. Per-user (HKCU) means no
// admin elevation needed.
//
// The path always reflects the currently-running .exe at Save time, so
// moving the .exe and re-saving Settings updates the registry to match.

using Microsoft.Win32;
using System.Windows.Forms;

namespace MartianGamesAlerts;

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MartianGamesAlerts";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) != null;
        }
        catch { return false; }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key == null) return;

            if (enabled)
            {
                var exePath = Environment.ProcessPath ?? Application.ExecutablePath;
                // Quote the path in case it contains spaces (it usually
                // will, somewhere in the user profile).
                key.SetValue(ValueName, $"\"{exePath}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch { /* registry can fail for many reasons; swallow */ }
    }
}
