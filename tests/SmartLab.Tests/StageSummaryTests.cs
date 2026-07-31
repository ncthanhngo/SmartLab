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
    //
    // The self-trace half of Uninstall left the window: it answered a question
    // nobody was asking on a screen about removing other programs. The scanner and
    // its rules are still covered where they still run - SelfTraceScannerTests, and
    // the command line's own report.

    [Fact]
    public void BeforeReadingAnythingTheDeletedDialClaimsNoResult()
    {
        var deleted = MainViewModel.SummariseDeleted(total: 0, recoverable: 0);

        Assert.Equal("Nothing read yet", deleted.Headline);
    }
}
