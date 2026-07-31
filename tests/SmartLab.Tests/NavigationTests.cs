using System.Text.RegularExpressions;
using SmartLab.App;
using SmartLab.App.Views;
using Xunit;

namespace SmartLab.Tests;

/// <summary>
/// The rail's contract with everything hanging off it.
/// </summary>
/// <remarks>
/// A section is declared in three places that cannot see each other: the rail lists
/// it, a palette gives it a colour, and a resource dictionary draws its stage. Each
/// of those failing alone is silent - a grey glyph, a blank stage, a section that
/// never lights up - so the seams are what these tests cover.
/// </remarks>
public sealed class NavigationTests
{
    private static readonly MainViewModel Model = new();

    private static IReadOnlyList<NavSection> Sections => Model.Sections;

    /// <summary>Palette files are copied beside the test binary by the csproj.</summary>
    private static string PaletteText(string theme) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Themes", $"Palette.{theme}.xaml"));

    [Fact]
    public void EverySectionKeyIsUnique()
    {
        // Stages are chosen by key. Two sections sharing one would both resolve to the
        // same stage, and only the first would ever be reachable.
        var keys = Sections.Select(s => s.Key).ToArray();

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EverySectionSitsInAKnownGroupOrNone()
    {
        foreach (var section in Sections)
        {
            var known = section.Group.Length == 0 || MainViewModel.Groups.Contains(section.Group);

            Assert.True(known, $"'{section.Title}' is in unknown group '{section.Group}'");
        }
    }

    [Fact]
    public void GroupsAreContiguous()
    {
        // A collection view places a group where its first member appears, so a section
        // declared away from its group silently drags the whole heading with it.
        var groups = Sections.Select(s => s.Group).ToArray();
        var seen = new List<string>();

        for (var i = 0; i < groups.Length; i++)
        {
            if (i > 0 && groups[i] == groups[i - 1]) continue;

            Assert.DoesNotContain(groups[i], seen);
            seen.Add(groups[i]);
        }
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void EverySectionHasItsHueInBothPalettes(string theme)
    {
        // A missing key does not throw. The rail falls back to grey, which reads as a
        // section nobody finished rather than as a fault.
        var palette = PaletteText(theme);

        foreach (var section in Sections)
            Assert.Contains($"x:Key=\"{section.AccentKey}\"", palette, StringComparison.Ordinal);
    }

    [Fact]
    public void EverySectionDeclaresAGlyphAndASubtitle()
    {
        foreach (var section in Sections)
        {
            Assert.False(string.IsNullOrWhiteSpace(section.Glyph), $"'{section.Title}' has no glyph");
            Assert.False(string.IsNullOrWhiteSpace(section.Subtitle), $"'{section.Title}' has no subtitle");
        }
    }

    [Fact]
    public void GlyphsAreDistinct()
    {
        // Two sections wearing the same glyph is the one rail fault a screenshot does
        // not make obvious, because both cells still look deliberate.
        var glyphs = Sections.Select(s => s.Glyph).ToArray();

        Assert.Equal(glyphs.Length, glyphs.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void SmartScanOpensTheAppAndAboutClosesIt()
    {
        Assert.Equal("smart", Sections[0].Key);
        Assert.Equal("about", Sections[^1].Key);
    }

    [Fact]
    public void TemplateKeysAreNamespaced()
    {
        // The stage dictionaries live in application resources beside every style, so
        // an un-prefixed key like "repair" would be one collision away from a section
        // rendering somebody's brush.
        Assert.StartsWith(SectionTemplateSelector.Prefix, SectionTemplateSelector.ResourceKeyFor("repair"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void EveryDeclaredStageBelongsToADeclaredSection()
    {
        // Catches the reverse of a missing stage: a template left behind after its
        // section was renamed, which nothing else would ever mention again.
        var views = Path.Combine(FindRepoRoot(), "src", "SmartLab.App", "Views");
        var keys = Sections.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var file in Directory.GetFiles(views, "Sections.*.xaml"))
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(file), @"x:Key=""section-([a-z]+)"""))
            {
                var key = match.Groups[1].Value;
                if (key == "missing") continue;

                Assert.True(keys.Contains(key), $"{Path.GetFileName(file)} draws unknown section '{key}'");
            }
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SmartLab.sln")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
