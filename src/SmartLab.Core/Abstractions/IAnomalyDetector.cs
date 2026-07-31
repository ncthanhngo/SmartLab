using SmartLab.Core.Model;

namespace SmartLab.Core.Abstractions;

/// <summary>
/// Inspects a single entry and reports anything abnormal about it.
/// </summary>
/// <remarks>
/// Detectors are registered independently and composed by the scanner, so
/// supporting a newly observed hiding technique means adding one class rather
/// than editing a growing conditional inside the scan loop.
/// </remarks>
public interface IAnomalyDetector
{
    /// <summary>Stable identifier, used in reports and to disable a detector by name.</summary>
    string Id { get; }

    IEnumerable<Anomaly> Inspect(FileEntry entry, ScanContext context);
}

/// <param name="Volume">The volume being scanned, for size-plausibility checks.</param>
/// <param name="IsVolumeRoot">True while inspecting entries directly under the root.</param>
public sealed record ScanContext(VolumeInfo Volume, bool IsVolumeRoot);
