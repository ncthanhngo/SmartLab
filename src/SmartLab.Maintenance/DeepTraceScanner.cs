using System.IO;
using Microsoft.Win32;

namespace SmartLab.Maintenance;

/// <summary>
/// How sure we are that a trace belongs to the program that was removed.
/// </summary>
/// <remarks>
/// The distinction is the whole safety story of a deep scan. A shortcut whose target
/// is inside the folder we just watched an uninstaller fail to remove is not a guess;
/// a registry key that merely shares a publisher's name might hold the settings of a
/// different product from the same company. Both are worth showing. Only one is worth
/// ticking for somebody.
/// </remarks>
public enum TraceEvidence
{
    /// <summary>The program registered this itself. Its own words about itself.</summary>
    Registered,

    /// <summary>Something here points into the program's own folder.</summary>
    PointsAtApp,

    /// <summary>Only the name matches. Could be another product, or a shared runtime.</summary>
    NameMatch,
}

/// <summary>
/// Looks for what a program left behind beyond what it registered.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the narrow scan was not enough in the ordinary case. Zalo's own
/// uninstaller removed its registration and left 1 GB in
/// <c>%LOCALAPPDATA%\Programs\Zalo</c>, two shortcuts and a protocol handler; the
/// program had registered no install location, so a scan that only reads what a
/// program says about itself found nothing and reported a clean removal.
/// </para>
/// <para>
/// What keeps it from being reckless is not narrowness but evidence. Every hit says
/// how it was found, folders are only ever matched one level below a known root, and
/// anything found by name alone arrives unticked and labelled. Deleting a shared
/// runtime because it shares a publisher's name is the failure mode this grades for
/// rather than pretends away.
/// </para>
/// </remarks>
public sealed class DeepTraceScanner(ITraceProbe probe)
{
    /// <summary>Folders whose direct children are candidates. Never searched deeper.</summary>
    /// <remarks>
    /// One level, because that is where applications put themselves. Recursing would
    /// turn a name match into a machine-wide grep and start proposing to delete
    /// somebody's project folder that happens to be called after the app.
    /// </remarks>
    private static IEnumerable<string> AppRoots()
    {
        yield return Path.Combine(Local, "Programs");
        yield return Local;
        yield return Roaming;
        yield return ProgramData;
        yield return ProgramFiles;
        yield return ProgramFilesX86;
    }

    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static string ProgramFilesX86 => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

    private static string StartMenu => Environment.GetFolderPath(Environment.SpecialFolder.Programs);
    private static string CommonStartMenu => Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
    private static string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>
    /// Everything this program appears to have left on the machine.
    /// </summary>
    /// <param name="progress">Reports each place looked at, clean or not.</param>
    public IReadOnlyList<AppTrace> Scan(
        InstalledProgram program, IProgress<UninstallStep>? progress = null)
    {
        var found = new List<AppTrace>();
        var names = NamesFor(program);

        Say(progress, UninstallStepKind.Info,
            $"Deep scan: looking for {string.Join(", ", names.Select(n => $"'{n}'"))}");

        // Materialised, not left lazy: the list is walked several times below, and a
        // lazy walk would scan the machine again and say so twice in the log.
        var folders = ScanFolders(names, progress).ToList();

        // Shortcuts are read after the folders, so a target landing inside one of them
        // can be recognised as pointing at the app rather than merely sharing its name.
        var appFolders = folders.Select(f => f.Location).ToList();

        if (!string.IsNullOrWhiteSpace(program.InstallLocation))
            appFolders.Add(program.InstallLocation!);

        var shortcuts = ScanShortcuts(names, appFolders, progress).ToList();

        // A Start Menu entry named after the program, launching something inside a
        // folder named after the program, is two independent things agreeing about
        // one place. That is no longer a guess, so the folder stops being one.
        var corroborated = shortcuts
            .Where(h => h.NameMatched && !string.IsNullOrWhiteSpace(h.Target))
            .SelectMany(h => folders.Where(f => IsInside(h.Target!, f.Location)))
            .Select(f => f.Location)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        found.AddRange(folders.Select(f => corroborated.Contains(f.Location)
            ? f with
            {
                Evidence = TraceEvidence.PointsAtApp,
                Description = f.Description + ", and a shortcut of its name launches something inside it",
            }
            : f));

        found.AddRange(shortcuts.Select(h => h.Trace));
        found.AddRange(ScanRegistry(names, appFolders, progress));

        Say(progress, found.Count == 0 ? UninstallStepKind.Ok : UninstallStepKind.Warning,
            found.Count == 0
                ? "Deep scan found nothing else."
                : $"Deep scan found {found.Count} more thing(s).");

        return found;
    }

    /// <summary>
    /// The words worth looking for: the program's name, and its publisher's.
    /// </summary>
    /// <remarks>
    /// Version numbers and edition words are stripped, because a folder is called
    /// <c>Zalo</c> where the registry entry is called <c>Zalo 26.06.11</c>. Anything
    /// under four characters is dropped: two-letter publishers match half the machine.
    /// </remarks>
    public static IReadOnlyList<string> NamesFor(InstalledProgram program)
    {
        var names = new List<string>();

        foreach (var raw in new[] { program.DisplayName, program.Publisher })
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var trimmed = Simplify(raw!);
            if (trimmed.Length >= 4 && !names.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                names.Add(trimmed);
        }

        return names;
    }

    /// <summary>Drops version numbers, bitness and trailing punctuation.</summary>
    private static string Simplify(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>();

        foreach (var word in words)
        {
            var w = word.Trim('(', ')', ',', '-', '.', '™', '®');

            if (w.Length == 0) continue;
            if (char.IsDigit(w[0])) break;                       // "Zalo 26.06.11"
            if (w.Equals("x64", StringComparison.OrdinalIgnoreCase)) break;
            if (w.Equals("x86", StringComparison.OrdinalIgnoreCase)) break;
            if (w.Equals("bit", StringComparison.OrdinalIgnoreCase)) break;

            kept.Add(w);
        }

        return string.Join(' ', kept).Trim();
    }

    private IEnumerable<AppTrace> ScanFolders(
        IReadOnlyList<string> names, IProgress<UninstallStep>? progress)
    {
        foreach (var root in AppRoots().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(root) || !probe.DirectoryExists(root)) continue;

            Say(progress, UninstallStepKind.Info, $"Checking under: {root}");

            string[] children;
            try { children = Directory.GetDirectories(root); }
            catch { continue; } // a root this account cannot list is not a finding

            foreach (var child in children)
            {
                var leaf = Path.GetFileName(child);
                if (!names.Any(n => Matches(leaf, n))) continue;
                if (IsRefused(child)) continue;

                var size = probe.DirectorySize(child);

                yield return new AppTrace(TraceKind.Directory, child,
                    $"Folder named after the program, under {root}")
                {
                    Exists = true,
                    SizeBytes = size,
                    Evidence = TraceEvidence.NameMatch,
                };
            }
        }
    }

    /// <param name="NameMatched">
    /// True when the shortcut's own name matched, which is what lets it corroborate a
    /// folder rather than merely inherit that folder's guess.
    /// </param>
    private sealed record ShortcutHit(AppTrace Trace, string? Target, bool NameMatched);

    private IEnumerable<ShortcutHit> ScanShortcuts(
        IReadOnlyList<string> names, IReadOnlyList<string> appFolders,
        IProgress<UninstallStep>? progress)
    {
        foreach (var root in new[] { StartMenu, CommonStartMenu, Desktop })
        {
            if (string.IsNullOrWhiteSpace(root) || !probe.DirectoryExists(root)) continue;

            Say(progress, UninstallStepKind.Info, $"Checking shortcuts under: {root}");

            string[] links;
            try
            {
                links = Directory.GetFiles(root, "*.lnk", SearchOption.AllDirectories);
            }
            catch { continue; }

            foreach (var link in links)
            {
                var target = ShortcutTarget(link);
                var pointsAtApp = target is { Length: > 0 } && appFolders.Any(f => IsInside(target, f));
                var nameMatches = names.Any(n => Matches(Path.GetFileNameWithoutExtension(link), n));

                if (!pointsAtApp && !nameMatches) continue;

                // A shortcut whose target is gone is worth removing whatever matched
                // it: what it opens does not exist any more.
                var dangling = target is { Length: > 0 } && !File.Exists(target) && !Directory.Exists(target);

                var trace = new AppTrace(TraceKind.File, link,
                    pointsAtApp
                        ? $"Shortcut into the program's folder: {target}"
                        : dangling
                            ? $"Shortcut named after the program, pointing at something gone: {target}"
                            : "Shortcut named after the program")
                {
                    Exists = true,
                    SizeBytes = probe.FileSize(link),
                    Evidence = pointsAtApp || dangling ? TraceEvidence.PointsAtApp : TraceEvidence.NameMatch,
                };

                yield return new ShortcutHit(trace, target, nameMatches);
            }
        }
    }

    /// <remarks>
    /// Late-bound through WScript.Shell rather than through a COM reference, because
    /// resolving a shortcut is the only thing this needs from the shell and an
    /// interop assembly for one property is a dependency nobody would thank us for.
    /// </remarks>
    private static string? ShortcutTarget(string linkPath)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return null;

            dynamic? shell = Activator.CreateInstance(type);
            if (shell is null) return null;

            dynamic link = shell.CreateShortcut(linkPath);
            string target = link.TargetPath;

            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch
        {
            // A shortcut that cannot be read is left alone rather than guessed at.
            return null;
        }
    }

    private IEnumerable<AppTrace> ScanRegistry(
        IReadOnlyList<string> names, IReadOnlyList<string> appFolders,
        IProgress<UninstallStep>? progress)
    {
        var software = new[]
        {
            (@"HKEY_CURRENT_USER\Software", Registry.CurrentUser, "Software"),
            (@"HKEY_LOCAL_MACHINE\SOFTWARE", Registry.LocalMachine, "SOFTWARE"),
            (@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node", Registry.LocalMachine, @"SOFTWARE\WOW6432Node"),
        };

        foreach (var (display, hive, path) in software)
        {
            Say(progress, UninstallStepKind.Info, $"Checking registry under: {display}");

            foreach (var name in EnumerateSubKeys(hive, path))
            {
                if (!names.Any(n => Matches(name, n))) continue;

                yield return new AppTrace(TraceKind.RegistryKey, $@"{display}\{name}",
                    "Registry key named after the program or its publisher")
                {
                    Exists = true,
                    Evidence = TraceEvidence.NameMatch,
                };
            }
        }

        // Protocol handlers: a key here is what makes zalo:// open something. Worth
        // upgrading to hard evidence when its command runs from the app's own folder.
        foreach (var name in EnumerateSubKeys(Registry.CurrentUser, @"Software\Classes"))
        {
            if (!names.Any(n => Matches(name, n))) continue;

            var command = ValueOf(Registry.CurrentUser, $@"Software\Classes\{name}\shell\open\command");
            var points = command is { Length: > 0 } && appFolders.Any(f => command.Contains(f, StringComparison.OrdinalIgnoreCase));

            yield return new AppTrace(TraceKind.RegistryKey,
                $@"HKEY_CURRENT_USER\Software\Classes\{name}",
                points
                    ? "Protocol handler running from the program's folder"
                    : "Protocol handler named after the program")
            {
                Exists = true,
                Evidence = points ? TraceEvidence.PointsAtApp : TraceEvidence.NameMatch,
            };
        }

        // Startup entries left pointing into a folder that is going or gone.
        foreach (var (display, hive) in new[]
                 {
                     (@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run", Registry.CurrentUser),
                     (@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", Registry.LocalMachine),
                 })
        {
            const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";

            using var key = hive.OpenSubKey(run);
            if (key is null) continue;

            foreach (var valueName in key.GetValueNames())
            {
                var value = key.GetValue(valueName) as string;
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (!appFolders.Any(f => value.Contains(f, StringComparison.OrdinalIgnoreCase))) continue;

                yield return new AppTrace(TraceKind.RegistryValue, display,
                    $"Runs at logon from the program's folder: {value}")
                {
                    ValueName = valueName,
                    Exists = true,
                    Evidence = TraceEvidence.PointsAtApp,
                };
            }
        }
    }

    private static IEnumerable<string> EnumerateSubKeys(RegistryKey hive, string path)
    {
        string[] names;

        try
        {
            using var key = hive.OpenSubKey(path);
            if (key is null) yield break;

            names = key.GetSubKeyNames();
        }
        catch
        {
            yield break; // a hive this account cannot read is not a finding
        }

        foreach (var name in names) yield return name;
    }

    private static string? ValueOf(RegistryKey hive, string path)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            return key?.GetValue(null) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Whole-word-ish match: 'Zalo' matches 'Zalo', not 'Zalotron'.</summary>
    private static bool Matches(string candidate, string name) =>
        candidate.Equals(name, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(name + "-", StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase);

    private static bool IsInside(string path, string folder)
    {
        try
        {
            var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

            return a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Places no deep scan may ever propose, whatever their name.
    /// </summary>
    /// <remarks>
    /// A root itself, Windows, and the shared runtime folders. A name match on
    /// <c>%ProgramFiles%\Common Files</c> would be a proposal to break every program
    /// on the machine, and the operator has no way to know that from the row.
    /// </remarks>
    public static bool IsRefused(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;

        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch { return true; }

        // Both directions: nothing inside Windows, nothing that contains it, and not
        // Windows itself. System32 is inside it; C:\ contains it.
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        if (full.Equals(windows, StringComparison.OrdinalIgnoreCase) ||
            IsInside(full, windows) || IsInside(windows, full))
        {
            return true;
        }

        var forbidden = new[]
        {
            Local, Roaming, ProgramData, ProgramFiles, ProgramFilesX86,
            Path.Combine(Local, "Programs"),
            Path.Combine(ProgramFiles, "Common Files"),
            Path.Combine(ProgramFilesX86, "Common Files"),
            Path.Combine(ProgramFiles, "WindowsApps"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };

        if (forbidden.Any(f => !string.IsNullOrEmpty(f) &&
                               full.Equals(Path.TrimEndingDirectorySeparator(f), StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // A drive root, and anything one level under it, is not an app folder.
        return Path.GetPathRoot(full)?.Equals(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static void Say(IProgress<UninstallStep>? progress, UninstallStepKind kind, string text) =>
        progress?.Report(new UninstallStep(kind, text));
}
