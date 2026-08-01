using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SmartLab.Maintenance;

/// <summary>What Defender could tell us, which is not always a verdict.</summary>
public enum DefenderState
{
    /// <summary>The scan ran and found nothing.</summary>
    Clean,

    /// <summary>The scan ran and found something.</summary>
    ThreatsFound,

    /// <summary>
    /// The scan did not run.
    /// </summary>
    /// <remarks>
    /// The state this whole class exists to keep separate from <see cref="Clean"/>.
    /// A security section that reports "clean" because it could not run is worse than
    /// one that reports nothing at all.
    /// </remarks>
    CouldNotRun,

    /// <summary>Defender is turned off or replaced by another product.</summary>
    NotAvailable,
}

/// <param name="Output">What MpCmdRun printed, kept for the operator to read.</param>
public sealed record DefenderResult(DefenderState State, string Detail, string Output)
{
    /// <summary>Threat names MpCmdRun listed, if any.</summary>
    public IReadOnlyList<string> Threats { get; init; } = [];

    /// <summary>
    /// How many threats the scan reported, named or not.
    /// </summary>
    /// <remarks>
    /// A custom scan that finds something usually prints a count and no names at all -
    /// so the count is what proves a scan was not clean, and <see cref="Threats"/> can
    /// legitimately be shorter than this.
    /// </remarks>
    public int ThreatCount { get; init; }
}

/// <summary>
/// Asks Windows Defender about a path.
/// </summary>
/// <remarks>
/// <para>
/// The codebase's rule is that this tool does not reimplement antivirus: its
/// signatures identify <i>hiding behaviour</i>, and identifying malware is delegated.
/// This is that delegation. It was documented long before it was built.
/// </para>
/// <para>
/// Argument building and output parsing are pure functions so both can be tested on a
/// machine where Defender is disabled - which is also the case they most need to get
/// right.
/// </para>
/// </remarks>
public static class DefenderBridge
{
    /// <summary>
    /// How long one path may take before the scan is abandoned.
    /// </summary>
    /// <remarks>
    /// Hours rather than minutes because a whole system drive is now a scannable path:
    /// on a mature machine that routinely runs past any figure in minutes, and a sweep
    /// killed part-way through C: reports "did not complete" for a scan that was
    /// working. Stop is what ends a scan early - a clock is only there so a wedged
    /// engine cannot hold the section for ever.
    /// </remarks>
    private static readonly TimeSpan Timeout = TimeSpan.FromHours(4);

    /// <summary>Removal is one machine-wide operation; it does not walk the disk.</summary>
    private static readonly TimeSpan RemoveTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Builds the custom-scan arguments.
    /// </summary>
    /// <remarks>
    /// ScanType 3 is a custom scan of one path. The path is quoted because a drive
    /// with a space in its mount point would otherwise become two arguments and scan
    /// something else, or nothing.
    /// </remarks>
    public static string BuildScanArguments(string path) =>
        $"-Scan -ScanType 3 -File \"{path.TrimEnd('"')}\"";

    /// <summary>
    /// The command that removes what Defender has identified.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not MpCmdRun: it has no removal switch at all. The switch whose name reads like
    /// one, <c>-RemoveDefinitions</c>, deletes Defender's own signatures - the exact
    /// opposite of what this button means, and the reason the text is built here where
    /// a test can assert that name never appears in it.
    /// </para>
    /// <para>
    /// <c>Remove-MpThreat</c> is the documented way to act on every active threat, and
    /// it names no path: what gets removed is Defender's list of what it found, not a
    /// target this app chose. It needs Administrator, so it runs behind a prompt the
    /// operator sees and can refuse.
    /// </para>
    /// <para>
    /// The two halves after it are what make the result trustworthy. PowerShell exits 0
    /// even when a cmdlet writes an error, so an access denied - exactly what an
    /// unelevated or refused run produces - would otherwise report as a successful
    /// removal; <c>$ErrorActionPreference='Stop'</c> turns that into a non-zero exit.
    /// And the threat list is read back afterwards, so success is "nothing is still
    /// active" rather than "the command returned". Anything still active exits non-zero
    /// and is named in the output.
    /// </para>
    /// <para>
    /// The read-back waits, and that is not defensive padding. <c>IsActive</c> is
    /// Defender's bookkeeping rather than the file's state: measured against a real
    /// EICAR detection, a threat whose file was already gone still read as active for
    /// seconds after <c>Remove-MpThreat</c> returned. Reading it once turns a removal
    /// that worked into "anything it named is still there", which in a security screen
    /// is exactly as bad as the false clean this whole file guards against.
    /// </para>
    /// </remarks>
    public static string BuildRemoveCommand() =>
        "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " +
        "\"$ErrorActionPreference='Stop'; Remove-MpThreat; " +
        "$deadline = (Get-Date).AddSeconds(30); " +
        "do { $active = @(Get-MpThreat | Where-Object IsActive); " +
        "if (-not $active) { break }; Start-Sleep -Seconds 2 } " +
        "while ((Get-Date) -lt $deadline); " +
        "if ($active) { $active | Format-List ThreatName,IsActive; exit 2 } " +
        "else { 'No threat is still active.' }\"";

    /// <summary>
    /// Whether a drive is one this section can scan and clean.
    /// </summary>
    /// <remarks>
    /// Fixed and removable, and nothing else. A network drive is not in this machine -
    /// scanning one reads somebody else's server over the wire and remediates on their
    /// disk. Optical media is read-only, so a detection there could be reported but
    /// never removed, and a drive that is not ready has no filesystem to walk.
    /// </remarks>
    public static bool IsScannable(DriveType type, bool isReady) =>
        isReady && type is DriveType.Fixed or DriveType.Removable;

    /// <summary>Every drive on this machine a scan can act on, in drive-letter order.</summary>
    public static IReadOnlyList<string> ScannableDrives()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(IsScannable)
                .Select(d => d.RootDirectory.FullName)
                .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static bool IsScannable(DriveInfo drive)
    {
        // IsReady throws on a drive that disappears mid-enumeration, which is exactly
        // what a removable drive does.
        try { return IsScannable(drive.DriveType, drive.IsReady); }
        catch { return false; }
    }

    /// <summary>
    /// One verdict for a sweep of several drives.
    /// </summary>
    /// <remarks>
    /// The rule the single-path case already follows, applied across a list: a drive
    /// that could not be scanned never averages away into clean. Threats win because a
    /// sweep that named something has named it whatever the other drives said, and a
    /// mix of clean and unreadable is <see cref="DefenderState.CouldNotRun"/> - part of
    /// the machine was not looked at.
    /// </remarks>
    public static DefenderState Aggregate(IReadOnlyList<DefenderState> states)
    {
        if (states.Count == 0) return DefenderState.CouldNotRun;
        if (states.Contains(DefenderState.ThreatsFound)) return DefenderState.ThreatsFound;
        if (states.All(s => s == DefenderState.NotAvailable)) return DefenderState.NotAvailable;

        return states.Any(s => s is DefenderState.CouldNotRun or DefenderState.NotAvailable)
            ? DefenderState.CouldNotRun
            : DefenderState.Clean;
    }

    /// <summary>
    /// Locates MpCmdRun.exe.
    /// </summary>
    /// <remarks>
    /// The platform copy moves with every engine update, so the versioned folder is
    /// checked first and the stable path second. Neither is guaranteed to exist:
    /// Defender can be replaced entirely.
    /// </remarks>
    public static string? FindExecutable()
    {
        var candidates = new List<string>();

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Defender");

            if (key?.GetValue("InstallLocation") as string is { Length: > 0 } installed)
                candidates.Add(Path.Combine(installed, "MpCmdRun.exe"));
        }
        catch
        {
            // Registry read denied; the fixed paths below still stand a chance.
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var platform = Path.Combine(programFiles, "Windows Defender", "Platform");

        try
        {
            if (Directory.Exists(platform))
            {
                // Newest platform folder first - that is the one in use.
                var newest = Directory.GetDirectories(platform)
                    .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

                if (newest is not null) candidates.Add(Path.Combine(newest, "MpCmdRun.exe"));
            }
        }
        catch
        {
            // Same reasoning.
        }

        candidates.Add(Path.Combine(programFiles, "Windows Defender", "MpCmdRun.exe"));

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Reads MpCmdRun's output and exit code into a state.
    /// </summary>
    /// <summary>
    /// The count line a custom scan ends with: "Scanning E:\ found 3 threats."
    /// </summary>
    /// <remarks>
    /// The line that matters most in this file. A scan that finds something and cleans
    /// it prints this, no names, and exits <b>0</b> - so reading names and exit codes
    /// alone reports "Defender found nothing" for a drive it just disinfected, which is
    /// the one mistake this section exists to avoid. Captured from a real detection;
    /// the fixture beside the tests is that transcript.
    /// </remarks>
    private static readonly Regex CountLine =
        new(@"found\s+(\d+)\s+threat", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <remarks>
    /// Exit code alone is not enough: MpCmdRun returns 2 both for "threats found" and
    /// for several failures, and 0 for a scan that found and removed one, so the text
    /// has to be read as well. Anything that is not recognisably a completed scan
    /// becomes <see cref="DefenderState.CouldNotRun"/> - never Clean by default.
    /// </remarks>
    public static DefenderResult Interpret(int exitCode, string output)
    {
        var text = output ?? string.Empty;

        if (text.Contains("Service is not running", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("disabled", StringComparison.OrdinalIgnoreCase))
        {
            return new DefenderResult(
                DefenderState.NotAvailable,
                "Defender is turned off or has been replaced by another product, so nothing was scanned.",
                text);
        }

        var threats = text
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("Threat", StringComparison.OrdinalIgnoreCase) &&
                        l.Contains(':', StringComparison.Ordinal))
            .Select(l => l[(l.IndexOf(':') + 1)..].Trim())
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var counted = CountLine.Match(text) is { Success: true } m &&
                      int.TryParse(m.Groups[1].Value, out var n)
            ? n
            : 0;

        if (threats.Length > 0)
        {
            return new DefenderResult(
                DefenderState.ThreatsFound,
                $"Defender identified {threats.Length} threat(s).",
                text)
            { Threats = threats, ThreatCount = Math.Max(threats.Length, counted) };
        }

        var finished = text.Contains("Scan finished", StringComparison.OrdinalIgnoreCase) ||
                       text.Contains("found no threats", StringComparison.OrdinalIgnoreCase);

        // A count with no names, which is what a real detection looks like. Whether it
        // exited 0 decides only whether the cleaning worked, never whether the drive
        // was clean.
        if (counted > 0)
        {
            var cleaned = text.Contains("Cleaning finished", StringComparison.OrdinalIgnoreCase);

            return new DefenderResult(
                DefenderState.ThreatsFound,
                cleaned
                    ? $"Defender found {counted} threat(s) and cleaned what it could."
                    : $"Defender found {counted} threat(s).",
                text)
            { ThreatCount = counted };
        }

        if (exitCode == 0 && finished)
            return new DefenderResult(DefenderState.Clean, "Defender found nothing.", text);

        if (exitCode == 2 && finished)
        {
            return new DefenderResult(
                DefenderState.ThreatsFound, "Defender reported findings - read its output below.", text);
        }

        return new DefenderResult(
            DefenderState.CouldNotRun,
            $"The scan did not complete (exit code {exitCode}). This is not the same as clean.",
            text);
    }

    /// <summary>
    /// Scans one path.
    /// </summary>
    /// <remarks>
    /// Defender acts on what it finds unless a scan is asked not to, and this one does
    /// not ask: the point of handing a drive to an antivirus is that the antivirus
    /// deals with what is on it. Smart Lab still removes nothing itself - what is
    /// quarantined here is Defender's decision, taken by the Defender service, and
    /// <see cref="Remove"/> is for finishing off what quarantine left behind.
    /// </remarks>
    public static DefenderResult Scan(string path, CancellationToken ct = default)
    {
        if (FindExecutable() is not { } mpCmdRun)
        {
            return new DefenderResult(
                DefenderState.NotAvailable,
                "MpCmdRun.exe was not found. Defender may be replaced by another product.",
                string.Empty);
        }

        try
        {
            var (output, exitCode) = Run(mpCmdRun, BuildScanArguments(path), ct);

            return Interpret(exitCode, output);
        }
        catch (Exception ex)
        {
            return new DefenderResult(DefenderState.CouldNotRun, ex.Message, string.Empty);
        }
    }

    /// <summary>
    /// Asks Defender to remove every threat it has active. A separate press, never automatic.
    /// </summary>
    /// <remarks>
    /// A scan quarantines; this is what clears what quarantine could not finish and
    /// what an operator means by removing it completely. It needs Administrator, so it
    /// raises a prompt - refusing that prompt is reported as a decision rather than
    /// dressed up as success.
    /// </remarks>
    public static async Task<DefenderResult> RemoveAsync(CancellationToken ct = default)
    {
        if (FindExecutable() is null)
        {
            return new DefenderResult(
                DefenderState.NotAvailable,
                "Defender was not found on this machine, so there is nothing to ask.",
                string.Empty);
        }

        var (ok, output) = await ElevatedProcess
            .RunAsync(BuildRemoveCommand(), RemoveTimeout, ct)
            .ConfigureAwait(false);

        return ok
            ? new DefenderResult(
                DefenderState.Clean, "Defender removed the threats it had active.", output)
            : new DefenderResult(
                DefenderState.CouldNotRun,
                "Removal did not complete - read Defender's output below. Anything it named " +
                "is still there.",
                output);
    }

    private static (string Output, int ExitCode) Run(
        string executable, string arguments, CancellationToken ct)
    {
        using var process = Process.Start(new ProcessStartInfo(executable, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        if (process is null) return (string.Empty, -1);

        using var registration = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        });

        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();

        if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { }

            return (output + "\nThe scan did not finish in time.", -1);
        }

        return (output, process.ExitCode);
    }
}
