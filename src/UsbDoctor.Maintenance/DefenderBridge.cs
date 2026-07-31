using System.Diagnostics;
using Microsoft.Win32;

namespace UsbDoctor.Maintenance;

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
    /// <summary>A full custom scan of a stick can take minutes.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(20);

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

    public static string BuildRemoveArguments() => "-Remove -All";

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
    /// <remarks>
    /// Exit code alone is not enough: MpCmdRun returns 2 both for "threats found" and
    /// for several failures, so the text has to be read as well. Anything that is not
    /// recognisably a completed scan becomes <see cref="DefenderState.CouldNotRun"/> -
    /// never Clean by default.
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

        if (threats.Length > 0)
        {
            return new DefenderResult(
                DefenderState.ThreatsFound,
                $"Defender identified {threats.Length} threat(s).",
                text)
            { Threats = threats };
        }

        var finished = text.Contains("Scan finished", StringComparison.OrdinalIgnoreCase) ||
                       text.Contains("found no threats", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Scans one path. Read-only: identification, never removal.</summary>
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

    /// <summary>Asks Defender to remove what it found. A separate, ticked action.</summary>
    public static DefenderResult Remove(CancellationToken ct = default)
    {
        if (FindExecutable() is not { } mpCmdRun)
            return new DefenderResult(DefenderState.NotAvailable, "MpCmdRun.exe was not found.", string.Empty);

        try
        {
            var (output, exitCode) = Run(mpCmdRun, BuildRemoveArguments(), ct);

            return exitCode == 0
                ? new DefenderResult(DefenderState.Clean, "Defender removed what it had quarantined.", output)
                : new DefenderResult(DefenderState.CouldNotRun,
                    $"Removal returned exit code {exitCode}.", output);
        }
        catch (Exception ex)
        {
            return new DefenderResult(DefenderState.CouldNotRun, ex.Message, string.Empty);
        }
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
