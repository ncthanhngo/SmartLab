namespace UsbDoctor.Uninstall;

/// <summary>One entry from a Windows uninstall registry key.</summary>
public sealed record InstalledProgram(string DisplayName, string RegistryKeyPath)
{
    public string? Version { get; init; }
    public string? Publisher { get; init; }
    public string? InstallLocation { get; init; }

    /// <summary>Command the vendor registered for interactive removal.</summary>
    public string? UninstallString { get; init; }

    /// <summary>Command that removes without prompting, when the vendor supplied one.</summary>
    public string? QuietUninstallString { get; init; }

    /// <summary>Vendor-reported size in bytes, converted from the registry's KB.</summary>
    public long EstimatedSizeBytes { get; init; }

    public bool Is64Bit { get; init; }
    public bool IsPerUser { get; init; }

    public bool HasUninstaller =>
        !string.IsNullOrWhiteSpace(UninstallString) ||
        !string.IsNullOrWhiteSpace(QuietUninstallString);

    public string SizeText => EstimatedSizeBytes switch
    {
        <= 0 => string.Empty,
        < 1024L * 1024 * 1024 => $"{EstimatedSizeBytes / 1024.0 / 1024:F0} MB",
        _ => $"{EstimatedSizeBytes / 1024.0 / 1024 / 1024:F2} GB",
    };
}

/// <summary>
/// Turns the raw values of an uninstall key into a program, or rejects it.
/// </summary>
/// <remarks>
/// Kept as a pure function over a value bag so the filtering rules can be tested
/// without a registry. The rules are where the risk lives: listing a Windows update
/// or a component as if it were an application invites the user to uninstall
/// something that will take the operating system with it.
/// </remarks>
public static class InstalledProgramParser
{
    public static bool TryParse(
        IReadOnlyDictionary<string, object?> values,
        string keyPath,
        bool is64Bit,
        bool isPerUser,
        out InstalledProgram? program)
    {
        program = null;

        var name = AsString(values, "DisplayName");
        if (string.IsNullOrWhiteSpace(name)) return false;

        // SystemComponent marks things Windows itself installed and Add/Remove
        // Programs hides. They are not applications and removing them breaks the OS.
        if (AsInt(values, "SystemComponent") == 1) return false;

        // An entry with a parent is a patch or add-on belonging to another product.
        // Uninstalling it independently generally does nothing useful and sometimes
        // corrupts the parent's install state.
        if (!string.IsNullOrWhiteSpace(AsString(values, "ParentKeyName")) ||
            !string.IsNullOrWhiteSpace(AsString(values, "ParentDisplayName")))
        {
            return false;
        }

        // Updates and hotfixes: same reasoning.
        var releaseType = AsString(values, "ReleaseType");
        if (!string.IsNullOrWhiteSpace(releaseType) &&
            !releaseType.Equals("Full", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Windows update entries are conventionally named "Update for ..." or
        // "Security Update for ..." and carry a KB reference.
        if (name!.StartsWith("Update for ", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Security Update for ", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Hotfix for ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // EstimatedSize is in kilobytes, and vendors sometimes write nonsense into
        // it, so an implausible value is dropped rather than displayed.
        var sizeKb = AsInt(values, "EstimatedSize");
        var sizeBytes = sizeKb is > 0 and < 1024L * 1024 * 512 ? sizeKb * 1024L : 0;

        program = new InstalledProgram(name.Trim(), keyPath)
        {
            Version = AsString(values, "DisplayVersion"),
            Publisher = AsString(values, "Publisher"),
            InstallLocation = AsString(values, "InstallLocation")?.Trim().Trim('"'),
            UninstallString = AsString(values, "UninstallString"),
            QuietUninstallString = AsString(values, "QuietUninstallString"),
            EstimatedSizeBytes = sizeBytes,
            Is64Bit = is64Bit,
            IsPerUser = isPerUser,
        };

        return true;
    }

    private static string? AsString(IReadOnlyDictionary<string, object?> values, string name) =>
        values.TryGetValue(name, out var v) ? v as string : null;

    private static long AsInt(IReadOnlyDictionary<string, object?> values, string name)
    {
        if (!values.TryGetValue(name, out var v) || v is null) return 0;

        return v switch
        {
            int i => i,
            long l => l,
            string s when long.TryParse(s, out var parsed) => parsed,
            _ => 0,
        };
    }
}
