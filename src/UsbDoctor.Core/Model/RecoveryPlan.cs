using UsbDoctor.Core.Paths;

namespace UsbDoctor.Core.Model;

public enum RecoveryActionKind
{
    /// <summary>Clear Hidden/System/ReadOnly from user data.</summary>
    ClearAttributes,
    /// <summary>Rename a pathological directory name to a safe ASCII one.</summary>
    RenameToSafeName,
    /// <summary>Copy data off the damaged volume to a rescue destination.</summary>
    RescueCopy,
    /// <summary>Move a suspected malicious file to the quarantine store.</summary>
    Quarantine,
    /// <summary>Delete a file confirmed malicious.</summary>
    DeleteThreat,
}

/// <summary>
/// One proposed change. Actions are produced by the planner and are inert until
/// an <see cref="ApprovedPlan"/> hands them to the executor.
/// </summary>
public sealed record RecoveryAction(
    RecoveryActionKind Kind,
    ExtendedPath Target,
    string Description)
{
    public ExtendedPath? Destination { get; init; }
    public Severity Severity { get; init; } = Severity.Info;

    /// <summary>Whether <see cref="Target"/> is a directory, as observed during the scan.</summary>
    public bool TargetIsDirectory { get; init; }

    /// <summary>
    /// True when the action cannot be undone. The UI must render these
    /// distinctly and leave them unchecked by default.
    /// </summary>
    public bool IsDestructive =>
        Kind is RecoveryActionKind.DeleteThreat;

    /// <summary>Estimated bytes moved, for progress reporting.</summary>
    public long EstimatedBytes { get; init; }
}

/// <summary>
/// The result of a scan: everything observed, plus what the tool proposes to do.
/// </summary>
/// <remarks>
/// Producing a plan is strictly read-only. This separation is the central safety
/// property of the tool — during the incident this project came from, a move was
/// issued before the volume was understood, and it silently degraded into a
/// recursive copy+delete that split a dataset across two locations. A plan the
/// operator reads before anything executes makes that class of mistake visible
/// in advance.
/// </remarks>
public sealed record RecoveryPlan(
    VolumeInfo Volume,
    IReadOnlyList<Anomaly> Anomalies,
    IReadOnlyList<ThreatMatch> Threats,
    IReadOnlyList<DamagedEntry> Damaged,
    IReadOnlyList<RecoveryAction> ProposedActions)
{
    public DateTimeOffset ScannedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public bool HasThreats => Threats.Count > 0;

    /// <summary>Selects a subset of proposed actions for execution.</summary>
    public ApprovedPlan Approve(IEnumerable<RecoveryAction> selected)
    {
        var chosen = selected.ToArray();

        foreach (var action in chosen)
        {
            if (!ProposedActions.Contains(action))
            {
                throw new InvalidOperationException(
                    $"Action '{action.Description}' is not part of this plan. " +
                    "The executor only ever runs actions produced by a scan.");
            }
        }

        return new ApprovedPlan(this, chosen);
    }
}

/// <summary>A plan an operator has explicitly signed off on.</summary>
public sealed record ApprovedPlan(RecoveryPlan Source, IReadOnlyList<RecoveryAction> Actions);
