using System.Reflection;
using SmartLab.App;
using Xunit;

namespace SmartLab.Tests;

/// <summary>
/// The front door's two-step flow.
/// </summary>
/// <remarks>
/// Home can act now, which it could not before. What keeps that safe is not the
/// absence of a verb but the shape of it: measuring and acting are separate presses,
/// the second is impossible until the first has finished, and it works only on what
/// the first found. These tests hold that shape.
/// </remarks>
public sealed class SmartScanTests
{
    [Fact]
    public void MeasuringAndActingAreSeparateCommands()
    {
        // The single most important property of this screen. One command that both
        // scanned and applied would be the "Fix everything" button that
        // plan-then-approve exists to prevent, whatever it happened to be called.
        var commands = typeof(SmartScanViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => typeof(System.Windows.Input.ICommand).IsAssignableFrom(p.PropertyType))
            .Select(p => p.Name)
            .Order()
            .ToArray();

        Assert.Equal(
            ["ApplyCommand", "CancelCommand", "OpenCommand", "OpenPillarCommand", "ScanCommand"],
            commands);
    }

    [Fact]
    public void NothingCanBeAppliedBeforeAScanHasRun()
    {
        // Apply works from what the scan found. With nothing found there is nothing
        // to work from, and the button must be dead rather than merely unhelpful.
        var scan = new MainViewModel().SmartScan;

        Assert.Equal(ScanPhase.Ready, scan.Phase);
        Assert.False(scan.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public void NothingCanBeAppliedWhileTheScanIsStillRunning()
    {
        var scan = new MainViewModel().SmartScan;
        scan.Phase = ScanPhase.Scanning;

        Assert.False(scan.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public void ScanningIsNotOfferedWhileAlreadyScanningOrApplying()
    {
        var scan = new MainViewModel().SmartScan;

        scan.Phase = ScanPhase.Scanning;
        Assert.False(scan.ScanCommand.CanExecute(null));

        scan.Phase = ScanPhase.Applying;
        Assert.False(scan.ScanCommand.CanExecute(null));

        scan.Phase = ScanPhase.Reviewing;
        Assert.True(scan.ScanCommand.CanExecute(null));
    }

    [Fact]
    public void APhaseCarriesExactlyOneMeaning()
    {
        // Scanning and reviewing must never both be true: the button reads Run in one
        // and Confirm in the other, and a screen showing both has lied about which
        // press the operator is about to make.
        var scan = new MainViewModel().SmartScan;

        foreach (var phase in Enum.GetValues<ScanPhase>())
        {
            scan.Phase = phase;
            Assert.False(scan.IsScanning && scan.IsReviewing);
        }
    }

    [Fact]
    public void ASkippedSectionIsNeverActionable()
    {
        // It could not look, so it has nothing to act on. Applying it would act on
        // whatever stale state the section happened to be holding.
        var outcome = new SectionOutcome("Updater", 0, "neutral", "winget missing", Skipped: true);

        Assert.False(outcome.IsActionable);
    }

    // ---- the headline ------------------------------------------------------------

    [Fact]
    public void ASkippedSectionIsNeverCountedAsClean()
    {
        // The easiest lie a summary screen can tell, and the hardest for a reader to
        // notice: six green rows, one of which never ran.
        var summary = SmartScanViewModel.Summarise(
            findings: 0, sections: 6, skipped: 2, worstTone: "good", ScanPhase.Reviewing);

        Assert.NotEqual("Nothing needs attention", summary.Headline);
        Assert.Contains("not counted as clean", summary.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("warning", summary.Tone);
    }

    [Fact]
    public void NothingFoundAndNothingSkippedIsClean()
    {
        var summary = SmartScanViewModel.Summarise(0, 6, 0, "good", ScanPhase.Reviewing);

        Assert.Equal("Nothing needs attention", summary.Headline);
        Assert.Equal("good", summary.Tone);
    }

    [Fact]
    public void TheWorstToneWins()
    {
        // A machine with one worm and five tidy sections is not "mostly fine", and an
        // average would say it was.
        var summary = SmartScanViewModel.Summarise(3, 6, 0, "danger", ScanPhase.Reviewing);

        Assert.Equal("danger", summary.Tone);
        Assert.Equal("Needs attention now", summary.Headline);
    }

    [Fact]
    public void BeforeRunningNothingIsClaimed()
    {
        var summary = SmartScanViewModel.Summarise(0, 0, 0, "neutral", ScanPhase.Ready);

        Assert.Equal("Ready when you are", summary.Headline);
        Assert.Equal("neutral", summary.Tone);
    }

    [Fact]
    public void WhileScanningTheHeadlineSaysNothingIsBeingChanged()
    {
        var summary = SmartScanViewModel.Summarise(0, 0, 0, "neutral", ScanPhase.Scanning);

        Assert.Contains("nothing is being changed", summary.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheReviewHeadlineSaysNothingHasHappenedYet()
    {
        // The moment the operator decides. It has to be unambiguous that the scan
        // changed nothing and that the next press is what will.
        var summary = SmartScanViewModel.Summarise(4, 6, 0, "warning", ScanPhase.Reviewing);

        Assert.Contains("nothing has been changed", summary.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("confirm", summary.Detail, StringComparison.OrdinalIgnoreCase);
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
