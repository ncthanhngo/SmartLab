using System.Diagnostics;

namespace UsbDoctor.Maintenance;

public enum UninstallOutcome { Completed, NoUninstaller, LaunchFailed, Cancelled }

/// <param name="ExitCode">Vendor uninstaller exit code, or null if it never ran.</param>
public sealed record UninstallRunResult(
    InstalledProgram Program,
    UninstallOutcome Outcome,
    int? ExitCode = null,
    string? Detail = null);

/// <summary>
/// Runs a program's own uninstaller, then reports what it left behind.
/// </summary>
/// <remarks>
/// The vendor's uninstaller always runs first and is never bypassed. Deleting a
/// program's files directly leaves its registration, its services and its drivers
/// in place, which is worse than not uninstalling at all. Leftover cleanup is a
/// second, separate step over what actually remains.
/// </remarks>
public sealed class ProgramUninstaller(ITraceProbe probe)
{
    /// <summary>
    /// Launches the uninstaller and waits for it.
    /// </summary>
    /// <param name="quiet">
    /// Prefer the vendor's silent command when they registered one. Many vendors
    /// register none, in which case the interactive command is used and the user
    /// has to answer its prompts - this method cannot make that not be true.
    /// </param>
    public async Task<UninstallRunResult> RunAsync(
        InstalledProgram program, bool quiet, CancellationToken ct = default)
    {
        var chosen = quiet && !string.IsNullOrWhiteSpace(program.QuietUninstallString)
            ? program.QuietUninstallString
            : program.UninstallString;

        var command = UninstallCommandParser.Parse(chosen);
        if (command.IsEmpty)
            return new UninstallRunResult(program, UninstallOutcome.NoUninstaller);

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = command.FileName,
                Arguments = command.Arguments,

                // UseShellExecute so the uninstaller gets its own elevation prompt
                // if it needs one. Suppressing that would just make it fail.
                UseShellExecute = true,
            });

            if (process is null)
                return new UninstallRunResult(program, UninstallOutcome.LaunchFailed, null, "Process did not start.");

            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            return new UninstallRunResult(program, UninstallOutcome.Completed, process.ExitCode);
        }
        catch (OperationCanceledException)
        {
            // The uninstaller keeps running; only the wait was abandoned.
            return new UninstallRunResult(program, UninstallOutcome.Cancelled, null,
                "Stopped waiting. The uninstaller may still be running.");
        }
        catch (Exception ex)
        {
            return new UninstallRunResult(program, UninstallOutcome.LaunchFailed, null, ex.Message);
        }
    }

    /// <summary>
    /// Reports what survived the uninstaller.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow: only the install folder the program registered and its
    /// own uninstall key. It does not go hunting the filesystem or registry for the
    /// vendor's name. A name-based sweep is how a cleaner ends up proposing to
    /// delete a shared runtime, another product from the same publisher, or a user
    /// folder that happens to match - and the operator has no way to tell which
    /// suggestions are safe.
    /// </remarks>
    public IReadOnlyList<AppTrace> ScanLeftovers(InstalledProgram program)
    {
        var leftovers = new List<AppTrace>();

        if (!string.IsNullOrWhiteSpace(program.InstallLocation) &&
            probe.DirectoryExists(program.InstallLocation))
        {
            leftovers.Add(new AppTrace(
                TraceKind.Directory,
                program.InstallLocation,
                $"Install folder left by {program.DisplayName}")
            {
                Exists = true,
                SizeBytes = probe.DirectorySize(program.InstallLocation),
            });
        }

        if (probe.RegistryKeyExists(program.RegistryKeyPath))
        {
            leftovers.Add(new AppTrace(
                TraceKind.RegistryKey,
                program.RegistryKeyPath,
                $"Uninstall entry left by {program.DisplayName}")
            {
                Exists = true,
            });
        }

        return leftovers;
    }
}
