using SmartLab.App;
using Xunit;

namespace SmartLab.Tests;

/// <summary>
/// The object every waiting section now reports through.
/// </summary>
/// <remarks>
/// Small enough to look obviously right and central enough that being wrong is
/// invisible: a band stuck at "running" says a finished job is still going, and one
/// that never sets <see cref="SectionProgress.HasRun"/> takes the verdict off screen
/// the moment the work stops - which is the failure this whole thing exists to fix.
/// </remarks>
public sealed class SectionProgressTests
{
    [Fact]
    public void ABegunRunMovesWithoutClaimingAFigure()
    {
        var progress = new SectionProgress();

        progress.Begin("Walking the tree");

        Assert.True(progress.IsRunning);
        Assert.True(progress.IsIndeterminate);
        Assert.Equal("Walking the tree", progress.Stage);
        Assert.Equal(0, progress.Percent);
    }

    [Fact]
    public void AStepStatesItsFigureAndStopsBeingIndeterminate()
    {
        var progress = new SectionProgress();

        progress.Begin("Starting");
        progress.Step("Third of nine", 33);

        Assert.False(progress.IsIndeterminate);
        Assert.Equal(33, progress.Percent);
        Assert.Equal("Third of nine", progress.Stage);
    }

    [Fact]
    public void FinishingLeavesTheVerdictOnScreen()
    {
        var progress = new SectionProgress();

        progress.Begin("Measuring");
        progress.Finish("warning", "Measured, with leftovers", "Two things are still on disk.");

        Assert.False(progress.IsRunning);
        Assert.True(progress.HasRun);
        Assert.Equal(100, progress.Percent);
        Assert.Equal("warning", progress.Tone);
        Assert.Equal("Measured, with leftovers", progress.Completion);
        Assert.Equal("Two things are still on disk.", progress.CompletionDetail);
    }

    [Fact]
    public void ASecondRunClearsTheFirstVerdictBeforeItStarts()
    {
        // A green tick from the last run sitting beside a job still in progress is a
        // claim nobody has earned yet.
        var progress = new SectionProgress();

        progress.Begin("Measuring");
        progress.Finish("good", "Measured");

        progress.Begin("Measuring again");

        Assert.True(progress.IsRunning);
        Assert.Empty(progress.Completion);

        // HasRun stays: the section has run before, and the panels that depend on
        // that are not un-shown by starting again.
        Assert.True(progress.HasRun);
    }

    [Fact]
    public void ResetTakesTheBandOffScreenEntirely()
    {
        var progress = new SectionProgress();

        progress.Begin("Measuring");
        progress.Finish("good", "Measured");
        progress.Reset();

        Assert.False(progress.HasRun);
        Assert.False(progress.IsRunning);
        Assert.Empty(progress.Completion);
        Assert.Equal(0, progress.Percent);
    }
}
