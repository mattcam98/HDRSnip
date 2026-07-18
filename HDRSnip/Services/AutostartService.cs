using System.IO;
using Microsoft.Win32;

namespace HDRSnip.Services;

public static class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HDRSnip";

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKey);

        if (enabled)
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                exe = Path.Combine(AppContext.BaseDirectory, "HDRSnip.exe");
            key.SetValue(ValueName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is not null;
    }
}
