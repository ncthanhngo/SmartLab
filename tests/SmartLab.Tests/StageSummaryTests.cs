using SmartLab.App;
using Xunit;

namespace SmartLab.Tests;

/// <summary>
/// The headings above the two dials that were added last.
/// </summary>
/// <remarks>
/// Every section now opens on one number and one sentence, which makes that sentence
/// the whole of what an operator reads before pressing something. These are the cases
/// where a plausible wording would be actively misleading rather than merely dull.
/// </remarks>
public sealed class StageSummaryTests
{
    // ---- deleted files ----------------------------------------------------------

    [Fact]
    public void NothingFound_AndNothingRecoverable_AreDifferentSentences()
    {
        // An empty list means the deletions are not in the directory structures at
        // all; a full list with nothing recoverable means they are there and the data
        // is gone. Only the second says "stop looking".
        var empty = MainViewModel.SummariseDeleted(total: 0, recoverable: 0);
        var allGone = MainViewModel.SummariseDeleted(total: 40, recoverable: 0);

        Assert.NotEqual(empty.Headline, allGone.Headline);
        Assert.NotEqual(empty.Detail, allGone.Detail);
    }

    [Fact]
    public void WithRecoverableFiles_TheDetailCarriesBothNumbers()
    {
        // The dial shows only the recoverable count, so the denominator has to be in
        // the sentence or "12" alone reads as everything that was deleted.
        var summary = MainViewModel.SummariseDeleted(total: 80, recoverable: 12);

        Assert.Contains("12", summary.Detail, StringComparison.Ordinal);
        Assert.Contains("80", summary.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveryIsNeverPromised()
    {
        // Carving reads forward from the starting cluster, which is correct only when
        // the file was not fragmented. Every result is a candidate.
        var summary = MainViewModel.SummariseDeleted(total: 80, recoverable: 12);

        Assert.Contains("fragmented", summary.Detail, StringComparison.OrdinalIgnoreCase);
    }

    // ---- uninstall --------------------------------------------------------------

    [Fact]
    public void TracesFoundButNoneTicked_SaysSoRatherThanReadyToRemove()
    {
        // The ring is empty in this state. A heading saying "ready to remove" over an
        // empty ring invites the operator to press a button that would do nothing.
        var summary = UninstallViewModel.SummariseTraces(total: 9, ticked: 0, userData: 0);

        Assert.Equal("Nothing ticked", summary.Headline);
    }

    [Fact]
    public void RescuedData_IsCalledOutEvenThoughItStartsUnticked()
    {
        // The most important line on the screen: those files may be the only copy left
        // of a drive that has since been formatted.
        var summary = UninstallViewModel.SummariseTraces(total: 9, ticked: 7, userData: 2);

        Assert.Contains("only copy", summary.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithNoRescuedData_TheWarningIsAbsent()
    {
        // A warning shown when there is nothing to warn about is decoration, and the
        // operator learns to read past it.
        var summary = UninstallViewModel.SummariseTraces(total: 9, ticked: 9, userData: 0);

        Assert.DoesNotContain("only copy", summary.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BeforeScanning_NeitherDialClaimsAResult()
    {
        var deleted = MainViewModel.SummariseDeleted(total: 0, recoverable: 0);
        var traces = UninstallViewModel.SummariseTraces(total: 0, ticked: 0, userData: 0);

        Assert.Equal("Nothing read yet", deleted.Headline);
        Assert.Equal("Not scanned yet", traces.Headline);
    }
}
