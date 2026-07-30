using UsbDoctor.Core.Abstractions;
using UsbDoctor.Core.Model;
using UsbDoctor.Core.Naming;
using UsbDoctor.Core.Paths;

namespace UsbDoctor.Engine;

public sealed record ExecutionOptions
{
    /// <summary>Where quarantined files are moved to. Must not be on the volume being repaired.</summary>
    public required string QuarantineRoot { get; init; }

    /// <summary>Where a <see cref="RecoveryActionKind.RescueCopy"/> writes to.</summary>
    public ExtendedPath? RescueDestination { get; init; }

    /// <summary>Stop at the first failure instead of continuing with the rest.</summary>
    public bool StopOnFirstFailure { get; init; }
}

public sealed record ActionOutcome(RecoveryAction Action, WriteResult Result, string? Note = null)
{
    /// <summary>Populated for a rescue copy, which reports per-file detail.</summary>
    public RescueReport? Rescue { get; init; }
}

public sealed record ExecutionReport(IReadOnlyList<ActionOutcome> Outcomes)
{
    public int Succeeded => Outcomes.Count(o => o.Result.Succeeded);
    public int Failed => Outcomes.Count(o => !o.Result.Succeeded);
    public bool AllSucceeded => Failed == 0;
}

public sealed record ExecutionProgress(int Completed, int Total, string Description);

/// <summary>
/// Applies an <see cref="ApprovedPlan"/>. The only component that changes a disk.
/// </summary>
public sealed class PlanExecutor(IWriteGate gate, IJournal journal, RescueCopier? rescueCopier = null)
{
    private static readonly EntryAttributes HidingAttributes =
        EntryAttributes.Hidden | EntryAttributes.System | EntryAttributes.ReadOnly;

    public async Task<ExecutionReport> ApplyAsync(
        ApprovedPlan plan,
        ExecutionOptions options,
        IProgress<ExecutionProgress>? progress = null,
        CancellationToken ct = default)
    {
        var ordered = Order(plan.Actions);
        var outcomes = new List<ActionOutcome>(ordered.Count);

        await journal.AppendAsync(
            JournalRecord.For("plan-begin", plan.Source.Volume.Root, true,
                $"{ordered.Count} approved action(s), dryRun={gate.DryRun}"), ct).ConfigureAwait(false);

        var completed = 0;
        foreach (var action in ordered)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ExecutionProgress(completed, ordered.Count, action.Description));

            var outcome = await ExecuteAsync(action, options, ct).ConfigureAwait(false);
            outcomes.Add(outcome);
            completed++;

            if (!outcome.Result.Succeeded && options.StopOnFirstFailure) break;
        }

        var report = new ExecutionReport(outcomes);

        await journal.AppendAsync(
            JournalRecord.For("plan-end", plan.Source.Volume.Root, report.AllSucceeded,
                $"{report.Succeeded} succeeded, {report.Failed} failed"), ct).ConfigureAwait(false);

        return report;
    }

    /// <summary>
    /// Orders actions so that earlier ones cannot invalidate the paths of later ones.
    /// </summary>
    /// <remarks>
    /// The rescue copy runs before anything else: get the data off the device
    /// while it is still readable, then repair. Threats are neutralised next, so a
    /// failure later in the run still leaves the malware dealt with. Renames go
    /// last and deepest-first, because renaming a parent rewrites the path of
    /// every descendant and would invalidate any action still queued against one.
    /// </remarks>
    private static List<RecoveryAction> Order(IReadOnlyList<RecoveryAction> actions) =>
        [.. actions
            .OrderBy(a => a.Kind switch
            {
                RecoveryActionKind.RescueCopy => 0,
                RecoveryActionKind.Quarantine => 1,
                RecoveryActionKind.DeleteThreat => 2,
                RecoveryActionKind.ClearAttributes => 3,
                RecoveryActionKind.RenameToSafeName => 4,
                _ => 5,
            })
            .ThenByDescending(a => Depth(a.Target))];

    private static int Depth(ExtendedPath path) => path.Value.Count(c => c == '\\');

    private async Task<ActionOutcome> ExecuteAsync(
        RecoveryAction action, ExecutionOptions options, CancellationToken ct)
    {
        switch (action.Kind)
        {
            case RecoveryActionKind.ClearAttributes:
            {
                var result = await gate.ClearAttributesAsync(action.Target, HidingAttributes, ct)
                    .ConfigureAwait(false);
                return new ActionOutcome(action, result);
            }

            case RecoveryActionKind.RenameToSafeName:
            {
                if (action.Destination is not { } destination)
                {
                    return new ActionOutcome(action,
                        WriteResult.Failed("rename", action.Target, 0, "No destination on the action."),
                        "planner produced a rename without a target name");
                }

                var result = await gate.RenameAsync(action.Target, destination, ct).ConfigureAwait(false);
                return new ActionOutcome(action, result);
            }

            case RecoveryActionKind.Quarantine:
                return await QuarantineAsync(action, options, ct).ConfigureAwait(false);

            case RecoveryActionKind.DeleteThreat:
            {
                var result = await gate.DeleteFileAsync(action.Target, ct).ConfigureAwait(false);
                return new ActionOutcome(action, result);
            }

            case RecoveryActionKind.RescueCopy:
                return await RescueAsync(action, options, ct).ConfigureAwait(false);

            default:
                return new ActionOutcome(action,
                    WriteResult.Failed(action.Kind.ToString(), action.Target, 0, "Not implemented."),
                    $"{action.Kind} is not implemented yet");
        }
    }

    private async Task<ActionOutcome> RescueAsync(
        RecoveryAction action, ExecutionOptions options, CancellationToken ct)
    {
        if (rescueCopier is null)
        {
            return new ActionOutcome(action,
                WriteResult.Failed("rescue", action.Target, 0, "No rescue copier was supplied."),
                "construct PlanExecutor with a RescueCopier to enable this action");
        }

        var destination = options.RescueDestination ?? action.Destination;
        if (destination is not { } target)
        {
            return new ActionOutcome(action,
                WriteResult.Failed("rescue", action.Target, 0, "No rescue destination configured."),
                "set ExecutionOptions.RescueDestination");
        }

        var report = await rescueCopier.CopyTreeAsync(action.Target, target, null, ct).ConfigureAwait(false);

        // A rescue that copied nothing at all is a failure; one that copied most
        // of a damaged volume is a success with a list of casualties, which is the
        // best outcome physically available.
        var result = report.FilesCopied > 0 || !report.AnyFailures
            ? WriteResult.Ok("rescue", action.Target)
            : WriteResult.Failed("rescue", action.Target, 0, "Nothing could be copied.");

        var note = report.AnyFailures
            ? $"{report.FilesCopied} file(s) copied, {report.Failures.Count} unreadable"
            : $"{report.FilesCopied} file(s) copied";

        return new ActionOutcome(action, result, note) { Rescue = report };
    }

    /// <summary>
    /// Removes a malicious directory once its contents have been dealt with.
    /// </summary>
    /// <remarks>
    /// Ordering does the real work here: quarantine actions run deepest-first, so
    /// by the time the folder itself comes up, each file inside it has already been
    /// copied to the store and deleted individually. What remains is an empty
    /// directory, and the attribute pair that kept it hidden has to come off before
    /// Windows will remove it. If anything is still inside, the delete fails and
    /// says so rather than forcing a recursive delete on a damaged volume.
    /// </remarks>
    private async Task<ActionOutcome> QuarantineDirectoryAsync(RecoveryAction action, CancellationToken ct)
    {
        var cleared = await gate.ClearAttributesAsync(action.Target, HidingAttributes, ct)
            .ConfigureAwait(false);

        if (!cleared.Succeeded)
            return new ActionOutcome(action, cleared, "could not clear attributes before removal");

        var deleted = await gate.DeleteEmptyDirectoryAsync(action.Target, ct).ConfigureAwait(false);

        return deleted.Succeeded
            ? new ActionOutcome(action, deleted, "removed after its contents were quarantined")
            : new ActionOutcome(action, deleted,
                "directory is not empty — approve the actions for the files inside it as well");
    }

    /// <summary>
    /// Copies a suspected file to the quarantine store, then removes the original.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Copy-then-delete, never move: the quarantine store is on a different volume
    /// from the device being repaired, and the original must survive until the copy
    /// is known to have landed.
    /// </para>
    /// <para>
    /// Directories go to <see cref="QuarantineDirectoryAsync"/> instead: the
    /// planner emits an action for the malicious folder and for every file inside
    /// it, and handling the files individually is what actually removes the
    /// payload.
    /// </para>
    /// </remarks>
    private async Task<ActionOutcome> QuarantineAsync(
        RecoveryAction action, ExecutionOptions options, CancellationToken ct)
    {
        if (action.TargetIsDirectory)
            return await QuarantineDirectoryAsync(action, ct).ConfigureAwait(false);

        var name = action.Target.Name;

        var sanitizer = new NameSanitizer();
        var safe = sanitizer.Sanitize(name);

        // The suffix keeps a quarantined payload from being launched by a
        // double-click in the store.
        var destination = ExtendedPath.From(options.QuarantineRoot).Child(safe.Safe + ".quarantined");

        var createdRoot = await gate.CreateDirectoryAsync(
            ExtendedPath.From(options.QuarantineRoot), ct).ConfigureAwait(false);

        if (!createdRoot.Succeeded)
            return new ActionOutcome(action, createdRoot, "could not create the quarantine store");

        var copied = await gate.CopyFileAsync(action.Target, destination, null, ct).ConfigureAwait(false);
        if (!copied.Succeeded)
            return new ActionOutcome(action, copied, "original left in place because the copy failed");

        var deleted = await gate.DeleteFileAsync(action.Target, ct).ConfigureAwait(false);

        var note = safe.WasChanged ? $"stored as '{safe.Safe}.quarantined' (original name preserved here)" : null;

        return deleted.Succeeded
            ? new ActionOutcome(action, deleted, note)
            : new ActionOutcome(action, deleted, "copied to quarantine but the original could not be removed");
    }
}
