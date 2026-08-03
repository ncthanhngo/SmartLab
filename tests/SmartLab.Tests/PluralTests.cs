using System.Text.RegularExpressions;
using SmartLab.Core.Text;
using Xunit;

namespace SmartLab.Tests;

/// <summary>
/// Counting in English rather than in shorthand.
/// </summary>
/// <remarks>
/// Every line the application shows used to hedge - "3 file(s) match", "1 entr(ies)
/// run at logon" - which is the program admitting it did not know the number when the
/// sentence was written, in front of a reader for whom it now does.
/// </remarks>
public sealed partial class PluralTests
{
    [Theory]
    [InlineData(0, "file", "0 files")]
    [InlineData(1, "file", "1 file")]
    [InlineData(2, "file", "2 files")]
    public void TheNumberDecidesTheNoun(int count, string noun, string expected) =>
        Assert.Equal(expected, Plural.Of(count, noun));

    [Theory]
    [InlineData("entry", "entries")]
    [InlineData("category", "categories")]
    [InlineData("anomaly", "anomalies")]
    public void ANounEndingInYTakesIes(string noun, string plural) =>
        Assert.Equal($"3 {plural}", Plural.Of(3, noun));

    /// <remarks>
    /// The endings that cannot take a bare -s without running two hisses together.
    /// "3 fixs" is the kind of thing that makes a tool look unfinished.
    /// </remarks>
    [Theory]
    [InlineData("fix", "fixes")]
    [InlineData("pass", "passes")]
    [InlineData("batch", "batches")]
    [InlineData("dish", "dishes")]
    public void ASibilantEndingTakesEs(string noun, string plural) =>
        Assert.Equal($"2 {plural}", Plural.Of(2, noun));

    /// <remarks>
    /// A file count can run to six figures, and 128035 is a number somebody has to
    /// stop and read digit by digit.
    /// </remarks>
    [Fact]
    public void LargeCountsAreGrouped() =>
        Assert.Equal("128,035 files", Plural.Of(128_035, "file"));

    /// <summary>
    /// The verb agrees too, because English puts the -s on the other word.
    /// </summary>
    /// <remarks>
    /// Counting correctly and then writing "1 package have a newer version" is the
    /// same fault the brackets were, moved one word to the right.
    /// </remarks>
    [Theory]
    [InlineData(1, "has")]
    [InlineData(0, "have")]
    [InlineData(4, "have")]
    public void TheVerbAgreesWithTheCount(int count, string expected) =>
        Assert.Equal(expected, Plural.Verb(count, "has", "have"));

    /// <remarks>
    /// A compound noun pluralises on its last word, which is what lets a caller write
    /// "1 deleted entry" and "5 deleted entries" from one call.
    /// </remarks>
    [Fact]
    public void ACompoundNounPluralisesOnItsLastWord()
    {
        Assert.Equal("1 deleted entry", Plural.Of(1, "deleted entry"));
        Assert.Equal("5 deleted entries", Plural.Of(5, "deleted entry"));
    }

    /// <summary>
    /// Nothing the application counts is still written with a bracketed plural.
    /// </summary>
    /// <remarks>
    /// Read out of the source rather than asserted line by line, because the failure
    /// this guards against is the next line somebody writes in the old habit. Comments
    /// are exempt: prose about code may say "(s)" without a user ever seeing it.
    /// </remarks>
    [Fact]
    public void NoUserFacingStringHedgesItsPlural()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(
            Path.Combine(FindRepoRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;

            var lineNumber = 0;

            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;

                var code = line.TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal)) continue;
                if (code.StartsWith('*')) continue;

                foreach (var quoted in Quoted().Matches(line).Cast<Match>())
                {
                    if (Hedged().IsMatch(quoted.Value))
                        offenders.Add($"{Path.GetFileName(file)}:{lineNumber} {quoted.Value}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [GeneratedRegex("\"[^\"]*\"")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"\w\((s|es|ies)\)")]
    private static partial Regex Hedged();

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SmartLab.sln")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
