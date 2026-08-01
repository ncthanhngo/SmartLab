using System.Text.Json;
using SmartLab.Core.Abstractions;

namespace SmartLab.Engine.Journal;

/// <summary>One run: everything between a plan-begin and the plan-end that closed it.</summary>
/// <param name="Volume">What the run was against, as the journal recorded it.</param>
/// <param name="Outcome">The plan-end's detail, or null for a run that never ended.</param>
public sealed record JournalRun(
    DateTimeOffset StartedUtc,
    string Volume,
    IReadOnlyList<JournalRecord> Records,
    string? Outcome,
    bool Succeeded)
{
    /// <summary>
    /// Writes that failed. The plan's own end marker is not one of them.
    /// </summary>
    /// <remarks>
    /// A run of three refused writes ends with a plan-end that also reports failure,
    /// and counting that would make every failed run report one more failure than it
    /// had. The markers bracket the run; they are not things that happened to the
    /// machine.
    /// </remarks>
    public int Failures =>
        Records.Count(r => !r.Success && r.Kind is not ("plan-begin" or "plan-end"));

    /// <summary>True for a run that has no end: the app was closed, or it crashed.</summary>
    public bool Unfinished => Outcome is null;
}

/// <summary>
/// Reads back what the write gate recorded.
/// </summary>
/// <remarks>
/// <para>
/// The journal has always been written and never shown. That is how three separate
/// repairs of one infected stick could report nothing on screen while every one of
/// them recorded <c>0 succeeded, 3 failed</c> on disk - the operator had no way to
/// learn that the quarantine folder could not be created, and the scan looked wrong
/// when the scan had been right every time.
/// </para>
/// <para>
/// Tolerant on purpose. A journal is flushed per line so a stick that disappears
/// mid-write leaves a partial last line, and refusing to read the file because of it
/// would throw away the ninety-nine complete lines above it.
/// </para>
/// </remarks>
public static class JournalReader
{
    /// <summary>Where the app keeps its journals.</summary>
    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartLab");

    /// <summary>Every journal file, newest first.</summary>
    public static IReadOnlyList<string> Files(string? folder = null)
    {
        folder ??= DefaultFolder;

        if (!Directory.Exists(folder)) return [];

        try
        {
            return Directory.GetFiles(folder, "journal-*.jsonl")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Reads one file's records, skipping any line that will not parse.</summary>
    public static IReadOnlyList<JournalRecord> Read(string path)
    {
        var records = new List<JournalRecord>();

        IEnumerable<string> lines;

        try
        {
            // Shared read: the app may be writing to this file while it is shown.
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).ToArray();
        }
        catch
        {
            return records;
        }

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            try
            {
                if (JsonSerializer.Deserialize<JournalRecord>(trimmed) is { } record)
                    records.Add(record);
            }
            catch (JsonException)
            {
                // A half-written last line, which is exactly what a journal flushed
                // per line leaves behind when the device goes away mid-run.
            }
        }

        return records;
    }

    /// <summary>
    /// Groups records into the runs that produced them, newest first.
    /// </summary>
    /// <remarks>
    /// A run is what an operator remembers doing - "I pressed Apply on the stick" -
    /// so it is the unit worth showing. Records outside any run still appear, in a run
    /// of their own with no outcome, because a write nobody can account for is the one
    /// most worth seeing.
    /// </remarks>
    public static IReadOnlyList<JournalRun> Runs(IReadOnlyList<JournalRecord> records)
    {
        var runs = new List<JournalRun>();

        var current = new List<JournalRecord>();
        JournalRecord? began = null;

        void Close(JournalRecord? end)
        {
            if (current.Count == 0 && began is null) return;

            var first = began ?? current[0];

            runs.Add(new JournalRun(
                first.TimestampUtc,
                first.Target,
                current.ToArray(),
                end?.Detail,
                end?.Success ?? false));

            current = [];
            began = null;
        }

        foreach (var record in records)
        {
            switch (record.Kind)
            {
                case "plan-begin":
                    Close(null);          // an earlier run that never ended
                    began = record;
                    current.Add(record);
                    break;

                case "plan-end":
                    current.Add(record);
                    Close(record);
                    break;

                default:
                    current.Add(record);
                    break;
            }
        }

        Close(null);

        runs.Reverse();
        return runs;
    }
}
