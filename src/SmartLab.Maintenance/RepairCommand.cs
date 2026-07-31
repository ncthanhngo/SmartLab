using System.Diagnostics;

namespace SmartLab.Maintenance;

/// <summary>One of Windows' own repair tools, run as itself.</summary>
/// <param name="Arguments">Fixed. Nothing here composes arguments from user input.</param>
public sealed record RepairCommand(
    string Id, string Title, string Detail, string Executable, string Arguments, bool NeedsElevation)
{
    /// <summary>
    /// The four repair tools Windows actually has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The macOS section this mirrors repairs permissions and rebuilds Spotlight.
    /// Neither exists here, so this maps to the commands that do - and every one is a
    /// Microsoft tool invoked as itself. Nothing in this file reimplements a repair,
    /// in the same spirit as handing removal to the vendor's own uninstaller.
    /// </para>
    /// <para>
    /// They are offered one at a time and never as a batch. Running DISM after SFC is
    /// a sequence an operator chooses when the first one reports something, not one
    /// this app should assume on their behalf.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<RepairCommand> All { get; } =
    [
        new("sfc", "Verify system files",
            "Checks protected system files against Windows' own catalogue and replaces any that differ.",
            "sfc.exe", "/scannow", NeedsElevation: true),

        new("dism", "Repair the component store",
            "Repairs the store SFC restores from. Worth running when SFC reports files it could not fix.",
            "DISM.exe", "/Online /Cleanup-Image /RestoreHealth", NeedsElevation: true),

        new("dns", "Flush the DNS cache",
            "Clears resolved addresses. Fixes a site that resolves to somewhere it has since moved from.",
            "ipconfig.exe", "/flushdns", NeedsElevation: false),

        // Read-only, and that is not a limitation to relax later. /f takes the volume
        // offline and can demand a reboot, which is not something a button labelled
        // "check" should decide for someone.
        new("chkdsk", "Check the system drive",
            "Scans for filesystem errors without repairing them or taking the drive offline.",
            "chkdsk.exe", "/scan", NeedsElevation: true),
    ];
}

/// <param name="Output">Everything the tool printed, verbatim.</param>
public sealed record RepairResult(RepairCommand Command, bool Started, int ExitCode, string Output, string? Error);

/// <summary>
/// Runs a repair command and captures what it said.
/// </summary>
/// <remarks>
/// <para>
/// This path is for the commands that need no elevation. Anything requiring
/// Administrator goes through <see cref="ElevatedWorkerClient"/> instead, which runs
/// it inside a single elevated worker - one prompt for the session, and output that
/// can actually be captured, since redirection does not cross an elevation boundary.
/// </para>
/// <para>
/// Output still goes through a transcript rather than a redirected pipe. These tools
/// write in the OEM codepage and pad with nul bytes, and reading the file back is
/// what lets that be dealt with in one place.
/// </para>
/// </remarks>
public static class RepairCommandRunner
{
    public static async Task<RepairResult> RunAsync(
        RepairCommand command, CancellationToken ct = default)
    {
        var transcript = Path.Combine(
            Path.GetTempPath(), $"smartlab-{command.Id}-{Guid.NewGuid():N}.txt");

        try
        {
            using var process = Process.Start(Direct(command, transcript));

            if (process is null)
                return new RepairResult(command, Started: false, -1, string.Empty, "The command would not start.");

            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            var output = ReadTranscript(transcript);

            return new RepairResult(command, Started: true, process.ExitCode, output, null);
        }
        catch (Exception ex)
        {
            return new RepairResult(command, Started: false, -1, string.Empty, ex.Message);
        }
        finally
        {
            try { if (File.Exists(transcript)) File.Delete(transcript); } catch { }
        }
    }

    private static ProcessStartInfo Direct(RepairCommand command, string transcript) =>
        new("cmd.exe", $"/c \"{command.Executable} {command.Arguments} > \"{transcript}\" 2>&1\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

    /// <remarks>
    /// These tools write UTF-16 on some systems and the OEM codepage on others, and
    /// SFC in particular emits nul bytes between characters. Stripping them is cruder
    /// than detecting the encoding and survives both.
    /// </remarks>
    private static string ReadTranscript(string path)
    {
        try
        {
            if (!File.Exists(path)) return string.Empty;

            var text = File.ReadAllText(path);

            return text.Replace("\0", string.Empty).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }
}
