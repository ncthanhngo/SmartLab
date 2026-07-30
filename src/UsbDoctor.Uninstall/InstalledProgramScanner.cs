using Microsoft.Win32;

namespace UsbDoctor.Uninstall;

/// <summary>
/// Reads the installed-program list from the Windows uninstall keys.
/// </summary>
/// <remarks>
/// All three locations are read: the 64-bit and 32-bit machine views, and the
/// per-user hive. Reading only the default view is the classic mistake - a 64-bit
/// process silently misses every 32-bit application, which on a typical machine is
/// most of them.
/// </remarks>
public sealed class InstalledProgramScanner
{
    private const string UninstallSubKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public IReadOnlyList<InstalledProgram> Scan()
    {
        var found = new List<InstalledProgram>();

        Read(RegistryHive.LocalMachine, RegistryView.Registry64, is64Bit: true, isPerUser: false, found);
        Read(RegistryHive.LocalMachine, RegistryView.Registry32, is64Bit: false, isPerUser: false, found);
        Read(RegistryHive.CurrentUser, RegistryView.Default, is64Bit: true, isPerUser: true, found);

        // The same product can appear in more than one view. Keyed on name and
        // version so two genuinely different versions both survive.
        return [.. found
            .GroupBy(p => (p.DisplayName, p.Version), StringTupleComparer.Instance)
            .Select(g => g.First())
            .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static void Read(
        RegistryHive hive, RegistryView view, bool is64Bit, bool isPerUser,
        List<InstalledProgram> into)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(UninstallSubKey);
            if (uninstall is null) return;

            var hiveName = hive == RegistryHive.CurrentUser ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE";

            foreach (var name in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var entry = uninstall.OpenSubKey(name);
                    if (entry is null) continue;

                    var values = entry.GetValueNames()
                        .ToDictionary(v => v, entry.GetValue, StringComparer.OrdinalIgnoreCase);

                    var keyPath = $@"{hiveName}\{UninstallSubKey}\{name}";

                    if (InstalledProgramParser.TryParse(values, keyPath, is64Bit, isPerUser, out var program))
                        into.Add(program!);
                }
                catch
                {
                    // One unreadable entry must not cost the whole list.
                }
            }
        }
        catch
        {
            // A view that does not exist on this machine is not an error.
        }
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string Name, string? Version)>
    {
        public static readonly StringTupleComparer Instance = new();

        public bool Equals((string Name, string? Version) a, (string Name, string? Version) b) =>
            string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Version, b.Version, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Name, string? Version) v) =>
            HashCode.Combine(
                v.Name.ToUpperInvariant(),
                v.Version?.ToUpperInvariant());
    }
}
