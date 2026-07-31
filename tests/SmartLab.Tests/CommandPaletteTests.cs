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
        var section = CommandPaletteViewModel.Score(Section("Trash Bins"), "trash");
        var action = CommandPaletteViewModel.Score(Action("Trash something"), "trash");

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
        Assert.Equal(int.MinValue, CommandPaletteViewModel.Score(Action("Shred the queued files"), "outlook"));
    }

    [Fact]
    public void TheContextIsSearchableButRanksLast()
    {
        // Typing a section name should find its actions, and still put the section
        // itself above them.
        var byTitle = CommandPaletteViewModel.Score(Action("Empty the ticked Recycle Bins"), "empty");
        var byContext = CommandPaletteViewModel.Score(
            new PaletteEntry("Empty the ticked Recycle Bins", "Trash Bins", isSection: false), "Trash");

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
            CommandPaletteViewModel.Score(Section("Space Lens"), "space"),
            CommandPaletteViewModel.Score(Section("Space Lens"), "SPACE"));
    }

    [Fact]
    public void TheResultListStaysShortEnoughToReadWithoutScrolling()
    {
        // A palette that needs scrolling has stopped being faster than the rail.
        Assert.InRange(CommandPaletteViewModel.MaxResults, 5, 10);
    }
}

/// <summary>
/// Rail badges, which turn the navigation into the summary.
/// </summary>
public sealed class RailBadgeTests
{
    private static NavSection Section() =>
        new("trash", "Trash Bins", "Per-drive recycle bins", "T", "NavTrashHex", MainViewModel.GroupCleanup);

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
