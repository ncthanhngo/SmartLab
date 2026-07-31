using System.Diagnostics;
using Microsoft.Win32;

namespace SmartLab.App;

/// <summary>
/// Registers the app to start with Windows, per user.
/// </summary>
/// <remarks>
/// <para>
/// Uses the per-user Run key, never the machine-wide one. A machine-wide entry
/// needs Administrator and would launch for every account on a shared lab PC,
/// which is not something a checkbox should decide.
/// </para>
/// <para>
/// This is the only part of the app that writes outside its own data folder, so
/// it is kept in one place: unticking the box removes the value and leaves no
/// trace behind.
/// </para>
/// </remarks>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Smart Lab";

    /// <summary>The executable to launch, quoted so a path with spaces survives.</summary>
    private static string CommandLine
    {
        get
        {
            var path = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            return string.IsNullOrEmpty(path) ? string.Empty : $"\"{path}\" --tray";
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string existing && existing.Length > 0;
        }
        catch
        {
            // A locked-down or policy-managed key is not an error worth surfacing;
            // it just means the feature is unavailable.
            return false;
        }
    }

    /// <summary>Adds or removes the entry. Returns false if the registry refused.</summary>
    public static bool Set(bool enabled, out string? error)
    {
        error = null;

        var command = CommandLine;
        if (enabled && string.IsNullOrEmpty(command))
        {
            error = "Could not determine the executable path.";
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                ?? throw new InvalidOperationException("Run key is unavailable.");

            if (enabled) key.SetValue(ValueName, command, RegistryValueKind.String);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
