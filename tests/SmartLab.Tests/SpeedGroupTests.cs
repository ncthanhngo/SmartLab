using SmartLab.App;
using SmartLab.Maintenance;
using Xunit;

namespace SmartLab.Tests;

/// <summary>
/// Startup entries, where a wrong decision breaks someone's login.
/// </summary>
public sealed class StartupItemTests
{
    private static StartupItem Item(
        string name = "Thing", string command = @"C:\Program Files\Thing\thing.exe",
        StartupOrigin origin = StartupOrigin.RunKey, bool perUser = true) =>
        new(name, command, origin, perUser, "location");

    [Fact]
    public void NothingIsTickedWhenTheListIsBuilt()
    {
        // A startup list arriving pre-ticked is a cleaner daring the user to notice.
        Assert.False(new StartupItemViewModel(Item()).IsSelected);
    }

    [Fact]
    public void AMachineWideEntryCannotBeChangedFromHere()
    {
        // This app runs as the invoking user by design - the same distinction the
        // program list already draws between per-user and machine-wide.
        Assert.False(StartupItemToggle.CanChange(Item(perUser: false)));
    }

    [Fact]
    public void AWindowsOwnedEntryIsNeverProposed()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var item = Item(command: Path.Combine(windows, "System32", "something.exe"));

        Assert.True(item.IsWindowsOwned);
        Assert.False(StartupItemToggle.CanChange(item));
    }

    [Fact]
    public void AnOrdinaryPerUserRunEntryCanBeChanged()
    {
        Assert.True(StartupItemToggle.CanChange(Item()));
    }

    [Fact]
    public void AStartupFolderShortcutIsNotToggledThroughTheRegistry()
    {
        // It is listed, but disabling it means moving a file, which this does not do.
        // Claiming otherwise would silently no-op.
        Assert.False(StartupItemToggle.CanChange(Item(origin: StartupOrigin.StartupFolder)));
    }

    [Fact]
    public void EachOriginReportsWhereItCameFrom()
    {
        Assert.Contains("Run key", Item().OriginText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Startup folder",
            Item(origin: StartupOrigin.StartupFolder).OriginText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DisableAndRestoreReturnTheValueByteForByte()
    {
        // A Run value's quoting is load-bearing: a restore that loses a pair of quotes
        // leaves a program starting with the wrong arguments, or not at all.
        const string quoted = "\"C:\\Program Files\\Thing\\thing.exe\" --flag \"a b\"";
        var name = $"SmartLabTest_{Guid.NewGuid():N}";

        using (var run = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"))
        {
            run.SetValue(name, quoted, Microsoft.Win32.RegistryValueKind.String);
        }

        try
        {
            var item = new StartupItem(name, quoted, StartupOrigin.RunKey, PerUser: true, "hkcu");

            Assert.True(StartupItemToggle.Disable(item, out var disableError), disableError);
            Assert.DoesNotContain(StartupItemScanner.Scan(), i => i.Name == name);

            Assert.True(StartupItemToggle.Restore(name, out var restoreError), restoreError);

            var restored = StartupItemScanner.Scan().SingleOrDefault(i => i.Name == name);

            Assert.NotNull(restored);
            Assert.Equal(quoted, restored!.Command);
        }
        finally
        {
            using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
            run?.DeleteValue(name, throwOnMissingValue: false);

            using var backup = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                StartupItemScanner.BackupPath, writable: true);
            backup?.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    [Fact]
    public void TheEntriesYouCanChangeSortAboveTheRest()
    {
        // Groups appear in the order their first member does, so this decides whether
        // the list opens on what the operator can act on. Sorting by the group's name
        // put "You can turn these off" last, alphabetically and uselessly - the same
        // mistake ConfidenceRank exists to prevent in the deleted-file list.
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var changeable = new StartupItemViewModel(Item()).ScopeRank;
        var needsAdmin = new StartupItemViewModel(Item(perUser: false)).ScopeRank;
        var windowsOwn = new StartupItemViewModel(
            Item(command: Path.Combine(windows, "System32", "x.exe"))).ScopeRank;

        Assert.True(changeable < needsAdmin);
        Assert.True(needsAdmin < windowsOwn);
    }

    [Fact]
    public void EveryScopeRanksDistinctly()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var ranks = new[]
        {
            new StartupItemViewModel(Item()).ScopeRank,
            new StartupItemViewModel(Item(perUser: false)).ScopeRank,
            new StartupItemViewModel(Item(command: Path.Combine(windows, "x.exe"))).ScopeRank,
        };

        Assert.Equal(ranks.Length, ranks.Distinct().Count());
    }

    [Fact]
    public void TheHeadingSaysDisablingIsReversible()
    {
        var summary = OptimizationViewModel.Summarise(found: 20, ticked: 0, changeable: 8, disabled: 0);

        Assert.Contains("put back", summary.Detail, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The repair commands, which must stay exactly as documented.
/// </summary>
public sealed class RepairCommandTests
{
    [Fact]
    public void ChkdskIsOnlyEverComposedWithScan()
    {
        // /f takes the volume offline and can demand a reboot. That is not something
        // a button labelled "check" should decide for someone.
        var chkdsk = RepairCommand.All.Single(c => c.Id == "chkdsk");

        Assert.Equal("/scan", chkdsk.Arguments);
        Assert.DoesNotContain("/f", chkdsk.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/r", chkdsk.Arguments, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryCommandIsAMicrosoftToolRunAsItself()
    {
        // Nothing here reimplements a repair, in the same spirit as handing removal to
        // the vendor's own uninstaller.
        string[] expected = ["sfc.exe", "DISM.exe", "ipconfig.exe", "chkdsk.exe"];

        Assert.Equal(expected.Order(), RepairCommand.All.Select(c => c.Executable).Order());
    }

    [Fact]
    public void ElevationIsDeclaredCorrectlyForEach()
    {
        Assert.True(RepairCommand.All.Single(c => c.Id == "sfc").NeedsElevation);
        Assert.True(RepairCommand.All.Single(c => c.Id == "dism").NeedsElevation);
        Assert.True(RepairCommand.All.Single(c => c.Id == "chkdsk").NeedsElevation);
        Assert.False(RepairCommand.All.Single(c => c.Id == "dns").NeedsElevation);
    }

    [Fact]
    public void ArgumentsAreFixedAndTakeNoInput()
    {
        // Every argument string is a literal in the catalogue. Nothing composes one
        // from a path or a name, so there is nothing here to inject into.
        foreach (var command in RepairCommand.All)
        {
            Assert.DoesNotContain("{", command.Arguments);
            Assert.DoesNotContain("%", command.Arguments);
        }
    }

    [Fact]
    public void TheHeadingSaysTheAppNeverRunsElevated()
    {
        var summary = MaintenanceViewModel.Summarise(completed: 0, total: 4);

        Assert.Contains("never runs elevated", summary.Detail, StringComparison.OrdinalIgnoreCase);
    }
}
