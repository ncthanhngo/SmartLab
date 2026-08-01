using SmartLab.App;
using SmartLab.Core.Abstractions;
using SmartLab.Engine.Journal;
using Xunit;

namespace SmartLab.Tests;

/// <summary>
/// Reading back what a run put in quarantine, so it can be put where it was.
/// </summary>
/// <remarks>
/// Quarantine has always been a move rather than a delete, and nothing ever moved
/// anything back. The store holds sanitised names and nothing saying where any of
/// them came from - except the journal, which recorded the copy and its destination
/// at the time. That pairing is what these hold.
/// </remarks>
public sealed class QuarantineRestoreTests
{
    private static JournalRecord Record(string kind, string target, bool ok, string? detail = null) =>
        new(DateTimeOffset.UtcNow, kind, target, ok, detail);

    private static JournalRunViewModel Run(params JournalRecord[] records) =>
        new(new JournalRun(DateTimeOffset.UtcNow, @"E:\", records, "done", true));

    [Fact]
    public void AQuarantinedFileIsPairedWithWhereItCameFrom()
    {
        var run = Run(
            Record("plan-begin", @"E:\", true, "1 approved action(s)"),
            Record("copy", @"E:\RECYCLER.BIN\bad.exe", true,
                @"-> C:\Users\x\SmartLab\quarantine\bad.exe.quarantined"),
            Record("delete", @"E:\RECYCLER.BIN\bad.exe", true),
            Record("plan-end", @"E:\", true, "2 succeeded, 0 failed"));

        var pair = Assert.Single(run.Quarantined);

        Assert.Equal(@"E:\RECYCLER.BIN\bad.exe", pair.Original);
        Assert.Equal(@"C:\Users\x\SmartLab\quarantine\bad.exe.quarantined", pair.Stored);
    }

    /// <remarks>
    /// The gate appends its own note after the destination when a name had to be
    /// sanitised, and a path with that note glued onto it is a path that does not
    /// exist.
    /// </remarks>
    [Fact]
    public void ANoteAfterTheDestinationIsNotPartOfThePath()
    {
        var run = Run(Record("copy", @"E:\odd name.exe", true,
            @"-> C:\q\odd_name.exe.quarantined (original name preserved here)"));

        Assert.Equal(@"C:\q\odd_name.exe.quarantined", Assert.Single(run.Quarantined).Stored);
    }

    [Fact]
    public void ACopyThatWasNotAQuarantineIsNotOffered()
    {
        // A rescue copy is a copy too, and putting one "back" would mean writing over
        // the original it was taken from.
        var run = Run(Record("copy", @"E:\photo.jpg", true, @"-> C:\Users\x\SmartLab\rescue\photo.jpg"));

        Assert.Empty(run.Quarantined);
    }

    [Fact]
    public void ACopyThatFailedIsNotOffered()
    {
        var run = Run(Record("copy", @"E:\bad.exe", false,
            @"-> C:\q\bad.exe.quarantined Access is denied."));

        Assert.Empty(run.Quarantined);
    }

    [Fact]
    public void ARunThatQuarantinedNothingOffersNothingToPutBack()
    {
        var run = Run(
            Record("plan-begin", @"E:\", true, "1 approved action(s)"),
            Record("clear-attributes", @"E:\hidden", true, "remove Hidden"),
            Record("plan-end", @"E:\", true, "1 succeeded, 0 failed"));

        Assert.Empty(run.Quarantined);
        Assert.False(run.CanRestore);
    }

    [Fact]
    public void APairIsOnlyRestorableWhileTheStoredCopyIsStillThere()
    {
        // Somebody may have emptied the quarantine folder by hand, and offering to
        // put back a file that is gone is a button that can only disappoint.
        var run = Run(Record("copy", @"E:\bad.exe", true,
            @"-> C:\definitely\not\here\bad.exe.quarantined"));

        Assert.Single(run.Quarantined);
        Assert.False(run.CanRestore);
    }
}
