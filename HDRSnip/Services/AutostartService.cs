using System.IO;
using Microsoft.Win32;

namespace HDRSnip.Services;

/// <summary>Start-with-Windows via the per-user Run key. Never needs elevation.</summary>
public static class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HDRSnip";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null)
                return;

            if (enabled)
            {
                var executable = Environment.ProcessPath;
                if (string.IsNullOrEmpty(executable))
                    executable = Path.Combine(AppContext.BaseDirectory, "HDRSnip.exe");
                key.SetValue(ValueName, $"\"{executable}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            App.LogError("Autostart", ex);
        }
    }
}
