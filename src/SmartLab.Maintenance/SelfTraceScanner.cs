using System.IO;

namespace SmartLab.Maintenance;

/// <summary>
/// Lists everything Smart Lab has put on the machine.
/// </summary>
/// <remarks>
/// The list is written out explicitly rather than discovered by searching for the
/// app's name. A search would be both incomplete - it cannot know the Run value is
/// ours - and dangerous, since anything else on the machine with "SmartLab" in its
/// path would be swept up with it. Every location here is one the app itself
/// writes, so the list stays honest as long as it is updated alongside the code
/// that creates them.
/// </remarks>
public sealed class SelfTraceScanner(ITraceProbe probe, UninstallPaths paths)
{
    /// <summary>
    /// What the app was called before, and still writes nothing to.
    /// </summary>
    /// <remarks>
    /// The rename moved every path this app writes. Anything already sitting under the
    /// old name would otherwise become invisible here - including rescued files, which
    /// may be the only copy left of a drive that has since been formatted. Listing the
    /// legacy folders is the whole reason this constant exists; nothing writes to them.
    /// </remarks>
    private const string LegacyName = "UsbDoctor";

    public IReadOnlyList<AppTrace> Scan()
    {
        var traces = new List<AppTrace>
        {
            RegistryValue(
                UninstallPaths.RunKeyPath, UninstallPaths.RunValueName,
                "Start with Windows entry"),

            Directory(
                Path.Combine(paths.LocalAppData, "SmartLab"),
                "Journals and crash log", isUserData: false),

            Directory(
                Path.Combine(paths.UserProfile, "SmartLab", "quarantine"),
                "Quarantined malware samples", isUserData: true),

            Directory(
                Path.Combine(paths.UserProfile, "SmartLab", "rescue"),
                "Data rescued off drives", isUserData: true),

            Directory(
                Path.Combine(paths.UserProfile, "SmartLab", "recovered"),
                "Files carved from deleted entries", isUserData: true),

            Directory(
                paths.InstallDirectory,
                "Application folder", isUserData: false),
        };

        // The parent only goes if it is the app's own and now empty; it is listed
        // last so it is removed after its children.
        var parent = Path.Combine(paths.UserProfile, "SmartLab");
        traces.Add(Directory(parent, "SmartLab folder, if empty", isUserData: false));

        traces.AddRange(LegacyTraces());

        return [.. traces.Where(t => t.Exists)];
    }

    /// <summary>
    /// The same locations under the app's former name.
    /// </summary>
    /// <remarks>
    /// Listed, never written to, and every one of them keeps whichever user-data flag
    /// its current counterpart carries - so rescued and carved files still arrive
    /// unticked. Their descriptions say "old" because an operator seeing two rescue
    /// folders needs to know which one this app has been filling.
    /// </remarks>
    private IEnumerable<AppTrace> LegacyTraces()
    {
        yield return Directory(
            Path.Combine(paths.LocalAppData, LegacyName),
            "Journals and crash log, from the old name", isUserData: false);

        yield return Directory(
            Path.Combine(paths.UserProfile, LegacyName, "quarantine"),
            "Quarantined malware samples, from the old name", isUserData: true);

        yield return Directory(
            Path.Combine(paths.UserProfile, LegacyName, "rescue"),
            "Data rescued off drives, from the old name", isUserData: true);

        yield return Directory(
            Path.Combine(paths.UserProfile, LegacyName, "recovered"),
            "Files carved from deleted entries, under the old name", isUserData: true);

        yield return Directory(
            Path.Combine(paths.UserProfile, LegacyName),
            "Old app folder, if empty", isUserData: false);
    }

    private AppTrace RegistryValue(string keyPath, string valueName, string description) =>
        new(TraceKind.RegistryValue, keyPath, description)
        {
            ValueName = valueName,
            Exists = probe.RegistryValueExists(keyPath, valueName),
        };

    private AppTrace Directory(string path, string description, bool isUserData)
    {
        var exists = probe.DirectoryExists(path);

        return new AppTrace(TraceKind.Directory, path, description)
        {
            Exists = exists,
            SizeBytes = exists ? probe.DirectorySize(path) : 0,
            IsUserData = isUserData,
        };
    }
}
