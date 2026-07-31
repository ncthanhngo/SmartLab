using Microsoft.Win32;

namespace SmartLab.Maintenance;

/// <summary>Where a startup entry lives, which decides how it is disabled.</summary>
public enum StartupOrigin
{
    /// <summary>A value under a <c>Run</c> key.</summary>
    RunKey,

    /// <summary>A shortcut in a Startup folder.</summary>
    StartupFolder,

    /// <summary>A scheduled task with a logon trigger.</summary>
    ScheduledTask,
}

/// <param name="PerUser">False for machine-wide, which this process cannot change.</param>
public sealed record StartupItem(
    string Name, string Command, StartupOrigin Origin, bool PerUser, string Location)
{
    /// <summary>
    /// True when the entry belongs to Windows rather than to something installed.
    /// </summary>
    /// <remarks>
    /// Listed but never proposed. Disabling one of these breaks a part of the
    /// operating system in a way the user has no obvious route back from.
    /// </remarks>
    public bool IsWindowsOwned
    {
        get
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

            return Command.Contains(windows, StringComparison.OrdinalIgnoreCase) ||
                   Name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
                   Name.Equals("SecurityHealth", StringComparison.OrdinalIgnoreCase);
        }
    }

    public string OriginText => Origin switch
    {
        StartupOrigin.RunKey => PerUser ? "Run key (you)" : "Run key (all users)",
        StartupOrigin.StartupFolder => PerUser ? "Startup folder (you)" : "Startup folder (all users)",
        _ => "Scheduled task",
    };
}

/// <summary>
/// Everything that runs when someone logs in.
/// </summary>
/// <remarks>
/// All four sources, because reading only the obvious one is the classic mistake -
/// the same one the program scanner avoids by reading all three uninstall views. An
/// entry the tool cannot see is an entry the user cannot turn off.
/// </remarks>
public static class StartupItemScanner
{
    private const string RunPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOncePath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";

    /// <summary>Where a disabled value is parked so it can be put back.</summary>
    public const string BackupPath = @"SOFTWARE\SmartLab\DisabledStartup";

    public static IReadOnlyList<StartupItem> Scan()
    {
        var items = new List<StartupItem>();

        ReadRunKey(Registry.CurrentUser, RunPath, perUser: true, items);
        ReadRunKey(Registry.CurrentUser, RunOncePath, perUser: true, items);
        ReadRunKey(Registry.LocalMachine, RunPath, perUser: false, items);
        ReadRunKey(Registry.LocalMachine, RunOncePath, perUser: false, items);

        ReadStartupFolder(Environment.SpecialFolder.Startup, perUser: true, items);
        ReadStartupFolder(Environment.SpecialFolder.CommonStartup, perUser: false, items);

        return items;
    }

    private static void ReadRunKey(RegistryKey hive, string path, bool perUser, List<StartupItem> items)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            if (key is null) return;

            foreach (var name in key.GetValueNames())
            {
                var command = key.GetValue(name)?.ToString() ?? string.Empty;
                if (command.Length == 0) continue;

                items.Add(new StartupItem(
                    name, command, StartupOrigin.RunKey, perUser, $@"{hive.Name}\{path}"));
            }
        }
        catch
        {
            // A hive this process may not read contributes nothing rather than
            // stopping the other three.
        }
    }

    private static void ReadStartupFolder(
        Environment.SpecialFolder folder, bool perUser, List<StartupItem> items)
    {
        try
        {
            var path = Environment.GetFolderPath(folder);
            if (path.Length == 0 || !Directory.Exists(path)) return;

            foreach (var file in Directory.GetFiles(path))
            {
                // desktop.ini describes the folder, it does not start anything.
                if (Path.GetFileName(file).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    continue;

                items.Add(new StartupItem(
                    Path.GetFileNameWithoutExtension(file), file,
                    StartupOrigin.StartupFolder, perUser, path));
            }
        }
        catch
        {
            // Same reasoning as above.
        }
    }
}

/// <summary>
/// Turns a startup entry off and back on again.
/// </summary>
/// <remarks>
/// <para>
/// Disabling moves the value to a backup key rather than deleting it, so it can be
/// restored exactly. That matters more than it looks: a Run value's quoting is
/// load-bearing, and a restore that loses a pair of quotes leaves a program that
/// starts with the wrong arguments or not at all.
/// </para>
/// <para>
/// Only per-user entries can be changed. This app runs as the invoking user by
/// design, and a machine-wide entry needs Administrator - the same distinction the
/// program list already draws.
/// </para>
/// </remarks>
public static class StartupItemToggle
{
    public static bool CanChange(StartupItem item) =>
        item is { PerUser: true, Origin: StartupOrigin.RunKey, IsWindowsOwned: false };

    public static bool Disable(StartupItem item, out string? error)
    {
        error = null;

        if (!CanChange(item))
        {
            error = item.IsWindowsOwned
                ? "That entry belongs to Windows."
                : "That entry needs Administrator.";

            return false;
        }

        try
        {
            using var backup = Registry.CurrentUser.CreateSubKey(StartupItemScanner.BackupPath);
            using var run = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);

            if (run is null)
            {
                error = "The Run key could not be opened.";
                return false;
            }

            // Written to the backup before removal, never the other way round: a
            // failure between the two would otherwise lose the value entirely.
            backup.SetValue(item.Name, item.Command, RegistryValueKind.String);
            run.DeleteValue(item.Name, throwOnMissingValue: false);

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool Restore(string name, out string? error)
    {
        error = null;

        try
        {
            using var backup = Registry.CurrentUser.OpenSubKey(StartupItemScanner.BackupPath, writable: true);

            if (backup?.GetValue(name) is not string command)
            {
                error = "No backup of that entry.";
                return false;
            }

            using var run = Registry.CurrentUser.CreateSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");

            run.SetValue(name, command, RegistryValueKind.String);
            backup.DeleteValue(name, throwOnMissingValue: false);

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Entries this app has disabled, so they can be put back.</summary>
    public static IReadOnlyList<StartupItem> Disabled()
    {
        var items = new List<StartupItem>();

        try
        {
            using var backup = Registry.CurrentUser.OpenSubKey(StartupItemScanner.BackupPath);
            if (backup is null) return items;

            foreach (var name in backup.GetValueNames())
            {
                items.Add(new StartupItem(
                    name, backup.GetValue(name)?.ToString() ?? string.Empty,
                    StartupOrigin.RunKey, PerUser: true, StartupItemScanner.BackupPath));
            }
        }
        catch
        {
            // Nothing restorable is a valid answer.
        }

        return items;
    }
}
