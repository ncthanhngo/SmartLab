using System.Text.Json;
using SmartLab.App;
using SmartLab.Core.Abstractions;
using SmartLab.Engine.Journal;
using Xunit;

namespace SmartLab.Tests;

/// <summary>
/// Reading back what the write gate recorded.
/// </summary>
/// <remarks>
/// The journal was written from the first release and shown nowhere, which is how
/// three separate repairs of one infected stick each recorded <c>0 succeeded, 3
/// failed</c> while the window said nothing was wrong. What is covered here is the
/// grouping that turns a flat file into the runs an operator remembers making, and
/// the tolerance that stops one truncated line discarding the file it is at the end
/// of.
/// </remarks>
public sealed class JournalReaderTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), $"smartlab-journal-{Guid.NewGuid():N}");

    public JournalReaderTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    private string Write(string name, params JournalRecord[] records)
    {
        var path = Path.Combine(_folder, name);

        File.WriteAllLines(path, records.Select(r => JsonSerializer.Serialize(r)));

        return path;
    }

    private static JournalRecord Record(string kind, string target, bool ok, string? detail = null) =>
        new(DateTimeOffset.UtcNow, kind, target, ok, detail);

    [Fact]
    public void RecordsAreGroupedIntoTheRunsThatProducedThem()
    {
        var path = Write("journal-E.jsonl",
            Record("plan-begin", @"E:\", true, "3 approved action(s), dryRun=False"),
            Record("create-directory", @"C:\q", true),
            Record("quarantine", @"E:\bad.exe", true),
            Record("plan-end", @"E:\", true, "2 succeeded, 0 failed"),
            Record("plan-begin", @"E:\", true, "1 approved action(s), dryRun=False"),
            Record("delete", @"E:\other.exe", true),
            Record("plan-end", @"E:\", true, "1 succeeded, 0 failed"));

        var runs = JournalReader.Runs(JournalReader.Read(path));

        Assert.Equal(2, runs.Count);

        // Newest first, which is the order somebody looking for what just happened
        // reads in.
        Assert.Equal("1 succeeded, 0 failed", runs[0].Outcome);
        Assert.Equal("2 succeeded, 0 failed", runs[1].Outcome);
    }

    /// <remarks>
    /// The exact shape of the failure that went unseen: every action refused, the
    /// plan ending with none succeeded, and nothing about it anywhere on screen.
    /// </remarks>
    [Fact]
    public void ARunThatFailedEveryWriteIsCountedAsSuch()
    {
        var path = Write("journal-E.jsonl",
            Record("plan-begin", @"E:\", true, "3 approved action(s), dryRun=False"),
            Record("create-directory", @"C:\Users\x\SmartLab\quarantine", false,
                "The system cannot find the path specified."),
            Record("create-directory", @"C:\Users\x\SmartLab\quarantine", false,
                "The system cannot find the path specified."),
            Record("plan-end", @"E:\", false, "0 succeeded, 3 failed"));

        var run = Assert.Single(JournalReader.Runs(JournalReader.Read(path)));

        Assert.Equal(2, run.Failures);
        Assert.False(run.Succeeded);
        Assert.False(run.Unfinished);

        var shown = new JournalRunViewModel(run);

        Assert.Equal("alert", shown.Tone);
        Assert.Equal("0 succeeded, 3 failed", shown.Outcome);
    }

    [Fact]
    public void ARunWithNoEndIsReportedAsUnfinishedRatherThanAsSucceeding()
    {
        // The app was closed, or the stick was pulled. Either way the run has no
        // verdict, and inventing one would be the worst thing this screen could do.
        var path = Write("journal-E.jsonl",
            Record("plan-begin", @"E:\", true, "2 approved action(s), dryRun=False"),
            Record("rename", @"E:\thing", true));

        var run = Assert.Single(JournalReader.Runs(JournalReader.Read(path)));

        Assert.True(run.Unfinished);
        Assert.Equal("warning", new JournalRunViewModel(run).Tone);
    }

    [Fact]
    public void ATruncatedLastLineDoesNotDiscardTheLinesAboveIt()
    {
        // A journal is flushed per line, so a device that disappears mid-write leaves
        // exactly this. Refusing the file would throw away the record of everything
        // that had already happened to it.
        var path = Write("journal-E.jsonl",
            Record("plan-begin", @"E:\", true, "1 approved action(s)"),
            Record("delete", @"E:\bad.exe", true));

        File.AppendAllText(path, "{\"TimestampUtc\":\"2026-08-01T0");

        var records = JournalReader.Read(path);

        Assert.Equal(2, records.Count);
    }

    [Fact]
    public void WritesOutsideAnyRunAreStillShown()
    {
        // A write nobody can account for is the one most worth seeing, so it gets a
        // run of its own rather than being dropped for having no plan-begin.
        var path = Write("journal-E.jsonl", Record("delete", @"E:\orphan", true));

        var run = Assert.Single(JournalReader.Runs(JournalReader.Read(path)));

        Assert.True(run.Unfinished);
        Assert.Single(run.Records);
    }

    [Fact]
    public void AMissingFolderIsNotAFailure()
    {
        Assert.Empty(JournalReader.Files(Path.Combine(_folder, "not-there")));
    }

    /// <remarks>
    /// The headline leads with failures when there are any: a screen that opens on
    /// "12 runs" while three of them failed has buried the only line worth reading.
    /// </remarks>
    [Theory]
    [InlineData(0, 0, "neutral")]
    [InlineData(4, 0, "good")]
    [InlineData(4, 3, "warning")]
    public void TheHeadlineLeadsWithWhatWentWrong(int runs, int failures, string tone)
    {
        var (headline, _, actual) = HistoryViewModel.Summarise(runs, failures);

        Assert.Equal(tone, actual);
        Assert.NotEmpty(headline);

        if (failures > 0) Assert.Contains(failures.ToString(), headline);
    }
}
