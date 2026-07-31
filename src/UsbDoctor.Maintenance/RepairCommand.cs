using System.Diagnostics;

namespace UsbDoctor.Maintenance;

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
/// <b>Elevation.</b> The UI must never run as Administrator, so an elevated command is
/// launched as a separate process through the shell's <c>runas</c> verb, with one UAC
/// prompt each. Standard output cannot be redirected across that boundary, so the
/// command is run under <c>cmd /c</c> with its output sent to a temp file that this
/// process reads afterwards.
/// </para>
/// <para>
/// This is the interim arrangement. The roadmap's elevated worker with a named-pipe
/// channel is the real answer, and when it exists this class should route through it
/// and lose the temp file entirely.
/// </para>
/// </remarks>
public static class RepairCommandRunner
{
    public static async Task<RepairResult> RunAsync(
        RepairCommand command, CancellationToken ct = default)
    {
        var transcript = Path.Combine(
            Path.GetTempPath(), $"usbdoctor-{command.Id}-{Guid.NewGuid():N}.txt");

        try
        {
            var info = command.NeedsElevation
                ? Elevated(command, transcript)
                : Direct(command, transcript);

            using var process = Process.Start(info);

            if (process is null)
                return new RepairResult(command, Started: false, -1, string.Empty, "The command would not start.");

            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            var output = ReadTranscript(transcript);

            return new RepairResult(command, Started: true, process.ExitCode, output, null);
        }
        catch (Exception ex)
        {
            // A refused UAC prompt lands here. It is a choice the user made, not a
            // fault, and the section says so rather than reporting a failure.
            var refused = ex is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 };

            return new RepairResult(
                command, Started: false, -1, string.Empty,
                refused ? "Cancelled at the Administrator prompt." : ex.Message);
        }
        finally
        {
            try { if (File.Exists(transcript)) File.Delete(transcript); } catch { }
        }
    }

    /// <remarks>
    /// UseShellExecute with the runas verb is what raises the UAC prompt. It also
    /// rules out redirection, which is why the transcript exists.
    /// </remarks>
    private static ProcessStartInfo Elevated(RepairCommand command, string transcript) =>
        new("cmd.exe", $"/c \"{command.Executable} {command.Arguments} > \"{transcript}\" 2>&1\"")
        {
            UseShellExecute = true,
            Verb = "runas",
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

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
