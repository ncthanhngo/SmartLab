using SmartLab.App;
using Xunit;

namespace SmartLab.Tests;

/// <summary>
/// The words on the buttons that act on a selection.
/// </summary>
/// <remarks>
/// Every one of them used to read "<c>verb</c> ticked" - Clean ticked, Apply ticked,
/// Empty ticked - which names the checkbox rather than the machine. Somebody deciding
/// whether to press one is asking what happens to their computer, and how much of it.
/// </remarks>
public sealed class ActionWordingTests
{
    [Fact]
    public void NothingTickedLeavesTheBareVerb() =>
        Assert.Equal("Empty", ActionWording.For("Empty", 0, "bin"));

    [Fact]
    public void OneTickedIsNotPluralised() =>
        Assert.Equal("Empty 1 bin", ActionWording.For("Empty", 1, "bin"));

    [Fact]
    public void TheCountAndTheNounSayHowMuchTheVerbCovers() =>
        Assert.Equal("Recycle 12 files", ActionWording.For("Recycle", 12, "file"));

    /// <remarks>
    /// No button uses such a noun today. The rule is here for the one that does next,
    /// because the way it fails without it is a button reading "categorys".
    /// </remarks>
    [Fact]
    public void ANounEndingInYPluralisesProperly() =>
        Assert.Equal("Clean 3 categories", ActionWording.For("Clean", 3, "category"));

    /// <remarks>
    /// The word "ticked" describes the list. These labels describe the act, which is
    /// what someone is deciding about.
    /// </remarks>
    /// <summary>
    /// No button anywhere in the application is labelled after the checkbox.
    /// </summary>
    /// <remarks>
    /// Read out of the XAML rather than asserted view model by view model, because the
    /// failure this guards against is a new section written in the old habit - and a
    /// test that only knows the sections that existed when it was written would not see
    /// it. Prose elsewhere may still say "ticked": explaining that a row is ticked is
    /// fair, naming a verb after it is not.
    /// </remarks>
    [Fact]
    public void NoButtonInTheApplicationIsLabelledAfterTheCheckbox()
    {
        var content = new System.Text.RegularExpressions.Regex(
            @"<Button\b[^>]*?Content=""(?<text>[^""]*)""",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(
            Path.Combine(FindRepoRoot(), "src", "SmartLab.App"), "*.xaml", SearchOption.AllDirectories))
        {
            foreach (System.Text.RegularExpressions.Match match in content.Matches(File.ReadAllText(file)))
            {
                var text = match.Groups["text"].Value;

                if (text.Contains("ticked", StringComparison.OrdinalIgnoreCase))
                    offenders.Add($"{Path.GetFileName(file)}: {text}");
            }
        }

        Assert.Empty(offenders);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SmartLab.sln")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    /// <summary>
    /// Every pair a button in this application actually shows.
    /// </summary>
    /// <remarks>
    /// Each noun names the thing the verb happens to - problems, programs, leftovers -
    /// rather than "items", which is true of anything on a screen and therefore tells
    /// somebody deciding whether to press nothing at all.
    /// </remarks>
    [Theory]
    [InlineData("Clean", "place")]
    [InlineData("Empty", "bin")]
    [InlineData("Recycle", "file")]
    [InlineData("Recover", "file")]
    [InlineData("Fix", "problem")]
    [InlineData("Turn off", "program")]
    [InlineData("Remove", "leftover")]
    [InlineData("Upgrade", "app")]
    [InlineData("Install", "driver")]
    public void NoLabelSaysTicked(string verb, string noun)
    {
        Assert.DoesNotContain("ticked", ActionWording.For(verb, 0, noun), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ticked", ActionWording.For(verb, 4, noun), StringComparison.OrdinalIgnoreCase);
    }
}
