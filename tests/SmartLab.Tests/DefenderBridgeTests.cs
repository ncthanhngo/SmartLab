using SmartLab.App;
using SmartLab.Maintenance;
using Xunit;

namespace SmartLab.Tests;

/// <summary>
/// The Defender delegation, and the one mistake it exists to avoid.
/// </summary>
/// <remarks>
/// A security section that reports "clean" because it could not run is worse than one
/// that reports nothing. Every test here is ultimately about keeping
/// <see cref="DefenderState.CouldNotRun"/> distinct from
/// <see cref="DefenderState.Clean"/>.
/// </remarks>
public sealed class DefenderBridgeTests
{
    [Fact]
    public void ACustomScanNamesExactlyOnePath()
    {
        var arguments = DefenderBridge.BuildScanArguments(@"E:\");

        Assert.Contains("-ScanType 3", arguments, StringComparison.Ordinal);
        Assert.Contains("-File", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void APathWithSpacesSurvivesQuoting()
    {
        // Unquoted, a mount point with a space becomes two arguments and Defender
        // scans something else, or nothing, and still reports success.
        var arguments = DefenderBridge.BuildScanArguments(@"C:\Program Files\Thing");

        Assert.Contains("\"C:\\Program Files\\Thing\"", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void ADriveRootSurvivesQuotingToo()
    {
        // Windows argument parsing lets a backslash immediately before the closing
        // quote escape it, so "E:\" arrives as E:" - a path that cannot exist. Measured
        // against the real MpCmdRun: it fails with hr = 0x80508023 in about a second,
        // having scanned nothing. Every drive root ends in that backslash.
        Assert.Equal(
            "-Scan -ScanType 3 -File \"E:\\\\\"",
            DefenderBridge.BuildScanArguments(@"E:\"));
    }

    [Fact]
    public void EveryScannableDriveProducesAnArgumentThatCanBeParsed()
    {
        // The sweep hands these straight to MpCmdRun, so the escaping has to hold for
        // whatever this machine actually has mounted.
        foreach (var root in DefenderBridge.ScannableDrives())
        {
            var arguments = DefenderBridge.BuildScanArguments(root);

            Assert.EndsWith("\\\\\"", arguments, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AScanThatFinishedCleanIsClean()
    {
        var result = DefenderBridge.Interpret(0, "Scan starting...\nScan finished.\nfound no threats.");

        Assert.Equal(DefenderState.Clean, result.State);
    }

    [Fact]
    public void AScanThatNamedThreatsReportsThem()
    {
        var output = """
            Scan starting...
            Threat information: Trojan:Win32/Wacatac.B!ml
            Threat information: Worm:Win32/Autorun
            Scan finished.
            """;

        var result = DefenderBridge.Interpret(2, output);

        Assert.Equal(DefenderState.ThreatsFound, result.State);
        Assert.Equal(2, result.Threats.Count);
    }

    [Fact]
    public void AScanThatDidNotRunIsNeverReportedAsClean()
    {
        // The whole point. Exit code 0 with no evidence a scan happened is not a
        // verdict, and must not be dressed as one.
        var result = DefenderBridge.Interpret(1, "MpCmdRun could not start the engine.");

        Assert.Equal(DefenderState.CouldNotRun, result.State);
        Assert.NotEqual(DefenderState.Clean, result.State);
    }

    [Fact]
    public void EmptyOutputIsNotAVerdict()
    {
        Assert.Equal(DefenderState.CouldNotRun, DefenderBridge.Interpret(0, string.Empty).State);
    }

    [Fact]
    public void ADisabledDefenderSaysSoRatherThanFailingQuietly()
    {
        var result = DefenderBridge.Interpret(-1, "The Service is not running.");

        Assert.Equal(DefenderState.NotAvailable, result.State);
    }

    [Fact]
    public void ThreeOutcomesStayThreeDistinctStates()
    {
        var clean = DefenderBridge.Interpret(0, "Scan finished. found no threats.").State;
        var found = DefenderBridge.Interpret(2, "Threat information: X\nScan finished.").State;
        var broken = DefenderBridge.Interpret(5, "something went wrong").State;

        Assert.Equal(3, new[] { clean, found, broken }.Distinct().Count());
    }

    [Fact]
    public void TheHeadingSaysACouldNotRunScanIsNotClean()
    {
        var summary = MalwareRemovalViewModel.Summarise(0, 0, DefenderState.CouldNotRun);

        Assert.Contains("not the same as clean", summary.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("warning", summary.Tone);
    }

    [Fact]
    public void ADisabledDefenderAlsoWarnsRatherThanReassures()
    {
        var summary = MalwareRemovalViewModel.Summarise(0, 0, DefenderState.NotAvailable);

        Assert.Contains("not the same as clean", summary.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("good", summary.Tone);
    }

    [Fact]
    public void HidingWithoutAVerdictIsNotCalledMalware()
    {
        // Smart Lab identifies hiding behaviour. Naming a program is Defender's job,
        // and the heading must not blur the two.
        var summary = MalwareRemovalViewModel.Summarise(hiding: 4, defender: 0, DefenderState.Clean);

        Assert.Equal("Hiding found, nothing named", summary.Headline);
    }

    [Fact]
    public void EveryDriveInTheMachineMeansFixedAndRemovable()
    {
        // A network drive is not in this machine: scanning one reads somebody else's
        // server and remediates on their disk. Optical media cannot be cleaned, and a
        // drive that is not ready has no filesystem to walk.
        Assert.True(DefenderBridge.IsScannable(DriveType.Fixed, isReady: true));
        Assert.True(DefenderBridge.IsScannable(DriveType.Removable, isReady: true));

        Assert.False(DefenderBridge.IsScannable(DriveType.Network, isReady: true));
        Assert.False(DefenderBridge.IsScannable(DriveType.CDRom, isReady: true));
        Assert.False(DefenderBridge.IsScannable(DriveType.Fixed, isReady: false));
    }

    [Fact]
    public void TheSweepFindsThisMachinesSystemDrive()
    {
        // The one claim worth making against the real machine: whatever else is
        // plugged in, a sweep that misses the drive Windows is on is not a sweep.
        var system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));

        Assert.Contains(DefenderBridge.ScannableDrives(), r =>
            string.Equals(r, system, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OneUnreadableDriveNeverAveragesIntoClean()
    {
        // The single-path rule, applied across a list. A drive that could not be
        // scanned must not hide inside a machine-wide "clean".
        var mixed = DefenderBridge.Aggregate(
            [DefenderState.Clean, DefenderState.Clean, DefenderState.CouldNotRun]);

        Assert.Equal(DefenderState.CouldNotRun, mixed);
    }

    [Fact]
    public void OneDriveWithThreatsDecidesTheWholeSweep()
    {
        var found = DefenderBridge.Aggregate(
            [DefenderState.Clean, DefenderState.ThreatsFound, DefenderState.CouldNotRun]);

        Assert.Equal(DefenderState.ThreatsFound, found);

        Assert.Equal(
            DefenderState.Clean,
            DefenderBridge.Aggregate([DefenderState.Clean, DefenderState.Clean]));
    }

    [Fact]
    public void ASweepOfNothingIsNotCleanEither()
    {
        Assert.Equal(DefenderState.CouldNotRun, DefenderBridge.Aggregate([]));
    }

    [Fact]
    public void RemovalNeverTouchesDefendersDefinitions()
    {
        // -RemoveDefinitions deletes Defender's signatures. It reads like the removal
        // switch and is the opposite of one, and MpCmdRun has no removal switch at all.
        var command = DefenderBridge.BuildRemoveCommand();

        Assert.DoesNotContain("RemoveDefinitions", command, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MpCmdRun", command, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Remove-MpThreat", command, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovalProvesItselfRatherThanTrustingAnExitCode()
    {
        // PowerShell exits 0 even when a cmdlet writes an error, so an access denied -
        // what a refused prompt produces - would report as a successful removal. And a
        // command that returned is not the same as a machine with nothing left active.
        var command = DefenderBridge.BuildRemoveCommand();

        Assert.Contains("$ErrorActionPreference='Stop'", command, StringComparison.Ordinal);
        Assert.Contains("Get-MpThreat", command, StringComparison.Ordinal);
        Assert.Contains("IsActive", command, StringComparison.Ordinal);
        Assert.Contains("exit 2", command, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReadBackWaitsForDefendersBookkeeping()
    {
        // IsActive is Defender's record, not the file's state: against a real EICAR
        // detection it read active for seconds after the file was already gone. Reading
        // it once turns a removal that worked into "it is still there".
        var command = DefenderBridge.BuildRemoveCommand();

        Assert.Contains("Start-Sleep", command, StringComparison.Ordinal);
        Assert.Contains("AddSeconds(30)", command, StringComparison.Ordinal);
    }

    [Fact]
    public void AStoppedSweepDoesNotClaimTheMachineIsClean()
    {
        var partial = MalwareRemovalViewModel.Describe(
            [@"C:\", @"D:\", @"E:\"], scanned: 1, unscanned: [], DefenderState.Clean);

        Assert.Contains("1 of 3", partial, StringComparison.Ordinal);
        Assert.DoesNotContain("found nothing", partial, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASweepNamesTheDrivesItCouldNotScan()
    {
        // Counting them leaves the operator to work out which, and the one they need
        // is the one they were worried about.
        var described = MalwareRemovalViewModel.Describe(
            [@"C:\", @"D:\"], scanned: 2, unscanned: [@"D:\"], DefenderState.CouldNotRun);

        Assert.Contains(@"D:\", described, StringComparison.Ordinal);
        Assert.Contains("not the same as clean", described, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindingsStayAttributableToTheirSource()
    {
        // Merged into one list, but each row still says which half produced it -
        // otherwise "4 findings" mixes two different kinds of claim.
        Assert.NotEqual(MalwareRemovalViewModel.HidingSource, MalwareRemovalViewModel.DefenderSource);
        Assert.Contains("Smart Lab", MalwareRemovalViewModel.HidingSource, StringComparison.Ordinal);
        Assert.Contains("Defender", MalwareRemovalViewModel.DefenderSource, StringComparison.Ordinal);
    }
}
