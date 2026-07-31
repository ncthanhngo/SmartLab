using System.Globalization;
using SmartLab.App;
using SmartLab.App.Converters;
using SmartLab.Fat;
using Xunit;

namespace SmartLab.Tests;

/// <summary>
/// The two decisions that give the grouped lists their order and their headings.
/// </summary>
/// <remarks>
/// Both fail quietly. A wrong rank still produces a tidy grouped list, just one
/// that opens on the files that cannot be recovered; a wrong heading still reads
/// as a sentence, just one that tells the operator the opposite of the truth
/// about whether removal needs administrator.
/// </remarks>
public sealed class InsetGroupingTests
{
    private static DeletedEntryViewModel Entry(RecoveryConfidence confidence) =>
        new(new RawEntry("\\file.bin", "file.bin", IsDirectory: false, IsDeleted: true,
                FirstCluster: 5, Length: 1024),
            confidence,
            summary: "test");

    [Fact]
    public void RecoverableVerdicts_RankAboveTheRest()
    {
        // Likely and Superseded are the two CanRecover admits, so they have to be the
        // two the operator meets first.
        var likely = Entry(RecoveryConfidence.Likely).ConfidenceRank;
        var superseded = Entry(RecoveryConfidence.Superseded).ConfidenceRank;
        var partial = Entry(RecoveryConfidence.Partial).ConfidenceRank;
        var overwritten = Entry(RecoveryConfidence.Overwritten).ConfidenceRank;
        var unknown = Entry(RecoveryConfidence.Unknown).ConfidenceRank;

        Assert.True(likely < superseded);
        Assert.True(superseded < partial);
        Assert.True(partial < overwritten);
        Assert.True(overwritten < unknown);
    }

    [Fact]
    public void EveryVerdict_RanksDistinctly()
    {
        var ranks = Enum.GetValues<RecoveryConfidence>()
            .Select(c => Entry(c).ConfidenceRank)
            .ToArray();

        Assert.Equal(ranks.Length, ranks.Distinct().Count());
    }

    [Fact]
    public void RankOrder_MatchesWhatCanActuallyBeRecovered()
    {
        // Guards the pairing rather than the numbers: if CanRecover ever admits
        // another verdict, that verdict has to be promoted too or the list will
        // bury the recoverable files under the ones that are gone.
        var recoverable = Enum.GetValues<RecoveryConfidence>()
            .Select(Entry)
            .Where(e => e.CanRecover)
            .Select(e => e.ConfidenceRank)
            .ToArray();

        var unrecoverable = Enum.GetValues<RecoveryConfidence>()
            .Select(Entry)
            .Where(e => !e.CanRecover)
            .Select(e => e.ConfidenceRank)
            .ToArray();

        Assert.NotEmpty(recoverable);
        Assert.True(recoverable.Max() < unrecoverable.Min());
    }

    [Theory]
    [InlineData(true, InstallScopeConverter.PerUser)]
    [InlineData(false, InstallScopeConverter.MachineWide)]
    public void ScopeHeading_FollowsWhoTheProgramWasInstalledFor(bool perUser, string expected)
    {
        var converted = new InstallScopeConverter()
            .Convert(perUser, typeof(string), null, CultureInfo.InvariantCulture);

        Assert.Equal(expected, converted);
    }

    [Fact]
    public void MachineWideHeading_SaysItNeedsAdministrator()
    {
        // The app runs as the invoking user, so this is the heading's whole job.
        Assert.Contains("administrator", InstallScopeConverter.MachineWide, StringComparison.OrdinalIgnoreCase);
    }
}
