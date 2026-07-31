using SmartLab.App;
using Xunit;

namespace SmartLab.Tests;

/// <summary>
/// How the palette ranks, which decides what a fast keystroke lands on.
/// </summary>
/// <remarks>
/// A palette is used at speed and mostly without reading past the first row. That is
/// the whole convenience and the whole hazard, so the ordering rules are worth pinning
/// down: navigating is always safe, and an action that cannot be undone should never
/// be the thing Enter selects.
/// </remarks>
public sealed class CommandPaletteTests
{
    private static PaletteEntry Section(string title) => new(title, "context", isSection: true);

    private static PaletteEntry Action(string title, bool destructive = false) =>
        new(title, "context", isSection: false, destructive);

    [Fact]
    public void ASectionOutranksAnActionThatMatchesJustAsWell()
    {
        // Navigation cannot change anything, so at equal textual quality it is what
        // the first keystroke should reach.
        var section = CommandPaletteViewModel.Score(Section("Recycle Bins"), "recycle");
        var action = CommandPaletteViewModel.Score(Action("Recycle something"), "recycle");

        Assert.True(section > action);
    }

    [Fact]
    public void ADestructiveActionRanksBelowASafeOneOfTheSameQuality()
    {
        var safe = CommandPaletteViewModel.Score(Action("Measure every drive's trash"), "measure");
        var destructive = CommandPaletteViewModel.Score(Action("Measure and empty bins", destructive: true), "measure");

        Assert.True(safe > destructive);
    }

    [Fact]
    public void APrefixBeatsAWordStartWhichBeatsAContains()
    {
        var prefix = CommandPaletteViewModel.Score(Action("Trash the queue"), "trash");
        var word = CommandPaletteViewModel.Score(Action("Empty the trash bins"), "trash");
        var inside = CommandPaletteViewModel.Score(Action("Untrashed items"), "trash");

        Assert.True(prefix > word);
        Assert.True(word > inside);
    }

    [Fact]
    public void AnEntryThatDoesNotMatchIsExcluded()
    {
        Assert.Equal(int.MinValue, CommandPaletteViewModel.Score(Action("Wipe the queued files"), "outlook"));
    }

    [Fact]
    public void TheContextIsSearchableButRanksLast()
    {
        // Typing a section name should find its actions, and still put the section
        // itself above them.
        var byTitle = CommandPaletteViewModel.Score(Action("Empty the ticked Recycle Bins"), "empty");
        var byContext = CommandPaletteViewModel.Score(
            new PaletteEntry("Empty the ticked Recycle Bins", "Recycle Bins", isSection: false), "Bins");

        Assert.True(byTitle > byContext);
        Assert.NotEqual(int.MinValue, byContext);
    }

    [Fact]
    public void AnEmptyQueryStillPutsSectionsFirst()
    {
        // Opening the palette and pressing Enter without typing should navigate, not
        // run something.
        var section = CommandPaletteViewModel.Score(Section("Home"), string.Empty);
        var action = CommandPaletteViewModel.Score(Action("Check everything"), string.Empty);

        Assert.True(section > action);
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        Assert.Equal(
            CommandPaletteViewModel.Score(Section("Disk Map"), "disk"),
            CommandPaletteViewModel.Score(Section("Disk Map"), "DISK"));
    }

    [Fact]
    public void TheResultListStaysShortEnoughToReadWithoutScrolling()
    {
        // A palette that needs scrolling has stopped being faster than the rail.
        Assert.InRange(CommandPaletteViewModel.MaxResults, 5, 10);
    }
}

/// <summary>
/// The keyboard path: open, type, arrow, Enter.
/// </summary>
/// <remarks>
/// The window owns the key handling, but every decision it delegates lives here, so
/// the sequence can be driven without a WPF message loop. What these cannot prove is
/// that the keys reach the view model at all - that is what the palette capture in
/// <c>--screenshot</c> is for.
/// </remarks>
public sealed class CommandPaletteKeyboardTests
{
    private static CommandPaletteViewModel Palette() => new MainViewModel().CommandPalette;

    [Fact]
    public void OpeningWithNoQueryOffersSectionsAndSelectsTheFirst()
    {
        var palette = Palette();
        palette.Open();

        Assert.True(palette.IsOpen);
        Assert.NotEmpty(palette.Results);
        Assert.Same(palette.Results[0], palette.Selected);
        Assert.True(palette.Selected!.IsSection);
    }

    [Fact]
    public void OpeningTwiceClearsWhateverWasTypedBefore()
    {
        // Otherwise the palette reopens showing the last search, and Enter runs
        // something the user typed a minute ago.
        var palette = Palette();
        palette.Open();
        palette.Query = "wipe";

        palette.Open();

        Assert.Equal(string.Empty, palette.Query);
    }

    [Fact]
    public void ArrowingMovesTheHighlightAndOnlyOneRowHoldsIt()
    {
        var palette = Palette();
        palette.Open();

        var first = palette.Selected;
        palette.Move(1);

        Assert.NotSame(first, palette.Selected);
        Assert.False(first!.IsSelected);
        Assert.True(palette.Selected!.IsSelected);
        Assert.Single(palette.Results, r => r.IsSelected);
    }

    [Fact]
    public void ArrowingPastTheEndsWraps()
    {
        var palette = Palette();
        palette.Open();

        palette.Move(-1);
        Assert.Same(palette.Results[^1], palette.Selected);

        palette.Move(1);
        Assert.Same(palette.Results[0], palette.Selected);
    }

    [Fact]
    public void TypingNarrowsTheResultsAndReselects()
    {
        var palette = Palette();
        palette.Open();
        palette.Query = "wipe";

        Assert.NotEmpty(palette.Results);
        Assert.All(palette.Results, r =>
            Assert.True(
                r.Title.Contains("wipe", StringComparison.OrdinalIgnoreCase) ||
                r.Context.Contains("wipe", StringComparison.OrdinalIgnoreCase)));

        Assert.Same(palette.Results[0], palette.Selected);
    }

    [Fact]
    public void AQueryThatMatchesNothingLeavesNothingSelected()
    {
        // Enter must then do nothing rather than run whatever was highlighted before.
        var palette = Palette();
        palette.Open();
        palette.Query = "zzzzzzz";

        Assert.Empty(palette.Results);
        Assert.Null(palette.Selected);
    }

    [Fact]
    public void EnterOnASectionNavigatesAndCloses()
    {
        var shell = new MainViewModel();
        var palette = shell.CommandPalette;

        palette.Open();
        palette.Query = "Wipe";

        var target = palette.Selected;
        Assert.NotNull(target);

        palette.InvokeCommand.Execute(null);

        Assert.False(palette.IsOpen);
        Assert.Equal(target!.SectionKey, shell.SelectedSection!.Key);
    }

    [Fact]
    public void EnterWithNothingSelectedIsHarmless()
    {
        var shell = new MainViewModel();
        var palette = shell.CommandPalette;

        palette.Open();
        palette.Query = "zzzzzzz";

        var before = shell.SelectedSection;
        palette.InvokeCommand.Execute(null);

        Assert.Same(before, shell.SelectedSection);
    }

    [Fact]
    public void EveryActionPointsAtASectionThatExists()
    {
        // An action whose SectionKey is a typo navigates nowhere and then runs, so
        // the result lands on a screen the operator is not looking at.
        var shell = new MainViewModel();
        var palette = shell.CommandPalette;
        var keys = shell.Sections.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);

        palette.Open();

        foreach (var letter in "abcdefghijklmnopqrstuvwxyz")
        {
            palette.Query = letter.ToString();

            foreach (var entry in palette.Results)
                Assert.True(keys.Contains(entry.SectionKey), $"'{entry.Title}' -> '{entry.SectionKey}'");
        }
    }

    [Fact]
    public void ClosingLeavesTheShellWhereItWas()
    {
        var shell = new MainViewModel();
        var before = shell.SelectedSection;

        shell.CommandPalette.Open();
        shell.CommandPalette.CloseCommand.Execute(null);

        Assert.False(shell.CommandPalette.IsOpen);
        Assert.Same(before, shell.SelectedSection);
    }
}

/// <summary>
/// Rail badges, which turn the navigation into the summary.
/// </summary>
public sealed class RailBadgeTests
{
    private static NavSection Section() =>
        new("trash", "Recycle Bins", "Per-drive recycle bins", "T", "NavTrashHex", MainViewModel.GroupCleanup);

    [Fact]
    public void ASectionStartsWithNoBadge()
    {
        Assert.False(Section().HasBadge);
    }

    [Fact]
    public void SettingACountShowsIt()
    {
        var section = Section();
        section.Badge = "3";

        Assert.True(section.HasBadge);
    }

    [Fact]
    public void ClearingRemovesBothTheCountAndItsTone()
    {
        var section = Section();
        section.Badge = "9";
        section.BadgeTone = "warn";

        section.ClearBadge();

        Assert.False(section.HasBadge);
        Assert.Equal(string.Empty, section.BadgeTone);
    }
}
