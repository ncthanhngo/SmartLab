using SmartLab.Core.Abstractions;
using SmartLab.Core.Model;
using SmartLab.Core.Paths;
using SmartLab.Win32.Io;
using Xunit;

namespace SmartLab.Tests;

/// <summary>
/// The one component allowed to change a filesystem, against a real one.
/// </summary>
/// <remarks>
/// Against a temp folder rather than a fake, because what is covered here is a Win32
/// behaviour a fake would have had to imitate correctly to be worth anything - and
/// imitating it correctly means already knowing the thing these tests exist to pin.
/// </remarks>
public sealed class WriteGateTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"smartlab-gate-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* a leftover temp folder is not worth failing a test run over */ }
    }

    /// <summary>
    /// The bug that left a worm on a stick through every repair anyone ran.
    /// </summary>
    /// <remarks>
    /// Quarantine writes to <c>%USERPROFILE%\SmartLab\quarantine</c>, and on a machine
    /// that has never had a SmartLab folder that is two levels below anything that
    /// exists. CreateDirectoryW makes one level and, under the <c>\\?\</c> prefix, will
    /// not invent the parent - so every quarantine failed with "the system cannot find
    /// the path specified", the plan reported 0 of 3 succeeded, and the next scan found
    /// the same two threats. The scanner had been right every time.
    /// </remarks>
    [Fact]
    public async Task ADirectoryTwoLevelsBelowAnythingThatExistsIsStillCreated()
    {
        var gate = new Win32WriteGate(new NullJournal(), dryRun: false);
        var target = Path.Combine(_root, "SmartLab", "quarantine");

        Assert.False(Directory.Exists(_root));

        var result = await gate.CreateDirectoryAsync(ExtendedPath.From(target), default);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public async Task CreatingADirectoryThatIsAlreadyThereSucceeds()
    {
        // The caller asked for the directory to exist. It does.
        var gate = new Win32WriteGate(new NullJournal(), dryRun: false);
        var target = Path.Combine(_root, "already");

        Directory.CreateDirectory(target);

        var result = await gate.CreateDirectoryAsync(ExtendedPath.From(target), default);

        Assert.True(result.Succeeded, result.Message);
    }

    [Fact]
    public async Task EveryLevelItCreatesIsWrittenToTheJournal()
    {
        // The journal is what an operator reads to find out what this app did to their
        // machine, and a directory it brought into being is one of those things.
        var journal = new RecordingJournal();
        var gate = new Win32WriteGate(journal, dryRun: false);

        await gate.CreateDirectoryAsync(
            ExtendedPath.From(Path.Combine(_root, "one", "two")), default);

        Assert.Equal(3, journal.Records.Count(r => r.Kind == "create-directory"));
    }

    [Fact]
    public async Task ADryRunCreatesNothing()
    {
        var gate = new Win32WriteGate(new NullJournal(), dryRun: true);
        var target = Path.Combine(_root, "SmartLab", "quarantine");

        var result = await gate.CreateDirectoryAsync(ExtendedPath.From(target), default);

        Assert.Equal(WriteOutcome.SkippedDryRun, result.Outcome);
        Assert.False(Directory.Exists(_root));
    }

    private sealed class NullJournal : IJournal
    {
        public Task AppendAsync(JournalRecord record, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingJournal : IJournal
    {
        public List<JournalRecord> Records { get; } = [];

        public Task AppendAsync(JournalRecord record, CancellationToken ct)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }
    }
}
