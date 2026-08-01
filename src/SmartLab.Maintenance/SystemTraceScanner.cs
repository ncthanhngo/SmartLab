using System.IO;
using Microsoft.Win32;

namespace SmartLab.Maintenance;

/// <summary>
/// The three things a program leaves that put it back on its feet.
/// </summary>
/// <remarks>
/// <para>
/// A folder left behind wastes space. A scheduled task, a service, or a firewall rule
/// left behind is a program that is still running, still allowed through, or still
/// reinstalling itself - which is exactly what somebody uninstalling it was trying to
/// stop.
/// </para>
/// <para>
/// Every one of these is matched by where it points, never by its name. A task whose
/// command runs from the program's own folder is that program's task; a task merely
/// called after it might be somebody's backup job. Reading is done through the
/// registry and the filesystem rather than by shelling out: <c>schtasks</c> and
/// <c>netsh</c> would each cost a process launch and return text to be parsed, and
/// the data is in a hive either way.
/// </para>
/// </remarks>
public sealed class SystemTraceScanner
{
    /// <summary>Where Windows keeps the definition of every scheduled task.</summary>
    private static string TaskFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Tasks");

    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    private const string FirewallKey =
        @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";

    /// <summary>
    /// Everything of these three kinds that runs from one of the given folders.
    /// </summary>
    /// <param name="appFolders">
    /// The program's own folders. Nothing outside them is ever matched, which is what
    /// keeps this from proposing to remove a service belonging to something else.
    /// </param>
    public IReadOnlyList<AppTrace> Scan(
        IReadOnlyList<string> appFolders, IProgress<UninstallStep>? progress = null)
    {
        var found = new List<AppTrace>();

        if (appFolders.Count == 0) return found;

        found.AddRange(ScheduledTasks(appFolders, progress));
        found.AddRange(Services(appFolders, progress));
        found.AddRange(FirewallRules(appFolders, progress));

        return found;
    }

    /// <remarks>
    /// The task files are XML and the command is in an <c>&lt;Command&gt;</c> element,
    /// but this looks for the folder anywhere in the file rather than parsing it: a
    /// task that names the folder in an argument or a working directory is still that
    /// program's task, and a scan that missed it for being in the wrong element would
    /// leave the thing that reinstalls the program behind.
    /// </remarks>
    private static IEnumerable<AppTrace> ScheduledTasks(
        IReadOnlyList<string> appFolders, IProgress<UninstallStep>? progress)
    {
        Say(progress, UninstallStepKind.Info, $"Checking scheduled tasks under: {TaskFolder}");

        string[] files;

        try
        {
            files = Directory.Exists(TaskFolder)
                ? Directory.GetFiles(TaskFolder, "*", SearchOption.AllDirectories)
                : [];
        }
        catch
        {
            // Reading every task needs Administrator on some machines. Saying nothing
            // is right; claiming there are none would not be.
            Say(progress, UninstallStepKind.Warning,
                "Scheduled tasks could not be read - that needs Administrator.");
            yield break;
        }

        foreach (var file in files)
        {
            string text;

            try { text = File.ReadAllText(file); }
            catch { continue; }

            var folder = appFolders.FirstOrDefault(f => text.Contains(f, StringComparison.OrdinalIgnoreCase));
            if (folder is null) continue;

            var name = Path.GetRelativePath(TaskFolder, file).Replace('\\', '/');

            yield return new AppTrace(TraceKind.File, file,
                $"Scheduled task '{name}' runs something from {folder}")
            {
                Exists = true,
                Evidence = TraceEvidence.PointsAtApp,
            };
        }
    }

    private static IEnumerable<AppTrace> Services(
        IReadOnlyList<string> appFolders, IProgress<UninstallStep>? progress)
    {
        Say(progress, UninstallStepKind.Info, @"Checking services under: HKLM\SYSTEM\CurrentControlSet\Services");

        string[] names;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServicesKey);
            names = key?.GetSubKeyNames() ?? [];
        }
        catch
        {
            yield break;
        }

        foreach (var name in names)
        {
            string? image;

            try
            {
                using var service = Registry.LocalMachine.OpenSubKey($@"{ServicesKey}\{name}");
                image = service?.GetValue("ImagePath") as string;
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(image)) continue;

            var folder = appFolders.FirstOrDefault(f => image.Contains(f, StringComparison.OrdinalIgnoreCase));
            if (folder is null) continue;

            yield return new AppTrace(TraceKind.RegistryKey,
                $@"HKEY_LOCAL_MACHINE\{ServicesKey}\{name}",
                $"Service '{name}' runs from {folder}")
            {
                Exists = true,
                Evidence = TraceEvidence.PointsAtApp,
            };
        }
    }

    /// <remarks>
    /// A rule is one registry value whose data is a string of <c>Key=Value</c> pairs,
    /// one of which is <c>App=</c> and holds the path being allowed through. Removing
    /// the value removes the rule.
    /// </remarks>
    private static IEnumerable<AppTrace> FirewallRules(
        IReadOnlyList<string> appFolders, IProgress<UninstallStep>? progress)
    {
        Say(progress, UninstallStepKind.Info, "Checking firewall rules");

        string[] values;
        RegistryKey? key = null;

        try
        {
            key = Registry.LocalMachine.OpenSubKey(FirewallKey);
            values = key?.GetValueNames() ?? [];
        }
        catch
        {
            key?.Dispose();
            yield break;
        }

        using (key)
        {
            foreach (var name in values)
            {
                var rule = key?.GetValue(name) as string;
                if (string.IsNullOrWhiteSpace(rule)) continue;

                var folder = appFolders.FirstOrDefault(f => rule.Contains(f, StringComparison.OrdinalIgnoreCase));
                if (folder is null) continue;

                yield return new AppTrace(TraceKind.RegistryValue,
                    $@"HKEY_LOCAL_MACHINE\{FirewallKey}",
                    $"Firewall rule allowing something in {folder}")
                {
                    ValueName = name,
                    Exists = true,
                    Evidence = TraceEvidence.PointsAtApp,
                };
            }
        }
    }

    private static void Say(IProgress<UninstallStep>? progress, UninstallStepKind kind, string text) =>
        progress?.Report(new UninstallStep(kind, text));
}
