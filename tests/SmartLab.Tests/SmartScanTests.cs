using System.Reflection;
using SmartLab.App;
using Xunit;

namespace SmartLab.Tests;

/// <summary>
/// The summary screen, and the button it must never grow.
/// </summary>
public sealed class SmartScanTests
{
    [Fact]
    public void SmartScanExposesNoWayToChangeAnything()
    {
        // The test this class exists for. One button that cleans, disables, removes
        // and upgrades across a whole machine is what plan-then-approve was designed
        // to prevent - so adding one has to fail here rather than ship quietly.
        string[] forbidden =
            ["clean", "remove", "delete", "upgrade", "disable", "empty", "shred", "apply", "fix"];

        var commands = typeof(SmartScanViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => typeof(System.Windows.Input.ICommand).IsAssignableFrom(p.PropertyType))
            .Select(p => p.Name)
            .ToArray();

        foreach (var command in commands)
        {
            foreach (var verb in forbidden)
            {
                Assert.False(command.Contains(verb, StringComparison.OrdinalIgnoreCase),
                    $"SmartScanViewModel exposes '{command}', which acts rather than measures.");
            }
        }
    }

    [Fact]
    public void ItsOnlyCommandsAreScanStopAndNavigate()
    {
        var commands = typeof(SmartScanViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => typeof(System.Windows.Input.ICommand).IsAssignableFrom(p.PropertyType))
            .Select(p => p.Name)
            .Order()
            .ToArray();

        Assert.Equal(["CancelCommand", "OpenCommand", "ScanCommand"], commands);
    }

    [Fact]
    public void ASkippedSectionIsNeverCountedAsClean()
    {
        // The easiest lie a summary screen can tell, and the hardest for a reader to
        // notice: seven green rows, one of which never ran.
        var summary = SmartScanViewModel.Summarise(
            findings: 0, sections: 6, skipped: 2, worstTone: "good", hasRun: true);

        Assert.NotEqual("Nothing needs attention", summary.Headline);
        Assert.Contains("not counted as clean", summary.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("warning", summary.Tone);
    }

    [Fact]
    public void NothingFoundAndNothingSkippedIsClean()
    {
        var summary = SmartScanViewModel.Summarise(0, 6, 0, "good", hasRun: true);

        Assert.Equal("Nothing needs attention", summary.Headline);
        Assert.Equal("good", summary.Tone);
    }

    [Fact]
    public void TheWorstToneWins()
    {
        // A machine with one worm and five tidy sections is not "mostly fine", and an
        // average would say it was.
        var summary = SmartScanViewModel.Summarise(3, 6, 0, "danger", hasRun: true);

        Assert.Equal("danger", summary.Tone);
        Assert.Equal("Needs attention now", summary.Headline);
    }

    [Fact]
    public void BeforeRunningNothingIsClaimed()
    {
        var summary = SmartScanViewModel.Summarise(0, 0, 0, "neutral", hasRun: false);

        Assert.Equal("Ready when you are", summary.Headline);
        Assert.Equal("neutral", summary.Tone);
    }

    [Fact]
    public void TheHeadingSaysItDoesNotAct()
    {
        var summary = SmartScanViewModel.Summarise(4, 6, 0, "warning", hasRun: true);

        Assert.Contains("nothing here acts", summary.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASkippedOutcomeIsDistinctFromZeroFindings()
    {
        var skipped = new SectionOutcome("Updater", 0, "neutral", "winget missing", Skipped: true);
        var clean = new SectionOutcome("Updater", 0, "good", "everything current");

        Assert.True(skipped.Skipped);
        Assert.False(clean.Skipped);
        Assert.NotEqual(skipped, clean);
    }
}
