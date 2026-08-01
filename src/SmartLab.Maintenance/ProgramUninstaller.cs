using System.Diagnostics;

namespace SmartLab.Maintenance;

public enum UninstallOutcome { Completed, NoUninstaller, LaunchFailed, Cancelled }

/// <summary>What a step says about itself, which is what decides its colour.</summary>
public enum UninstallStepKind { Info, Ok, Warning, Failed }

/// <summary>
/// One line of the commentary an uninstall produces while it is happening.
/// </summary>
/// <remarks>
/// Reported rather than returned, because the whole point is that it arrives while
/// the work is still running. A list handed back at the end describes an uninstall
/// nobody could watch - and watching is what tells an operator whether a vendor's
/// uninstaller is working or waiting for an answer on a window they cannot see.
/// </remarks>
public sealed record UninstallStep(UninstallStepKind Kind, string Text);

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
    /// <param name="progress">
    /// Where the running commentary goes. The command line is reported verbatim
    /// because it is the one fact that explains everything that follows: a silent
    /// switch that turned out not to be silent, or an msiexec argument that opens a
    /// repair dialog, is visible there and nowhere else.
    /// </param>
    public async Task<UninstallRunResult> RunAsync(
        InstalledProgram program, bool quiet,
        IProgress<UninstallStep>? progress = null, CancellationToken ct = default)
    {
        var chosen = quiet && !string.IsNullOrWhiteSpace(program.QuietUninstallString)
            ? program.QuietUninstallString
            : program.UninstallString;

        var command = UninstallCommandParser.Parse(chosen);

        if (command.IsEmpty)
        {
            Say(progress, UninstallStepKind.Warning,
                $"{program.DisplayName} registered no uninstall command.");

            return new UninstallRunResult(program, UninstallOutcome.NoUninstaller);
        }

        Say(progress, UninstallStepKind.Info,
            $"Running: {$"{command.FileName} {command.Arguments}".Trim()}");

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
            {
                Say(progress, UninstallStepKind.Failed, "The uninstaller did not start.");
                return new UninstallRunResult(program, UninstallOutcome.LaunchFailed, null, "Process did not start.");
            }

            Say(progress, UninstallStepKind.Info,
                $"Started as process {process.Id}. Answer any prompt it shows.");

            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            // A non-zero code is reported, not judged. Vendors use them for "the user
            // cancelled" as readily as for "it broke", and the leftover scan below is
            // what actually says whether the program is gone.
            Say(progress,
                process.ExitCode == 0 ? UninstallStepKind.Ok : UninstallStepKind.Warning,
                $"Uninstaller finished with exit code {process.ExitCode}.");

            return new UninstallRunResult(program, UninstallOutcome.Completed, process.ExitCode);
        }
        catch (OperationCanceledException)
        {
            // The uninstaller keeps running; only the wait was abandoned.
            Say(progress, UninstallStepKind.Warning,
                "Stopped waiting. The uninstaller may still be running.");

            return new UninstallRunResult(program, UninstallOutcome.Cancelled, null,
                "Stopped waiting. The uninstaller may still be running.");
        }
        catch (Exception ex)
        {
            Say(progress, UninstallStepKind.Failed, $"Could not start it: {ex.Message}");
            return new UninstallRunResult(program, UninstallOutcome.LaunchFailed, null, ex.Message);
        }
    }

    /// <summary>
    /// Reports what survived the uninstaller.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow: only the install folder the program registered and its
    /// own uninstall key. Nothing here is guessed at, so nothing here needs grading.
    ///
    /// It is also not enough on its own - a program that registered no install
    /// location leaves this with nothing to check - which is what
    /// <see cref="DeepTraceScanner"/> is for. That one does go looking, and answers
    /// the danger of a name-based sweep by saying how it found each thing rather than
    /// by not looking.
    /// </remarks>
    /// <param name="progress">
    /// Reports every place this looked, including the ones that came back clean. A
    /// scan that only names what it found leaves the operator unable to tell a
    /// thorough search from one that never ran, and "nothing left behind" is a claim
    /// worth being able to check.
    /// </param>
    public IReadOnlyList<AppTrace> ScanLeftovers(
        InstalledProgram program, IProgress<UninstallStep>? progress = null)
    {
        var leftovers = new List<AppTrace>();

        if (string.IsNullOrWhiteSpace(program.InstallLocation))
        {
            Say(progress, UninstallStepKind.Info,
                "No install folder was registered, so there is no folder to check.");
        }
        else
        {
            Say(progress, UninstallStepKind.Info, $"Checking folder: {program.InstallLocation}");

            if (probe.DirectoryExists(program.InstallLocation))
            {
                var trace = new AppTrace(
                    TraceKind.Directory,
                    program.InstallLocation,
                    $"Install folder left by {program.DisplayName}")
                {
                    Exists = true,
                    SizeBytes = probe.DirectorySize(program.InstallLocation),
                };

                leftovers.Add(trace);

                Say(progress, UninstallStepKind.Warning,
                    $"Still there: {program.InstallLocation}" +
                    (trace.SizeText.Length > 0 ? $" ({trace.SizeText})" : string.Empty));
            }
            else
            {
                Say(progress, UninstallStepKind.Ok, $"Gone: {program.InstallLocation}");
            }
        }

        Say(progress, UninstallStepKind.Info, $"Checking registry key: {program.RegistryKeyPath}");

        if (probe.RegistryKeyExists(program.RegistryKeyPath))
        {
            leftovers.Add(new AppTrace(
                TraceKind.RegistryKey,
                program.RegistryKeyPath,
                $"Uninstall entry left by {program.DisplayName}")
            {
                Exists = true,
            });

            Say(progress, UninstallStepKind.Warning, $"Still there: {program.RegistryKeyPath}");
        }
        else
        {
            Say(progress, UninstallStepKind.Ok, $"Gone: {program.RegistryKeyPath}");
        }

        // Said outright, because the list above is short by design and a short list
        // can be mistaken for a shallow one. Nothing else is searched: hunting the
        // machine for the vendor's name is how a cleaner proposes to delete a shared
        // runtime or another product from the same publisher.
        Say(progress, UninstallStepKind.Info,
            "Only what the program registered is checked - nothing is searched for by name.");

        return leftovers;
    }

    private static void Say(IProgress<UninstallStep>? progress, UninstallStepKind kind, string text) =>
        progress?.Report(new UninstallStep(kind, text));
}
