using SmartLab.Core.Abstractions;
using SmartLab.Core.Model;
using SmartLab.Engine;
using SmartLab.Core.Text;

namespace SmartLab.Cli;

/// <summary>Console rendering for plans and execution reports.</summary>
public static class PlanRenderer
{
    private const int MaxListed = 50;

    public static void WritePlan(RecoveryPlan plan)
    {
        var v = plan.Volume;
        Console.WriteLine(
            $"Volume {v.Root}  {v.FileSystem ?? "?"}  " +
            $"{v.SizeBytes / 1024.0 / 1024 / 1024:F2} GB  ({v.DriveType})");
        Console.WriteLine();

        WriteThreats(plan);
        WriteAnomalies(plan);
        WriteDamaged(plan);
        WriteActions(plan);
    }

    private static void WriteThreats(RecoveryPlan plan)
    {
        if (plan.Threats.Count == 0) return;

        Console.WriteLine($"THREATS ({plan.Threats.Count})");
        foreach (var t in plan.Threats)
        {
            Console.WriteLine($"  [{t.Severity}] {t.Path.ForDisplay()}");
            Console.WriteLine($"      {t.SignatureId}: {t.Reason}");
        }
        Console.WriteLine();
    }

    private static void WriteAnomalies(RecoveryPlan plan)
    {
        if (plan.Anomalies.Count == 0) return;

        Console.WriteLine($"ANOMALIES ({plan.Anomalies.Count})");
        foreach (var a in plan.Anomalies.Take(MaxListed))
        {
            var shown = string.IsNullOrEmpty(a.VisibleName) ? a.Path.ForDisplay() : a.VisibleName;
            Console.WriteLine($"  [{a.Severity}] {a.Kind}  {shown}");
            Console.WriteLine($"      {a.Explanation}");
        }

        // Say what was withheld. A truncated list that does not admit it reads as
        // a complete one.
        if (plan.Anomalies.Count > MaxListed)
            Console.WriteLine($"  ... and {plan.Anomalies.Count - MaxListed} more (use --json for all)");

        Console.WriteLine();
    }

    private static void WriteDamaged(RecoveryPlan plan)
    {
        if (plan.Damaged.Count == 0) return;

        Console.WriteLine($"UNREADABLE ENTRIES ({plan.Damaged.Count})");
        foreach (var d in plan.Damaged.Take(20))
            Console.WriteLine($"  {d.Path.ForDisplay()}  (Win32 {d.Win32Error}) {d.Message}");

        if (plan.Damaged.Count > 20)
            Console.WriteLine($"  ... and {plan.Damaged.Count - 20} more");

        Console.WriteLine();
    }

    private static void WriteActions(RecoveryPlan plan)
    {
        Console.WriteLine($"PROPOSED ACTIONS ({plan.ProposedActions.Count}) - nothing has been executed");

        if (plan.ProposedActions.Count == 0)
        {
            Console.WriteLine("  (none)");
            return;
        }

        foreach (var action in plan.ProposedActions)
        {
            var mark = action.IsDestructive ? "!" : " ";
            Console.WriteLine($" {mark}[{action.Kind}] {action.Description}");
        }
    }

    public static void WriteExecutionReport(ExecutionReport report, bool dryRun)
    {
        Console.WriteLine();
        Console.WriteLine(dryRun
            ? "DRY RUN - no changes were made. Re-run with --execute to apply."
            : "RESULTS");

        foreach (var outcome in report.Outcomes)
        {
            var status = outcome.Result.Outcome switch
            {
                WriteOutcome.Succeeded => "ok  ",
                WriteOutcome.SkippedDryRun => "plan",
                _ => "FAIL",
            };

            Console.WriteLine($"  [{status}] {outcome.Action.Kind}: {outcome.Action.Description}");

            if (!string.IsNullOrEmpty(outcome.Note))
                Console.WriteLine($"         {outcome.Note}");

            if (outcome.Result.Outcome == WriteOutcome.Failed)
                Console.WriteLine($"         Win32 {outcome.Result.Win32Error}: {outcome.Result.Message}");

            if (outcome.Rescue is { } rescue)
                WriteRescueDetail(rescue);
        }

        Console.WriteLine();
        Console.WriteLine($"{report.Succeeded} succeeded, {report.Failed} failed");
    }

    private static void WriteRescueDetail(RescueReport rescue)
    {
        Console.WriteLine(
            $"         {Plural.Of(rescue.FilesCopied, "file")}, " +
            $"{rescue.BytesCopied / 1024.0 / 1024 / 1024:F3} GB, " +
            $"{Plural.Of(rescue.DirectoriesCreated, "dir")}");

        if (rescue.Renames.Count > 0)
            Console.WriteLine($"         {Plural.Of(rescue.Renames.Count, "name")} sanitised for the destination");

        if (rescue.Failures.Count == 0) return;

        Console.WriteLine($"         {Plural.Of(rescue.Failures.Count, "entry")} could not be copied:");
        foreach (var failure in rescue.Failures.Take(10))
            Console.WriteLine($"           {failure.Source} (Win32 {failure.Win32Error}) {failure.Message}");

        if (rescue.Failures.Count > 10)
            Console.WriteLine($"           ... and {rescue.Failures.Count - 10} more");
    }
}
