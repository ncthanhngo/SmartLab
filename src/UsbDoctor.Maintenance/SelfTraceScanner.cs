using System.IO;

namespace UsbDoctor.Maintenance;

/// <summary>
/// Lists everything USB Doctor has put on the machine.
/// </summary>
/// <remarks>
/// The list is written out explicitly rather than discovered by searching for the
/// app's name. A search would be both incomplete - it cannot know the Run value is
/// ours - and dangerous, since anything else on the machine with "UsbDoctor" in its
/// path would be swept up with it. Every location here is one the app itself
/// writes, so the list stays honest as long as it is updated alongside the code
/// that creates them.
/// </remarks>
public sealed class SelfTraceScanner(ITraceProbe probe, UninstallPaths paths)
{
    public IReadOnlyList<AppTrace> Scan()
    {
        var traces = new List<AppTrace>
        {
            RegistryValue(
                UninstallPaths.RunKeyPath, UninstallPaths.RunValueName,
                "Start with Windows entry"),

            Directory(
                Path.Combine(paths.LocalAppData, "UsbDoctor"),
                "Journals and crash log", isUserData: false),

            Directory(
                Path.Combine(paths.UserProfile, "UsbDoctor", "quarantine"),
                "Quarantined malware samples", isUserData: true),

            Directory(
                Path.Combine(paths.UserProfile, "UsbDoctor", "rescue"),
                "Data rescued off drives", isUserData: true),

            Directory(
                Path.Combine(paths.UserProfile, "UsbDoctor", "recovered"),
                "Files carved from deleted entries", isUserData: true),

            Directory(
                paths.InstallDirectory,
                "Application folder", isUserData: false),
        };

        // The parent only goes if it is the app's own and now empty; it is listed
        // last so it is removed after its children.
        var parent = Path.Combine(paths.UserProfile, "UsbDoctor");
        traces.Add(Directory(parent, "UsbDoctor folder, if empty", isUserData: false));

        return [.. traces.Where(t => t.Exists)];
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
