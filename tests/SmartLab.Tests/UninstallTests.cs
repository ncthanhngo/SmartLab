using SmartLab.Maintenance;
using Xunit;

namespace SmartLab.Tests;

/// <summary>Probe backed by explicit sets, so a trace list can be asserted exactly.</summary>
public sealed class FakeTraceProbe : ITraceProbe
{
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> RegistryKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> RegistryValues { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, long> Sizes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> FileCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public long RecycleBinBytes { get; set; }

    public bool FileExists(string path) => Files.Contains(path);
    public bool DirectoryExists(string path) => Directories.Contains(path);
    public long DirectorySize(string path) => Sizes.TryGetValue(path, out var s) ? s : 0;
    public long FileSize(string path) => Sizes.TryGetValue(path, out var s) ? s : 0;
    public bool RegistryKeyExists(string keyPath) => RegistryKeys.Contains(keyPath);
    public long RecycleBinSize() => RecycleBinBytes;

    public (long Bytes, int Files) DirectoryStats(string path) =>
        (DirectorySize(path), FileCounts.TryGetValue(path, out var c) ? c : 0);

    public bool RegistryValueExists(string keyPath, string valueName) =>
        RegistryValues.Contains($"{keyPath}::{valueName}");
}

public class SelfTraceScannerTests
{
    private static readonly UninstallPaths Paths =
        new(@"C:\Users\tester\AppData\Local", @"C:\Users\tester", @"C:\Apps\Smart Lab");

    [Fact]
    public void Only_traces_that_exist_are_listed()
    {
        var probe = new FakeTraceProbe();
        probe.Directories.Add(@"C:\Users\tester\AppData\Local\SmartLab");

        var traces = new SelfTraceScanner(probe, Paths).Scan();

        // Nothing else was placed on the fake machine, so nothing else may appear.
        var trace = Assert.Single(traces);
        Assert.Equal(TraceKind.Directory, trace.Kind);
        Assert.Contains("SmartLab", trace.Location, StringComparison.Ordinal);
    }

    [Fact]
    public void The_startup_registry_value_is_found()
    {
        var probe = new FakeTraceProbe();
        probe.RegistryValues.Add($"{UninstallPaths.RunKeyPath}::{UninstallPaths.RunValueName}");

        var trace = Assert.Single(new SelfTraceScanner(probe, Paths).Scan());

        Assert.Equal(TraceKind.RegistryValue, trace.Kind);
        Assert.Equal(UninstallPaths.RunValueName, trace.ValueName);
    }

    /// <summary>
    /// The single most important behaviour here. Rescued data may be the only copy
    /// left of a drive that has since been formatted, so it must be distinguishable
    /// from the app's own state.
    /// </summary>
    [Fact]
    public void Rescued_data_and_quarantine_are_marked_as_user_data()
    {
        var probe = new FakeTraceProbe();
        probe.Directories.Add(@"C:\Users\tester\SmartLab\rescue");
        probe.Directories.Add(@"C:\Users\tester\SmartLab\quarantine");
        probe.Directories.Add(@"C:\Users\tester\SmartLab\recovered");
        probe.Directories.Add(@"C:\Users\tester\AppData\Local\SmartLab");
        probe.Directories.Add(@"C:\Apps\Smart Lab");

        var traces = new SelfTraceScanner(probe, Paths).Scan();

        Assert.Equal(3, traces.Count(t => t.IsUserData));

        // The app's own state and its install folder are not the user's data.
        Assert.False(traces.Single(t => t.Location.EndsWith(@"Local\SmartLab", StringComparison.Ordinal)).IsUserData);
        Assert.False(traces.Single(t => t.Location == @"C:\Apps\Smart Lab").IsUserData);
    }

    [Fact]
    public void Directory_sizes_are_reported()
    {
        var probe = new FakeTraceProbe();
        probe.Directories.Add(@"C:\Users\tester\SmartLab\rescue");
        probe.Sizes[@"C:\Users\tester\SmartLab\rescue"] = 14_862_032_942;

        var trace = Assert.Single(new SelfTraceScanner(probe, Paths).Scan());

        Assert.Equal(14_862_032_942, trace.SizeBytes);
        Assert.Equal("13.84 GB", trace.SizeText);
    }
}

public class UninstallCommandParserTests
{
    [Fact]
    public void A_quoted_path_with_spaces_survives()
    {
        var command = UninstallCommandParser.Parse("\"C:\\Program Files\\Thing\\unins000.exe\" /SILENT");

        Assert.Equal(@"C:\Program Files\Thing\unins000.exe", command.FileName);
        Assert.Equal("/SILENT", command.Arguments);
    }

    [Fact]
    public void An_unquoted_path_with_spaces_splits_after_the_executable()
    {
        // Vendors really do write these unquoted, and splitting on the first space
        // would try to launch "C:\Program".
        var command = UninstallCommandParser.Parse(@"C:\Program Files\Thing\uninst.exe /S /norestart");

        Assert.Equal(@"C:\Program Files\Thing\uninst.exe", command.FileName);
        Assert.Equal("/S /norestart", command.Arguments);
    }

    [Fact]
    public void An_msi_command_splits_at_the_first_space()
    {
        var command = UninstallCommandParser.Parse("MsiExec.exe /X{2A1B4C3D-0000-1111-2222-333344445555}");

        Assert.Equal("MsiExec.exe", command.FileName);
        Assert.Equal("/X{2A1B4C3D-0000-1111-2222-333344445555}", command.Arguments);
    }

    [Theory]
    [InlineData("MsiExec.exe /I{2A1B4C3D-0000-1111-2222-333344445555}")]
    [InlineData("MsiExec.exe /i{2A1B4C3D-0000-1111-2222-333344445555}")]
    [InlineData("msiexec -I{2A1B4C3D-0000-1111-2222-333344445555}")]
    public void An_msi_command_that_would_repair_is_turned_into_one_that_removes(string registered)
    {
        // The bug this exists for: Windows writes /I - install mode - into the
        // uninstall key for most MSI products. 99 of the 134 MSI entries on the
        // machine this was found on. Running it opens a repair dialog, or for a
        // component with no UI does nothing visible, which is exactly what "the
        // uninstall button does not work" looks like.
        var command = UninstallCommandParser.Parse(registered);

        Assert.DoesNotContain("/I{", command.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("X{2A1B4C3D-0000-1111-2222-333344445555}", command.Arguments, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MsiExec.exe /package {2A1B4C3D-0000-1111-2222-333344445555}", "/uninstall {2A1B4C3D-0000-1111-2222-333344445555}")]
    [InlineData("MsiExec.exe /X{2A1B4C3D-0000-1111-2222-333344445555}", "/X{2A1B4C3D-0000-1111-2222-333344445555}")]
    [InlineData("MsiExec.exe /x {2A1B4C3D-0000-1111-2222-333344445555}", "/x {2A1B4C3D-0000-1111-2222-333344445555}")]
    public void TheLongFormIsRewrittenAndAnAlreadyCorrectCommandIsLeftAlone(
        string registered, string expected)
    {
        Assert.Equal(expected, UninstallCommandParser.Parse(registered).Arguments);
    }

    [Fact]
    public void OnlyMsiExecIsRewritten()
    {
        // /I means something else entirely to somebody else's uninstaller, and this
        // has no business editing arguments it did not write.
        var command = UninstallCommandParser.Parse(@"""C:\Apps\thing\uninst.exe"" /I /quiet");

        Assert.Equal("/I /quiet", command.Arguments);
    }

    [Fact]
    public void NothingIsAddedToAnMsiCommand()
    {
        // No /qn, no /norestart. msiexec still asks before it removes anything, and
        // for an irreversible action that prompt is worth keeping.
        var command = UninstallCommandParser.Parse("MsiExec.exe /I{2A1B4C3D-0000-1111-2222-333344445555}");

        Assert.DoesNotContain("/q", command.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("norestart", command.Arguments, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_bare_executable_has_no_arguments()
    {
        var command = UninstallCommandParser.Parse(@"C:\Apps\thing\uninstall.exe");

        Assert.Equal(@"C:\Apps\thing\uninstall.exe", command.FileName);
        Assert.Equal(string.Empty, command.Arguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_parses_to_empty(string? value)
    {
        Assert.True(UninstallCommandParser.Parse(value).IsEmpty);
    }

    [Fact]
    public void An_unbalanced_quote_does_not_swallow_the_command()
    {
        var command = UninstallCommandParser.Parse("\"C:\\Apps\\thing.exe");

        Assert.False(command.IsEmpty);
        Assert.Contains("thing.exe", command.FileName, StringComparison.Ordinal);
    }
}

public class InstalledProgramParserTests
{
    private static Dictionary<string, object?> Values(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void A_normal_program_parses()
    {
        var values = Values(
            ("DisplayName", "Some Tool"),
            ("DisplayVersion", "3.1.4"),
            ("Publisher", "Some Vendor"),
            ("InstallLocation", @"""C:\Program Files\Some Tool"" "),
            ("UninstallString", @"C:\Program Files\Some Tool\uninst.exe"),
            ("EstimatedSize", 51_200));

        Assert.True(InstalledProgramParser.TryParse(values, "key", true, false, out var program));

        Assert.Equal("Some Tool", program!.DisplayName);
        Assert.Equal("3.1.4", program.Version);
        Assert.Equal(@"C:\Program Files\Some Tool", program.InstallLocation);
        Assert.Equal(51_200L * 1024, program.EstimatedSizeBytes);
        Assert.True(program.HasUninstaller);
    }

    [Fact]
    public void An_entry_without_a_display_name_is_rejected()
    {
        Assert.False(InstalledProgramParser.TryParse(
            Values(("UninstallString", "x.exe")), "key", true, false, out _));
    }

    /// <summary>
    /// These filters are where the risk lives. Offering a Windows component or an
    /// update as if it were an application invites the user to remove something that
    /// takes the operating system with it.
    /// </summary>
    [Fact]
    public void A_system_component_is_rejected()
    {
        Assert.False(InstalledProgramParser.TryParse(
            Values(("DisplayName", "Some Runtime"), ("SystemComponent", 1)),
            "key", true, false, out _));
    }

    [Theory]
    [InlineData("ParentKeyName", "OtherProduct")]
    [InlineData("ParentDisplayName", "Other Product")]
    public void An_add_on_belonging_to_another_product_is_rejected(string key, string value)
    {
        Assert.False(InstalledProgramParser.TryParse(
            Values(("DisplayName", "Some Patch"), (key, value)), "key", true, false, out _));
    }

    [Theory]
    [InlineData("Update for Microsoft Something (KB123456)")]
    [InlineData("Security Update for Something (KB999)")]
    [InlineData("Hotfix for Something")]
    public void Updates_are_rejected_by_name(string name)
    {
        Assert.False(InstalledProgramParser.TryParse(
            Values(("DisplayName", name)), "key", true, false, out _));
    }

    [Fact]
    public void A_non_full_release_type_is_rejected()
    {
        Assert.False(InstalledProgramParser.TryParse(
            Values(("DisplayName", "Thing"), ("ReleaseType", "Security Update")),
            "key", true, false, out _));

        Assert.True(InstalledProgramParser.TryParse(
            Values(("DisplayName", "Thing"), ("ReleaseType", "Full")),
            "key", true, false, out _));
    }

    [Fact]
    public void An_implausible_size_is_dropped_rather_than_shown()
    {
        // Vendors write nonsense here; a bogus terabyte figure is worse than none.
        var values = Values(("DisplayName", "Thing"), ("EstimatedSize", 999_999_999_999L));

        Assert.True(InstalledProgramParser.TryParse(values, "key", true, false, out var program));
        Assert.Equal(0, program!.EstimatedSizeBytes);
        Assert.Equal(string.Empty, program.SizeText);
    }

    [Fact]
    public void A_program_with_no_uninstaller_still_lists_but_is_marked()
    {
        Assert.True(InstalledProgramParser.TryParse(
            Values(("DisplayName", "Orphan")), "key", true, false, out var program));

        Assert.False(program!.HasUninstaller);
    }

    [Fact]
    public void A_quiet_uninstall_string_counts_as_an_uninstaller()
    {
        Assert.True(InstalledProgramParser.TryParse(
            Values(("DisplayName", "Thing"), ("QuietUninstallString", "x.exe /S")),
            "key", true, false, out var program));

        Assert.True(program!.HasUninstaller);
    }
}

public class ProgramUninstallerLeftoverTests
{
    private static readonly InstalledProgram Program =
        new("Some Tool", @"HKEY_LOCAL_MACHINE\SOFTWARE\...\Uninstall\SomeTool")
        {
            InstallLocation = @"C:\Program Files\Some Tool",
        };

    [Fact]
    public void A_surviving_install_folder_and_key_are_both_reported()
    {
        var probe = new FakeTraceProbe();
        probe.Directories.Add(Program.InstallLocation!);
        probe.Sizes[Program.InstallLocation!] = 5_000_000;
        probe.RegistryKeys.Add(Program.RegistryKeyPath);

        var leftovers = new ProgramUninstaller(probe).ScanLeftovers(Program);

        Assert.Equal(2, leftovers.Count);
        Assert.Contains(leftovers, l => l.Kind == TraceKind.Directory && l.SizeBytes == 5_000_000);
        Assert.Contains(leftovers, l => l.Kind == TraceKind.RegistryKey);
    }

    [Fact]
    public void A_clean_uninstall_reports_nothing()
    {
        Assert.Empty(new ProgramUninstaller(new FakeTraceProbe()).ScanLeftovers(Program));
    }

    [Fact]
    public void A_program_with_no_install_location_reports_only_the_key()
    {
        var program = Program with { InstallLocation = null };
        var probe = new FakeTraceProbe();
        probe.RegistryKeys.Add(program.RegistryKeyPath);

        var leftover = Assert.Single(new ProgramUninstaller(probe).ScanLeftovers(program));

        Assert.Equal(TraceKind.RegistryKey, leftover.Kind);
    }
}
