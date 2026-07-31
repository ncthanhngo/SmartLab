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
    public void FindingsStayAttributableToTheirSource()
    {
        // Merged into one list, but each row still says which half produced it -
        // otherwise "4 findings" mixes two different kinds of claim.
        Assert.NotEqual(MalwareRemovalViewModel.HidingSource, MalwareRemovalViewModel.DefenderSource);
        Assert.Contains("Smart Lab", MalwareRemovalViewModel.HidingSource, StringComparison.Ordinal);
        Assert.Contains("Defender", MalwareRemovalViewModel.DefenderSource, StringComparison.Ordinal);
    }
}
