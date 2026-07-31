using SmartLab.Core.Abstractions;
using SmartLab.Core.Model;
using SmartLab.Core.Paths;
using SmartLab.Signatures;

namespace SmartLab.Engine;

public sealed record ScanOptions
{
    /// <summary>Depth limit, or <c>null</c> for the whole tree.</summary>
    public int? MaxDepth { get; init; }

    /// <summary>
    /// Skip hashing files larger than this. Hashing is the dominant cost of a
    /// scan and payloads in this malware family are consistently under a megabyte.
    /// </summary>
    public long MaxHashBytes { get; init; } = 32L * 1024 * 1024;

    /// <summary>
    /// When set, the plan opens with a rescue copy to this destination. Leave null
    /// for pure triage.
    /// </summary>
    public ExtendedPath? RescueDestination { get; init; }

    public static ScanOptions Default { get; } = new();
}

/// <param name="CurrentPath">The entry being inspected, for display.</param>
/// <remarks>
/// A struct because it is reported thousands of times during a scan; a class here
/// would put one short-lived allocation per report on the heap for no benefit.
/// </remarks>
public readonly record struct ScanProgress(int DirectoriesVisited, int EntriesSeen, string CurrentPath);

/// <summary>
/// Walks a volume and produces a <see cref="RecoveryPlan"/>.
/// </summary>
/// <remarks>
/// Strictly read-only: the scanner holds no <see cref="IWriteGate"/> and cannot
/// modify anything, by construction rather than by convention.
/// </remarks>
public sealed class VolumeScanner(
    IVolumeReader reader,
    IReadOnlyList<IAnomalyDetector> detectors,
    SignatureMatcher matcher)
{
    /// <summary>
    /// How many entries pass between progress reports.
    /// </summary>
    /// <remarks>
    /// Small enough that the reported path changes many times a second, which is
    /// what makes a long scan look like work rather than a hang; large enough that
    /// the reporting itself stays free.
    /// </remarks>
    private const int ReportEveryEntries = 12;

    public async Task<RecoveryPlan> ScanAsync(
        char driveLetter,
        ScanOptions? options = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        options ??= ScanOptions.Default;

        var volume = reader.GetVolume(driveLetter)
            ?? throw new InvalidOperationException($"Drive {driveLetter}: is not mounted.");

        var anomalies = new List<Anomaly>();
        var threats = new List<ThreatMatch>();
        var damaged = new List<DamagedEntry>();

        var root = ExtendedPath.From(volume.Root);
        var queue = new Queue<(ExtendedPath Path, int Depth)>();
        queue.Enqueue((root, 0));

        var directories = 0;
        var entries = 0;
        var sinceReport = 0;

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var (current, depth) = queue.Dequeue();
            directories++;
            progress?.Report(new ScanProgress(directories, entries, current.ForDisplay()));

            var isRoot = depth == 0;
            var context = new ScanContext(volume, isRoot);

            await foreach (var item in reader.EnumerateAsync(current, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                entries++;

                switch (item)
                {
                    case EnumEntry.Damaged bad:
                        damaged.Add(new DamagedEntry(
                            bad.RawName is null ? bad.Parent : bad.Parent.Child(bad.RawName),
                            bad.Win32Error,
                            bad.Message));

                        anomalies.Add(new Anomaly(
                            AnomalyKind.UnreadableEntry,
                            Severity.Medium,
                            bad.Parent,
                            $"Entry could not be read (Win32 {bad.Win32Error}): {bad.Message}"));
                        break;

                    case EnumEntry.Ok ok:
                        // Sampled rather than reported for every entry. A caller
                        // that writes to a console or marshals to a UI thread would
                        // otherwise spend longer rendering the walk than walking it,
                        // and at these rates nobody can read individual paths anyway.
                        if (++sinceReport >= ReportEveryEntries)
                        {
                            sinceReport = 0;
                            progress?.Report(
                                new ScanProgress(directories, entries, ok.Entry.Path.ForDisplay()));
                        }

                        Inspect(ok.Entry, context, options, anomalies, threats);

                        if (ok.Entry.IsDirectory &&
                            !ok.Entry.Attributes.HasFlag(EntryAttributes.ReparsePoint) &&
                            (options.MaxDepth is null || depth < options.MaxDepth))
                        {
                            queue.Enqueue((ok.Entry.Path, depth + 1));
                        }
                        break;
                }
            }
        }

        var actions = RecoveryPlanner.Plan(volume, anomalies, threats, options.RescueDestination);

        return new RecoveryPlan(volume, anomalies, threats, damaged, actions);
    }

    private void Inspect(
        FileEntry entry,
        ScanContext context,
        ScanOptions options,
        List<Anomaly> anomalies,
        List<ThreatMatch> threats)
    {
        foreach (var detector in detectors)
        {
            // A faulty detector must not abort the scan of a volume that may be
            // the operator's only copy of the data.
            try
            {
                anomalies.AddRange(detector.Inspect(entry, context));
            }
            catch (Exception ex)
            {
                anomalies.Add(new Anomaly(
                    AnomalyKind.UnreadableEntry, Severity.Low, entry.Path,
                    $"Detector '{detector.Id}' failed: {ex.Message}"));
            }
        }

        Func<Stream>? open = null;
        if (!entry.IsDirectory && entry.Length > 0 && entry.Length <= options.MaxHashBytes)
            open = () => reader.OpenRead(entry.Path);

        try
        {
            threats.AddRange(matcher.Match(entry, context.IsVolumeRoot, open));
        }
        catch (Exception ex)
        {
            anomalies.Add(new Anomaly(
                AnomalyKind.UnreadableEntry, Severity.Low, entry.Path,
                $"Signature matching failed: {ex.Message}"));
        }
    }
}
