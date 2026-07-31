using System.Diagnostics;

namespace UsbDoctor.Maintenance;

/// <param name="Available">Current version, or "Unknown" when winget cannot tell.</param>
public sealed record UpgradablePackage(string Id, string Name, string Installed, string Available, string Source)
{
    /// <summary>
    /// True when winget knows this package but did not install it.
    /// </summary>
    /// <remarks>
    /// Worth surfacing: upgrading one of these replaces a hand-placed build with the
    /// store's, which is occasionally exactly what someone did not want.
    /// </remarks>
    public bool NotFromWinget => Source.Length == 0;
}

/// <summary>
/// Runs winget and reads what it says.
/// </summary>
/// <remarks>
/// <para>
/// Wraps the tool rather than inventing a package database, for the same reason the
/// uninstaller runs the vendor's own uninstaller first: the thing that installed a
/// program is the thing that knows how to replace it.
/// </para>
/// <para>
/// Parsing is separated from running so it can be tested without winget present, and
/// so a machine where winget is missing produces a stated reason rather than an empty
/// list that reads as "everything is up to date".
/// </para>
/// </remarks>
public static class WingetBridge
{
    /// <summary>Long enough for a source refresh over a slow link.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    public static bool IsInstalled => FindExecutable() is not null;

    private static string? FindExecutable()
    {
        try
        {
            // Resolved through PATH rather than a hardcoded WindowsApps path, which
            // carries an ACL that blocks a plain directory listing.
            var probe = Process.Start(new ProcessStartInfo("where", "winget.exe")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (probe is null) return null;

            var output = probe.StandardOutput.ReadToEnd();
            probe.WaitForExit(10_000);

            var first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();

            return string.IsNullOrEmpty(first) ? null : first;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses <c>winget upgrade</c> output into packages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reads the table by locating the column offsets from the header rule rather than
    /// splitting on whitespace: package names contain spaces, and a naive split turns
    /// "Visual Studio Code" into three fields.
    /// </para>
    /// <para>
    /// Anything it cannot make sense of yields nothing rather than throwing. A winget
    /// update that changes the layout must degrade to "no upgrades found", never to a
    /// crash in a section the operator opened to look at a list.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<UpgradablePackage> ParseUpgrades(string output)
    {
        var packages = new List<UpgradablePackage>();
        if (string.IsNullOrWhiteSpace(output)) return packages;

        var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        // The rule of dashes under the header is the only reliable landmark: the
        // header words themselves are localised.
        var ruleIndex = Array.FindIndex(lines, l => l.Length > 10 && l.All(c => c is '-' or ' '));
        if (ruleIndex <= 0) return packages;

        var header = lines[ruleIndex - 1];
        var starts = ColumnStarts(header);

        if (starts.Count < 4) return packages;

        for (var i = ruleIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0) break;

            // The trailing summary line ("N upgrades available.") has no columns.
            if (starts[1] >= line.Length) continue;

            var name = Slice(line, starts[0], starts[1]);
            var id = Slice(line, starts[1], starts[2]);
            var installed = Slice(line, starts[2], starts[3]);
            var available = starts.Count > 4
                ? Slice(line, starts[3], starts[4])
                : Slice(line, starts[3], line.Length);
            var source = starts.Count > 4 ? Slice(line, starts[4], line.Length) : string.Empty;

            if (id.Length == 0 || name.Length == 0) continue;

            // "Unknown" is winget saying it cannot compare versions. Offering that as
            // an upgrade invites a reinstall dressed up as an update.
            if (available.Length == 0 ||
                available.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) continue;

            packages.Add(new UpgradablePackage(id, name, installed, available, source));
        }

        return packages;
    }

    /// <summary>Where each column begins, taken from the header row.</summary>
    private static List<int> ColumnStarts(string header)
    {
        var starts = new List<int>();
        var inGap = true;

        for (var i = 0; i < header.Length; i++)
        {
            if (header[i] == ' ')
            {
                inGap = true;
                continue;
            }

            if (inGap) starts.Add(i);
            inGap = false;
        }

        return starts;
    }

    private static string Slice(string line, int start, int end)
    {
        if (start >= line.Length) return string.Empty;

        var stop = Math.Min(end, line.Length);

        return stop <= start ? string.Empty : line[start..stop].Trim();
    }

    /// <summary>Lists what winget would upgrade. Never writes.</summary>
    public static (IReadOnlyList<UpgradablePackage> Packages, string? Error) ListUpgrades()
    {
        if (FindExecutable() is not { } winget)
            return ([], "winget is not installed. It ships with App Installer from the Microsoft Store.");

        try
        {
            // --include-unknown so packages winget cannot version are still listed
            // rather than silently dropped before this code ever sees them.
            var result = Run(winget, "upgrade --include-unknown --disable-interactivity");

            return (ParseUpgrades(result.Output), result.Error);
        }
        catch (Exception ex)
        {
            return ([], ex.Message);
        }
    }

    /// <summary>Upgrades one package. Returns what winget printed.</summary>
    /// <remarks>
    /// One at a time on purpose. A batch that fails halfway reports as one failure,
    /// leaving the operator unable to tell which packages actually changed.
    /// </remarks>
    public static (bool Succeeded, string Detail) Upgrade(string packageId)
    {
        if (FindExecutable() is not { } winget) return (false, "winget is not installed.");

        try
        {
            var result = Run(winget,
                $"upgrade --id {packageId} --exact --silent --disable-interactivity " +
                "--accept-package-agreements --accept-source-agreements");

            return (result.ExitCode == 0, result.Error ?? LastMeaningfulLine(result.Output));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static (string Output, string? Error, int ExitCode) Run(string executable, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(executable, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        });

        if (process is null) return (string.Empty, "winget would not start.", -1);

        var output = process.StandardOutput.ReadToEnd();

        if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { }

            return (output, "winget did not finish in time.", -1);
        }

        return (output, null, process.ExitCode);
    }

    private static string LastMeaningfulLine(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .LastOrDefault(l => l.Length > 0) ?? "No output.";
}
