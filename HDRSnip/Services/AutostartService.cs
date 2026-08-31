using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace HDRSnip.Services;

/// <summary>
/// Start-with-Windows. The Store/MSIX build uses a <c>windows.startupTask</c>
/// declared in the package manifest — the only activation path Explorer honours
/// for WindowsApps. Unpackaged installs use the per-user Run key. Neither
/// needs elevation.
/// </summary>
public static class AutostartService
{
    internal const string TaskId = "HDRSnipStartup";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "HDRSnip";

    /// <summary>Windows 11 Startup Apps flag: 02 = enabled. A Run value without this can exist and still be skipped at logon.</summary>
    private static readonly byte[] ApprovedEnabled =
        [0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    private static readonly Lazy<bool> Packaged = new(static () =>
    {
        try
        {
            return !string.IsNullOrEmpty(Package.Current.Id.FamilyName);
        }
        catch (Exception)
        {
            return false;
        }
    });

    public static bool IsPackaged => Packaged.Value;

    public static async Task<bool> IsEnabledAsync()
    {
        if (IsPackaged)
        {
            var task = await TryGetStartupTaskAsync().ConfigureAwait(true);
            return task is not null && IsOn(task.State);
        }

        return RunKeyPointsAtExistingExe();
    }

    /// <summary>
    /// Make the OS registration match the saved preference. Safe on every launch:
    /// refreshes a stale unpackaged path, and enables a packaged task that was
    /// declared but never requested (the old Run-key write is a no-op inside MSIX).
    /// </summary>
    public static Task ReconcileAsync(bool desired) =>
        SetEnabledAsync(desired, interactive: false);

    public static Task<AutostartStatus> SetEnabledAsync(bool enabled) =>
        SetEnabledAsync(enabled, interactive: true);

    public static void OpenWindowsStartupSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.LogError("Autostart.Settings", ex);
        }
    }

    private static async Task<AutostartStatus> SetEnabledAsync(bool enabled, bool interactive)
    {
        try
        {
            return IsPackaged
                ? await SetPackagedAsync(enabled, interactive).ConfigureAwait(true)
                : SetUnpackaged(enabled);
        }
        catch (Exception ex)
        {
            App.LogError("Autostart", ex);
            return new AutostartStatus(false,
                "HDRSnip could not change the sign-in setting.\n\n" + ex.Message);
        }
    }

    private static async Task<AutostartStatus> SetPackagedAsync(bool enabled, bool interactive)
    {
        var task = await TryGetStartupTaskAsync().ConfigureAwait(true);
        if (task is null)
        {
            return interactive
                ? new AutostartStatus(false,
                    "This install cannot register a sign-in task. Update HDRSnip from the Store and try again.")
                : AutostartStatus.Off;
        }

        if (enabled)
        {
            if (IsOn(task.State))
                return AutostartStatus.On;

            if (task.State is StartupTaskState.DisabledByUser or StartupTaskState.DisabledByPolicy)
            {
                return interactive
                    ? new AutostartStatus(false, UserBlockedMessage(task.State), OfferStartupSettings: true)
                    : AutostartStatus.Off;
            }

            var state = await task.RequestEnableAsync();
            if (IsOn(state))
                return AutostartStatus.On;

            return interactive
                ? new AutostartStatus(false, UserBlockedMessage(state), OfferStartupSettings: true)
                : AutostartStatus.Off;
        }

        if (task.State is StartupTaskState.Enabled)
            task.Disable();

        return AutostartStatus.Off;
    }

    private static AutostartStatus SetUnpackaged(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (key is null)
            return new AutostartStatus(false, "Could not open the Windows sign-in registry key.");

        if (enabled)
        {
            key.SetValue(ValueName, $"\"{CurrentExecutable()}\"");
            WriteApproved(enabled: true);
            return AutostartStatus.On;
        }

        key.DeleteValue(ValueName, throwOnMissingValue: false);
        WriteApproved(enabled: false);
        return AutostartStatus.Off;
    }

    private static async Task<StartupTask?> TryGetStartupTaskAsync()
    {
        try
        {
            return await StartupTask.GetAsync(TaskId);
        }
        catch (Exception ex)
        {
            App.LogError("Autostart.GetTask", ex);
            return null;
        }
    }

    private static bool IsOn(StartupTaskState state) =>
        state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;

    private static string UserBlockedMessage(StartupTaskState state) =>
        state == StartupTaskState.DisabledByPolicy
            ? "Startup is blocked by policy on this PC."
            : "Windows is blocking HDRSnip from starting when you sign in.\n\nEnable it under Settings → Apps → Startup.";

    private static string CurrentExecutable()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
            executable = Path.Combine(AppContext.BaseDirectory, "HDRSnip.exe");
        return executable;
    }

    private static bool RunKeyPointsAtExistingExe()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            var value = key?.GetValue(ValueName) as string;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var path = ExtractPath(value);
            return path.Length > 0 && File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static string ExtractPath(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            return end > 1 ? trimmed[1..end] : trimmed.Trim('"');
        }

        var space = trimmed.IndexOf(' ');
        return space > 0 ? trimmed[..space] : trimmed;
    }

    private static void WriteApproved(bool enabled)
    {
        try
        {
            if (enabled)
            {
                using var key = Registry.CurrentUser.CreateSubKey(ApprovedKey, writable: true);
                key?.SetValue(ValueName, ApprovedEnabled, RegistryValueKind.Binary);
                return;
            }

            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            App.LogError("Autostart.Approved", ex);
        }
    }
}

public readonly record struct AutostartStatus(bool Enabled, string? Message, bool OfferStartupSettings = false)
{
    public static AutostartStatus On => new(true, null);
    public static AutostartStatus Off => new(false, null);
}
