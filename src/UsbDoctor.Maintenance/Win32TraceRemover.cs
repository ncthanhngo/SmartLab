using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace UsbDoctor.Maintenance;

/// <summary>
/// Deletes traces, deferring the one it cannot delete from inside itself.
/// </summary>
/// <param name="runningFromDirectory">
/// The folder the current process runs from. Deleting it has to wait until after
/// exit, so it is handled differently rather than silently failing.
/// </param>
public sealed class Win32TraceRemover(bool dryRun, string? runningFromDirectory = null) : ITraceRemover
{
    public bool DryRun { get; } = dryRun;

    /// <summary>Set when a removal was scheduled to run after the process exits.</summary>
    public bool HasDeferredWork { get; private set; }

    public RemovalResult Remove(AppTrace trace)
    {
        if (DryRun) return new RemovalResult(trace, RemovalOutcome.SkippedDryRun);

        try
        {
            return trace.Kind switch
            {
                TraceKind.RegistryValue => RemoveRegistryValue(trace),
                TraceKind.RegistryKey => RemoveRegistryKey(trace),
                TraceKind.File => RemoveFile(trace),
                TraceKind.Directory => RemoveDirectory(trace),
                TraceKind.DirectoryContents => EmptyDirectory(trace),
                TraceKind.RecycleBin => EmptyRecycleBin(trace),
                _ => new RemovalResult(trace, RemovalOutcome.Failed, "Unsupported trace kind."),
            };
        }
        catch (Exception ex)
        {
            return new RemovalResult(trace, RemovalOutcome.Failed, ex.Message);
        }
    }

    private static RemovalResult RemoveRegistryValue(AppTrace trace)
    {
        if (!RegistryPath.TrySplit(trace.Location, out var hive, out var subKey))
            return new RemovalResult(trace, RemovalOutcome.Failed, "Unrecognised registry hive.");

        using var key = hive!.OpenSubKey(subKey, writable: true);
        if (key is null) return new RemovalResult(trace, RemovalOutcome.NotFound);

        if (key.GetValue(trace.ValueName) is null)
            return new RemovalResult(trace, RemovalOutcome.NotFound);

        key.DeleteValue(trace.ValueName!, throwOnMissingValue: false);
        return new RemovalResult(trace, RemovalOutcome.Removed);
    }

    private static RemovalResult RemoveRegistryKey(AppTrace trace)
    {
        if (!RegistryPath.TrySplit(trace.Location, out var hive, out var subKey))
            return new RemovalResult(trace, RemovalOutcome.Failed, "Unrecognised registry hive.");

        using (var check = hive!.OpenSubKey(subKey))
        {
            if (check is null) return new RemovalResult(trace, RemovalOutcome.NotFound);
        }

        hive.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
        return new RemovalResult(trace, RemovalOutcome.Removed);
    }

    private static RemovalResult RemoveFile(AppTrace trace)
    {
        if (!File.Exists(trace.Location)) return new RemovalResult(trace, RemovalOutcome.NotFound);

        File.SetAttributes(trace.Location, FileAttributes.Normal);
        File.Delete(trace.Location);
        return new RemovalResult(trace, RemovalOutcome.Removed);
    }

    /// <summary>
    /// Deletes everything inside a directory, keeping the directory.
    /// </summary>
    /// <remarks>
    /// Locked files are expected, not exceptional: a temp folder on a live machine
    /// always holds handles that something still owns. Each failure costs that one
    /// entry, and the count is reported so the number reflects what actually went
    /// rather than what was attempted.
    /// </remarks>
    private static RemovalResult EmptyDirectory(AppTrace trace)
    {
        if (!Directory.Exists(trace.Location)) return new RemovalResult(trace, RemovalOutcome.NotFound);

        int removed = 0, locked = 0;

        foreach (var file in Directory.EnumerateFiles(trace.Location, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        }))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
                removed++;
            }
            catch
            {
                locked++;
            }
        }

        // Subdirectories are swept after their files so the empty ones can go.
        // Reparse points are skipped: following a junction out of a temp folder is
        // how a cleaner deletes something that was never inside it.
        foreach (var directory in Directory.EnumerateDirectories(trace.Location))
        {
            try
            {
                var info = new DirectoryInfo(directory);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                locked++;
            }
        }

        var detail = locked > 0
            ? $"{removed} file(s) removed, {locked} still in use"
            : $"{removed} file(s) removed";

        return new RemovalResult(trace, RemovalOutcome.Removed, detail);
    }

    private static RemovalResult EmptyRecycleBin(AppTrace trace) =>
        RecycleBin.Empty(out var error)
            ? new RemovalResult(trace, RemovalOutcome.Removed)
            : new RemovalResult(trace, RemovalOutcome.Failed, error);

    private RemovalResult RemoveDirectory(AppTrace trace)
    {
        if (!Directory.Exists(trace.Location)) return new RemovalResult(trace, RemovalOutcome.NotFound);

        // A process cannot delete the folder it is executing from, so that one is
        // handed to a detached script that waits for this process to end. Attempting
        // it inline would fail with a sharing violation and look like a bug.
        if (IsRunningFrom(trace.Location))
        {
            ScheduleDeleteAfterExit(trace.Location);
            HasDeferredWork = true;

            return new RemovalResult(trace, RemovalOutcome.Deferred,
                "Removed after the app closes - it cannot delete the folder it is running from.");
        }

        Directory.Delete(trace.Location, recursive: true);
        return new RemovalResult(trace, RemovalOutcome.Removed);
    }

    private bool IsRunningFrom(string directory)
    {
        if (string.IsNullOrEmpty(runningFromDirectory)) return false;

        var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runningFromDirectory));

        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Spawns a detached shell that waits for this process to exit, then removes
    /// the folder and itself.
    /// </summary>
    /// <remarks>
    /// The wait is a ping loop rather than a fixed sleep, because a fixed sleep
    /// either races the shutdown or makes the user watch a console for no reason.
    /// The script deletes itself last so nothing is left in TEMP.
    /// </remarks>
    private static void ScheduleDeleteAfterExit(string directory)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"usbdoctor-cleanup-{Guid.NewGuid():N}.cmd");
        var pid = Environment.ProcessId;

        var script = $"""
            @echo off
            :wait
            tasklist /FI "PID eq {pid}" 2>nul | find "{pid}" >nul
            if not errorlevel 1 (
                ping -n 2 127.0.0.1 >nul
                goto wait
            )
            rmdir /s /q "{directory}"
            del "%~f0"
            """;

        File.WriteAllText(scriptPath, script);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{scriptPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
        });
    }
}
