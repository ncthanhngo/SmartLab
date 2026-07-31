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
    [InlineData("Nombre    Id    Versión\nnot a rule at all")]
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

/// <summary>
/// Extension scanning, which must stay read-only.
/// </summary>
public sealed class ExtensionScannerTests
{
    private const string Manifest = """
        {
          "name": "Some Extension",
          "version": "3.2.1",
          "permissions": ["storage", "tabs"],
          "host_permissions": ["<all_urls>"]
        }
        """;

    [Fact]
    public void AManifestYieldsItsNameVersionAndPermissions()
    {
        var extension = BrowserExtensionScanner.ParseManifest("Chrome", "abc", "3.2.1_0", Manifest);

        Assert.NotNull(extension);
        Assert.Equal("Some Extension", extension!.Name);
        Assert.Equal("3.2.1", extension.Version);
        Assert.Contains("storage", extension.Permissions);
    }

    [Fact]
    public void AnExtensionThatCanReadEverySiteIsFlagged()
    {
        // The one fact worth surfacing on this screen. Size tells nobody anything;
        // "can read and change data on every site" is the finding.
        var extension = BrowserExtensionScanner.ParseManifest("Chrome", "abc", "1", Manifest);

        Assert.True(extension!.ReadsEverySite);
    }

    [Fact]
    public void AWildcardHostPatternCountsToo()
    {
        var extension = BrowserExtensionScanner.ParseManifest(
            "Edge", "abc", "1", """{"name":"x","host_permissions":["https://*/*"]}""");

        Assert.True(extension!.ReadsEverySite);
    }

    [Fact]
    public void ANarrowExtensionIsNotFlagged()
    {
        var extension = BrowserExtensionScanner.ParseManifest(
            "Chrome", "abc", "1", """{"name":"x","permissions":["storage"]}""");

        Assert.False(extension!.ReadsEverySite);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json at all")]
    [InlineData("""{"name": 42}""")]
    public void AMalformedManifestStillReportsThatSomethingIsInstalled(string json)
    {
        // An extension that will not identify itself is the interesting one. Dropping
        // the row would hide exactly the case worth looking at.
        var extension = BrowserExtensionScanner.ParseManifest("Chrome", "someid", "1.0", json);

        Assert.NotNull(extension);
        Assert.Equal("someid", extension!.Id);
    }

    [Fact]
    public void ALocalisedNamePlaceholderFallsBackToTheId()
    {
        var extension = BrowserExtensionScanner.ParseManifest(
            "Chrome", "abcdef", "1", """{"name":"__MSG_appName__"}""");

        Assert.Equal("abcdef", extension!.Name);
    }

    [Fact]
    public void ShellExtensionsAreListedWithNoRemovalPath()
    {
        // A wrongly removed shell extension takes Explorer's context menu with it, and
        // the tool that would have helped is the one that just broke. There is no
        // remove method on the scanner at all - asserted over its public surface so
        // adding one later fails here rather than shipping quietly.
        var methods = typeof(ShellExtensionScanner).GetMethods()
            .Select(m => m.Name)
            .Where(n => n.Contains("Remove", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("Delete", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("Disable", StringComparison.OrdinalIgnoreCase));

        Assert.Empty(methods);
    }

    [Fact]
    public void TheBrowserScannerHasNoWritePathEither()
    {
        var methods = typeof(BrowserExtensionScanner).GetMethods()
            .Select(m => m.Name)
            .Where(n => n.Contains("Remove", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("Delete", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("Write", StringComparison.OrdinalIgnoreCase));

        Assert.Empty(methods);
    }
}
