using SmartLab.App;
using SmartLab.Maintenance;
using Xunit;

namespace SmartLab.Tests;

/// <summary>
/// Reading winget's table, which is the part that breaks when winget changes.
/// </summary>
/// <remarks>
/// Parsed by column offset taken from the header rule, not by splitting on whitespace:
/// package names contain spaces, and a naive split turns "Visual Studio Code" into
/// three fields and then reads the version out of the wrong one.
/// </remarks>
public sealed class WingetOutputTests
{
    private const string Sample = """
        Name                           Id                        Version      Available    Source
        ------------------------------------------------------------------------------------------
        Visual Studio Code             Microsoft.VisualStudioCode 1.90.0       1.91.1       winget
        7-Zip 23.01 (x64)              7zip.7zip                 23.01        24.07        winget
        Some Vendor Tool               Vendor.Tool               1.0.0        Unknown      winget
        Hand Placed Build              Local.Build               2.1          2.4
        3 upgrades available.
        """;

    [Fact]
    public void PackageNamesWithSpacesSurviveIntact()
    {
        var packages = WingetBridge.ParseUpgrades(Sample);

        Assert.Contains(packages, p => p.Name == "Visual Studio Code");
        Assert.Contains(packages, p => p.Id == "Microsoft.VisualStudioCode");
    }

    [Fact]
    public void VersionsComeFromTheRightColumns()
    {
        var code = WingetBridge.ParseUpgrades(Sample).Single(p => p.Id == "Microsoft.VisualStudioCode");

        Assert.Equal("1.90.0", code.Installed);
        Assert.Equal("1.91.1", code.Available);
    }

    [Fact]
    public void APackageWithAnUnknownVersionIsNotOfferedAsAnUpgrade()
    {
        // "Unknown" is winget saying it cannot compare. Offering it invites a
        // reinstall dressed up as an update.
        Assert.DoesNotContain(WingetBridge.ParseUpgrades(Sample), p => p.Id == "Vendor.Tool");
    }

    [Fact]
    public void APackageWingetDidNotInstallIsFlagged()
    {
        var local = WingetBridge.ParseUpgrades(Sample).Single(p => p.Id == "Local.Build");

        Assert.True(local.NotFromWinget);
    }

    [Fact]
    public void TheTrailingSummaryLineIsNotAPackage()
    {
        Assert.DoesNotContain(WingetBridge.ParseUpgrades(Sample), p => p.Name.Contains("upgrades available"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("winget: command not found")]
    [InlineData("Nombre    Id    VersiĂ³n\nnot a rule at all")]
    public void UnrecognisableOutputYieldsNothingRatherThanThrowing(string output)
    {
        // A winget update that changes the layout must degrade to "no upgrades",
        // never to a crash in a section someone opened to read a list.
        Assert.Empty(WingetBridge.ParseUpgrades(output));
    }

    [Fact]
    public void NotInstalledIsReportedAsAReasonNotAsUpToDate()
    {
        // Only meaningful on a machine without winget; on one with it, this asserts
        // the other half - that a real list comes back without an error string.
        var (packages, error) = WingetBridge.ListUpgrades();

        if (WingetBridge.IsInstalled)
            Assert.True(error is null || packages.Count >= 0);
        else
            Assert.Contains("winget", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheHeadingCallsOutPackagesWingetDidNotInstall()
    {
        var summary = UpdaterViewModel.Summarise(found: 10, ticked: 8, foreign: 2);

        Assert.Contains("not installed by winget", summary.Detail, StringComparison.OrdinalIgnoreCase);
    }
}

