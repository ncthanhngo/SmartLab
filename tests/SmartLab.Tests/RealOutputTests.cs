using SmartLab.Maintenance;
using Xunit;

namespace SmartLab.Tests;

/// <summary>
/// The parsers, run against output these tools actually produced.
/// </summary>
/// <remarks>
/// <para>
/// The hand-written samples elsewhere test the shapes the parsers were designed for.
/// These test the shapes the tools really emit, captured on 2026-07-31 from a live
/// machine and committed verbatim - column widths, real package names, the trailing
/// summary line, the sentence Defender ends a clean scan with.
/// </para>
/// <para>
/// Fixtures rather than live invocations. A test that shells out to winget depends on
/// the network, the machine's package set and how long a source refresh takes, and
/// would fail for reasons that have nothing to do with this code.
/// </para>
/// </remarks>
public sealed class RealOutputTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    // ---- winget ------------------------------------------------------------------

    [Fact]
    public void RealWingetOutputParsesEveryRow()
    {
        var packages = WingetBridge.ParseUpgrades(Fixture("winget-upgrade.txt"));

        Assert.Equal(7, packages.Count);
    }

    [Fact]
    public void ANameWithSpacesDashesAndParenthesesSurvives()
    {
        // "Microsoft Windows Desktop Runtime - 8.0.3 (x64)" is the row that would
        // break a whitespace split, and the reason the parser reads column offsets.
        var packages = WingetBridge.ParseUpgrades(Fixture("winget-upgrade.txt"));

        var runtime = packages.Single(p => p.Id == "Microsoft.DotNet.DesktopRuntime.8");

        Assert.Equal("Microsoft Windows Desktop Runtime - 8.0.3 (x64)", runtime.Name);
        Assert.Equal("8.0.3", runtime.Installed);
        Assert.Equal("8.0.29", runtime.Available);
    }

    [Fact]
    public void AVersionInsideTheNameIsNotMistakenForTheVersionColumn()
    {
        // "Zalo 26.2.10" carries a version in its display name. A parser that hunted
        // for the first version-looking token would read the wrong one.
        var zalo = WingetBridge.ParseUpgrades(Fixture("winget-upgrade.txt"))
            .Single(p => p.Id == "VNGCorp.Zalo");

        Assert.Equal("Zalo 26.2.10", zalo.Name);
        Assert.Equal("26.2.10", zalo.Installed);
        Assert.Equal("26.5.20", zalo.Available);
    }

    [Fact]
    public void TheTrailingCountLineIsNotReadAsAPackage()
    {
        var packages = WingetBridge.ParseUpgrades(Fixture("winget-upgrade.txt"));

        Assert.DoesNotContain(packages, p => p.Id.Contains("upgrades", StringComparison.OrdinalIgnoreCase));
        Assert.All(packages, p => Assert.Equal("winget", p.Source));
    }

    [Fact]
    public void EveryRealRowKnowsItCameFromWinget()
    {
        // None of these were hand-placed, so none should be flagged as such. The flag
        // firing on a normal machine would make the warning meaningless.
        Assert.All(WingetBridge.ParseUpgrades(Fixture("winget-upgrade.txt")),
            p => Assert.False(p.NotFromWinget));
    }

    // ---- Defender ----------------------------------------------------------------

    [Fact]
    public void ARealCleanScanReadsAsClean()
    {
        // Captured from MpCmdRun.exe -Scan -ScanType 3, exit code 0. The wording is
        // the point: the parser looks for this sentence, not merely for a zero exit.
        var result = DefenderBridge.Interpret(0, Fixture("defender-clean.txt"));

        Assert.Equal(DefenderState.Clean, result.State);
        Assert.Empty(result.Threats);
    }

    [Fact]
    public void TheSameOutputWithANonZeroExitIsNotClean()
    {
        // Defender exiting non-zero after printing a clean transcript means something
        // went wrong afterwards, and the section must not report a verdict it did not
        // get.
        var result = DefenderBridge.Interpret(5, Fixture("defender-clean.txt"));

        Assert.NotEqual(DefenderState.Clean, result.State);
    }

    [Fact]
    public void ARealScanArgumentIsWhatWasActuallyRun()
    {
        // The exact argument string that produced the fixture above.
        Assert.Equal(
            "-Scan -ScanType 3 -File \"C:\\scratch\\realdata\"",
            DefenderBridge.BuildScanArguments(@"C:\scratch\realdata"));
    }
}
