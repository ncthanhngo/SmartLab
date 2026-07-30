using UsbDoctor.Core.Model;
using UsbDoctor.Core.Naming;
using UsbDoctor.Core.Paths;

namespace UsbDoctor.Engine;

/// <summary>
/// Turns scan findings into proposed actions.
/// </summary>
/// <remarks>
/// Nothing here executes. The planner is a pure function of the findings, which
/// makes the whole decision layer testable without a disk.
/// </remarks>
public static class RecoveryPlanner
{
    public static IReadOnlyList<RecoveryAction> Plan(
        VolumeInfo volume,
        IReadOnlyList<Anomaly> anomalies,
        IReadOnlyList<ThreatMatch> threats,
        ExtendedPath? rescueDestination = null)
    {
        var actions = new List<RecoveryAction>();
        var renamed = new HashSet<string>(StringComparer.Ordinal);

        // Rescue first when asked for. It is ordered ahead of every repair by the
        // executor anyway, but emitting it first also puts it at the top of the
        // proposal the operator reads, which is the order the work should happen
        // in: get the data off before touching the volume.
        if (rescueDestination is { } destination)
        {
            actions.Add(new RecoveryAction(
                RecoveryActionKind.RescueCopy,
                ExtendedPath.From(volume.Root),
                $"Copy everything readable from {volume.Root} to " +
                $"'{destination.ForDisplay()}' before any repair")
            {
                Destination = destination,
                Severity = Severity.Info,
                TargetIsDirectory = true,
                EstimatedBytes = volume.SizeBytes - volume.FreeBytes,
            });
        }

        foreach (var anomaly in anomalies)
        {
            switch (anomaly.Kind)
            {
                case AnomalyKind.InvisibleName:
                case AnomalyKind.TrimmableName:
                case AnomalyKind.NonPrintableName:
                case AnomalyKind.BidiOverride:
                {
                    // One proposal per path, even when several rules fired on it.
                    if (!renamed.Add(anomaly.Path.Value)) break;

                    // A pathological folder sitting directly at the volume root is
                    // the worm's staging folder: everything the user had was moved
                    // into it. Putting the contents back where they were is the
                    // repair; renaming the folder would only make them reachable.
                    if (anomaly.Path.Parent is { IsDriveRoot: true })
                    {
                        actions.Add(new RecoveryAction(
                            RecoveryActionKind.RestoreToRoot,
                            anomaly.Path,
                            $"Move everything inside '{anomaly.VisibleName}' back to {volume.Root} " +
                            "and remove the empty folder, restoring the original layout")
                        {
                            Destination = ExtendedPath.From(volume.Root),
                            Severity = anomaly.Severity,
                            TargetIsDirectory = true,
                        });
                        break;
                    }

                    var safe = ProposeSafeName(anomaly.Path);
                    if (safe is null) break;

                    actions.Add(new RecoveryAction(
                        RecoveryActionKind.RenameToSafeName,
                        anomaly.Path,
                        $"Rename '{anomaly.VisibleName}' to '{safe.Value.Name}' so the contents " +
                        "become reachable with ordinary paths")
                    {
                        Destination = safe,
                        Severity = anomaly.Severity,
                    });
                    break;
                }

                case AnomalyKind.HiddenSystemUserData:
                    actions.Add(new RecoveryAction(
                        RecoveryActionKind.ClearAttributes,
                        anomaly.Path,
                        // Name the target. Without it, several distinct actions render
                        // as identical lines and the operator cannot tell what they are
                        // approving.
                        $"Clear Hidden+System on '{Display(anomaly)}' so it is visible in Explorer")
                    {
                        Severity = anomaly.Severity,
                    });
                    break;
            }
        }

        foreach (var threat in threats)
        {
            // Report-only signatures contribute a finding and nothing else. They
            // exist so a weak indicator can be surfaced without proposing that the
            // user's file be taken away.
            if (threat.Action == ThreatAction.Report) continue;

            var kind = threat.Action == ThreatAction.Delete
                ? RecoveryActionKind.DeleteThreat
                : RecoveryActionKind.Quarantine;

            var verb = kind == RecoveryActionKind.DeleteThreat ? "Delete" : "Quarantine";

            actions.Add(new RecoveryAction(
                kind,
                threat.Path,
                $"{verb} {(threat.IsDirectory ? "folder" : "file")} " +
                $"'{threat.Path.ForDisplay()}' — matched '{threat.SignatureId}': {threat.Reason}")
            {
                Severity = threat.Severity,
                TargetIsDirectory = threat.IsDirectory,
            });
        }

        return actions;
    }

    /// <summary>
    /// Renders a finding's path with its final component escaped, so an invisible
    /// name is legible in the proposal the operator approves.
    /// </summary>
    private static string Display(Anomaly anomaly)
    {
        var path = anomaly.Path.ForDisplay();
        if (string.IsNullOrEmpty(anomaly.VisibleName)) return path;

        var separator = path.LastIndexOf('\\');
        return separator < 0 ? anomaly.VisibleName : path[..(separator + 1)] + anomaly.VisibleName;
    }

    /// <summary>
    /// Proposes an ASCII sibling name for a pathological directory.
    /// </summary>
    /// <remarks>
    /// Renaming in place is preferred over moving the contents out. A rename is a
    /// single directory-entry write; moving the children is thousands of
    /// operations against a filesystem already known to be damaged, and is exactly
    /// what split the dataset during the incident this tool comes from.
    /// </remarks>
    private static ExtendedPath? ProposeSafeName(ExtendedPath path)
    {
        var parent = path.Parent;
        if (parent is null) return null;

        var original = path.Name;

        // Decide on the ORIGINAL name, not on the sanitiser's output. Feeding a
        // blank name through NameSanitizer yields "_blank", which is technically
        // valid and therefore passes every subsequent check — leaving the operator
        // staring at a proposal to rename their data folder to "_blank". The
        // sanitiser's job is to make a name legal; naming it usefully is this
        // method's job.
        var candidate = SuspiciousNameRules.IsEffectivelyBlank(original)
            ? "RECOVERED_DATA"
            : Sanitize(original);

        return parent.Value.Child(candidate);

        static string Sanitize(string original)
        {
            var safe = new NameSanitizer().Sanitize(original);
            return safe.WasChanged ? safe.Safe : original + "_recovered";
        }
    }
}
