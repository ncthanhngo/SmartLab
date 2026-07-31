using SmartLab.App;
using Xunit;

namespace SmartLab.Tests;

/// <summary>
/// The About page, which is the only screen that makes claims about the build itself.
/// </summary>
public sealed class AboutTests
{
    [Fact]
    public void TheNewestReleaseNoteIsTheVersionTheAppReports()
    {
        // A build that ships with the previous version's notes tells the operator it
        // fixed things it did not, which is worse than shipping no notes at all.
        Assert.Equal(MainViewModel.AppVersion, AboutViewModel.Current.Version);
    }

    [Fact]
    public void ReleaseNotesRunNewestFirst()
    {
        var versions = AboutViewModel.ReleaseNotes.Select(n => Version.Parse(n.Version)).ToArray();

        Assert.Equal(versions.OrderByDescending(v => v).ToArray(), versions);
    }

    [Fact]
    public void EveryReleaseNoteCarriesAReadableVersionAndDate()
    {
        foreach (var note in AboutViewModel.ReleaseNotes)
        {
            Assert.True(Version.TryParse(note.Version, out _), $"'{note.Version}' is not a version");
            Assert.True(DateOnly.TryParse(note.Date, out _), $"'{note.Date}' is not a date");
            Assert.True(note.HasAdditions || note.HasFixes, $"{note.Version} says nothing");
        }
    }

    [Fact]
    public void EverySectionThatDoesAJobIsDescribed()
    {
        // The list is derived from the rail rather than written twice. This is what
        // says so: a section added to the rail turns up here without an edit.
        var shell = new MainViewModel();

        var described = shell.About.FeatureGroups.SelectMany(g => g.Features).Select(f => f.Title).ToHashSet();

        var expected = shell.Sections
            .Where(s => s.Group.Length > 0 && s.Group != MainViewModel.GroupApp)
            .Select(s => s.Title);

        Assert.All(expected, title => Assert.Contains(title, described));
    }

    [Fact]
    public void TheAppsOwnPagesAreNotListedAsFeatures()
    {
        var shell = new MainViewModel();

        var described = shell.About.FeatureGroups.SelectMany(g => g.Features).Select(f => f.Title).ToArray();

        Assert.DoesNotContain("Settings", described);
        Assert.DoesNotContain("About", described);
        Assert.DoesNotContain("Home", described);
    }

    [Fact]
    public void FeatureGroupsFollowTheRailsOrder()
    {
        var shell = new MainViewModel();

        var fromRail = shell.Sections
            .Where(s => s.Group.Length > 0 && s.Group != MainViewModel.GroupApp)
            .Select(s => s.Group)
            .Distinct()
            .ToArray();

        Assert.Equal(fromRail, shell.About.FeatureGroups.Select(g => g.Name).ToArray());
    }

    [Theory]
    [InlineData("v0.2.0")]
    [InlineData("0.2.0")]
    [InlineData("  V0.2.0  ")]
    public void ANewerPublishedReleaseIsReported(string tag)
    {
        var (verdict, status) = AboutViewModel.Compare("0.1.0", tag);

        Assert.Equal(UpdateVerdict.Available, verdict);
        Assert.Contains("0.2.0", status, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameVersionReadsAsUpToDate()
    {
        var (verdict, _) = AboutViewModel.Compare("0.1.0", "v0.1.0");

        Assert.Equal(UpdateVerdict.Current, verdict);
    }

    [Fact]
    public void ABuildAheadOfTheFeedIsNotCalledOutOfDate()
    {
        // Running from source is the normal case in this repository.
        var (verdict, status) = AboutViewModel.Compare("0.3.0", "v0.2.0");

        Assert.Equal(UpdateVerdict.Current, verdict);
        Assert.Contains("ahead", status, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nightly")]
    [InlineData("v")]
    public void ATagThatIsNotAVersionIsUnknownRatherThanNewer(string? tag)
    {
        // A repository with no release, or one tagged with a word, must never tell
        // someone their build is out of date.
        var (verdict, _) = AboutViewModel.Compare("0.1.0", tag);

        Assert.Equal(UpdateVerdict.Unknown, verdict);
    }

    [Fact]
    public void NothingIsClaimedBeforeACheckHasRun()
    {
        var shell = new MainViewModel();

        Assert.Equal(UpdateVerdict.Unchecked, shell.About.Verdict);
        Assert.False(shell.About.HasNewerVersion);
        Assert.Empty(shell.About.LatestVersion);
    }
}
