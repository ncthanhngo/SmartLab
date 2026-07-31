using SmartLab.Core.Abstractions;
using SmartLab.Core.Model;
using SmartLab.Core.Naming;

namespace SmartLab.Engine.Detectors;

/// <summary>
/// Flags user data carrying Hidden+System, and sizes the volume cannot hold.
/// </summary>
public sealed class HiddenDataDetector : IAnomalyDetector
{
    public string Id => "hidden-user-data";

    /// <summary>
    /// Names Windows and macOS legitimately create with Hidden+System. These are
    /// expected on a stick that has been mounted on both, and flagging them would
    /// bury the one entry that actually matters.
    /// </summary>
    private static readonly HashSet<string> KnownSystemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System Volume Information",
        "$RECYCLE.BIN",
        "RECYCLER",
        ".Spotlight-V100",
        ".fseventsd",
        ".Trashes",
        ".TemporaryItems",
        ".DS_Store",
        "found.000",
    };

    public IEnumerable<Anomaly> Inspect(FileEntry entry, ScanContext context)
    {
        // Only at the volume root. A worm hides relocated user data there, where
        // the owner would look for it. Application installers routinely mark
        // folders deep inside their own tree Hidden+System - scanning a stick with
        // an installer on it produced six such warnings from one driver utility,
        // and noise at that rate trains the operator to ignore the report. An
        // invisible or unaddressable *name* is still flagged at any depth by
        // NameAnomalyDetector, which is the stronger signal anyway.
        if (context.IsVolumeRoot && entry.IsDirectory && entry.IsHidden && entry.IsSystem &&
            !KnownSystemNames.Contains(entry.Name))
        {
            yield return new Anomaly(
                AnomalyKind.HiddenSystemUserData,
                Severity.High,
                entry.Path,
                "Directory at the volume root carries Hidden+System but is not a name Windows " +
                "or macOS creates. Applying both attributes is how a worm keeps relocated user " +
                "data out of sight, since Explorer hides System items even when 'show hidden " +
                "files' is enabled.")
            { VisibleName = SuspiciousNameRules.Describe(entry.Name) };
        }

        // A single entry claiming more than the volume can physically hold means
        // the directory record is corrupt. On the source volume, one folder
        // reported 138 GB on a 14 GB stick.
        if (!entry.IsDirectory && entry.Length > context.Volume.SizeBytes && context.Volume.SizeBytes > 0)
        {
            yield return new Anomaly(
                AnomalyKind.ImpossibleSize,
                Severity.Medium,
                entry.Path,
                $"Reported size ({entry.Length:N0} bytes) exceeds the volume capacity " +
                $"({context.Volume.SizeBytes:N0} bytes). The directory entry is corrupt; " +
                "the size cannot be trusted and the content may not be recoverable.")
            { VisibleName = SuspiciousNameRules.Describe(entry.Name) };
        }
    }
}
