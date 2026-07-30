using UsbDoctor.Core.Model;
using UsbDoctor.Core.Naming;
using UsbDoctor.Engine;
using UsbDoctor.Engine.Journal;
using UsbDoctor.Fat;
using UsbDoctor.Win32.Io;

namespace UsbDoctor.Cli;

internal static class Program
{
    private const int ExitClean = 0;
    private const int ExitError = 1;
    private const int ExitUsage = 2;
    private const int ExitFindings = 3;
    private const int ExitCancelled = 130;

    private static async Task<int> Main(string[] args)
    {
        var options = CliOptions.Parse(args, out var parseError);

        if (options.Command == CliCommand.None)
        {
            if (parseError is not null) Console.Error.WriteLine(parseError);
            Console.Error.WriteLine(CliOptions.Usage);
            return parseError is null ? ExitUsage : ExitError;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            return options.Command switch
            {
                CliCommand.Scan => await RunScanAsync(options, cts.Token).ConfigureAwait(false),
                CliCommand.Apply => await RunApplyAsync(options, cts.Token).ConfigureAwait(false),
                CliCommand.Raw => RunRaw(options),
                _ => ExitUsage,
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Cancelled.");
            return ExitCancelled;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitError;
        }
    }

    private static async Task<int> RunScanAsync(CliOptions options, CancellationToken ct)
    {
        var reader = new Win32VolumeReader();
        var plan = await ScanRunner.RunAsync(reader, options, options.Json, ct).ConfigureAwait(false);

        if (options.Json) ScanRunner.WriteJson(plan);
        else PlanRenderer.WritePlan(plan);

        return HasFindings(plan) ? ExitFindings : ExitClean;
    }

    private static async Task<int> RunApplyAsync(CliOptions options, CancellationToken ct)
    {
        var reader = new Win32VolumeReader();
        var plan = await ScanRunner.RunAsync(reader, options, quiet: false, ct).ConfigureAwait(false);

        PlanRenderer.WritePlan(plan);

        if (plan.ProposedActions.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("Nothing to do.");
            return ExitClean;
        }

        var needsQuarantine = plan.ProposedActions.Any(a => a.Kind == RecoveryActionKind.Quarantine);
        if (needsQuarantine && string.IsNullOrWhiteSpace(options.QuarantineRoot))
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                "The plan quarantines files, so --quarantine <dir> is required. " +
                "It must be on a different volume.");
            return ExitError;
        }

        if (options.Execute && !options.AssumeYes && !Confirm(plan))
        {
            Console.WriteLine("Aborted.");
            return ExitCancelled;
        }

        await using var journal = new JsonlJournal(JournalPath(options.DriveLetter));
        var gate = new Win32WriteGate(journal, dryRun: !options.Execute);
        var executor = new PlanExecutor(gate, journal, new RescueCopier(reader, gate, journal));

        var executionOptions = new ExecutionOptions
        {
            // Only ever read when a quarantine action runs, which the check above
            // has already gated on.
            QuarantineRoot = options.QuarantineRoot ?? string.Empty,
            RescueDestination = options.RescueDestination,
            StopOnFirstFailure = options.StopOnFirstFailure,
        };

        // Every proposed action is approved here. Selecting a subset is what the
        // UI is for; the CLI's safety mechanism is that --execute is opt-in.
        var approved = plan.Approve(plan.ProposedActions);

        var report = await executor.ApplyAsync(approved, executionOptions, null, ct)
            .ConfigureAwait(false);

        PlanRenderer.WriteExecutionReport(report, dryRun: !options.Execute);
        Console.WriteLine($"Journal: {JournalPath(options.DriveLetter)}");

        return report.AllSucceeded ? ExitClean : ExitFindings;
    }

    private static bool Confirm(RecoveryPlan plan)
    {
        var destructive = plan.ProposedActions.Count(a => a.IsDestructive);

        Console.WriteLine();
        Console.Write(
            $"Apply {plan.ProposedActions.Count} action(s) to {plan.Volume.Root}" +
            (destructive > 0 ? $", {destructive} irreversible" : string.Empty) +
            "? [y/N] ");

        var answer = Console.ReadLine();

        // Null means stdin is closed - a non-interactive run without --yes. Treat
        // silence as refusal rather than consent.
        return answer is not null &&
               answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the volume's FAT structures directly, bypassing the mounted
    /// filesystem. Strictly read-only.
    /// </summary>
    private static int RunRaw(CliOptions options)
    {
        if (!RawVolume.CanOpen(options.DriveLetter, out var reason))
        {
            Console.Error.WriteLine($"Cannot open {options.DriveLetter}: for raw reading: {reason}");
            Console.Error.WriteLine("Reading a fixed disk this way requires Administrator.");
            return ExitError;
        }

        using var stream = RawVolume.Open(options.DriveLetter);

        if (!RawFileSystem.TryOpen(stream, out var fileSystem, out var error))
        {
            Console.Error.WriteLine($"No supported filesystem found: {error}");
            return ExitError;
        }

        Console.WriteLine(fileSystem!.Describe());
        Console.WriteLine();

        var listed = 0;
        var recovered = 0;
        var skipped = 0;
        var sanitizer = new NameSanitizer();

        // First pass prints live entries and collects what is needed to judge the
        // deleted ones: the starting clusters that live files still claim. Only the
        // deleted entries are held, so memory stays bounded on a large volume.
        var deletedEntries = new List<RawEntry>();
        var liveFirstClusters = new HashSet<uint>();

        foreach (var item in fileSystem.EnumerateTree())
        {
            if (item.IsDeleted)
            {
                deletedEntries.Add(item);
                continue;
            }

            if (!item.IsDirectory && item.FirstCluster >= 2)
                liveFirstClusters.Add(item.FirstCluster);

            if (options.DeletedOnly) continue;

            var mark = item.IsDirectory ? "DIR" : "   ";
            Console.WriteLine($"  [{mark}] {item.Path}  (cluster {item.FirstCluster}, {item.Length} bytes)");
            listed++;
        }

        foreach (var item in deletedEntries)
        {
            Console.WriteLine($"  [DEL] {item.Path}  (cluster {item.FirstCluster}, {item.Length} bytes)");
            listed++;

            if (item is not { IsDirectory: false, Length: > 0, FirstCluster: >= 2 }) continue;

            var assessment = fileSystem.AssessRange(item.FirstCluster, item.Length);
            var confidence = DeletedEntryAssessor.Refine(
                assessment.Confidence, item.FirstCluster, liveFirstClusters);

            Console.WriteLine($"        {confidence}: {assessment.SummaryFor(confidence)}");

            if (options.RecoverTo is not { } destination) continue;

            // Carving a fully reallocated range returns another file's bytes under
            // this file's name, which is worse than returning nothing. A superseded
            // range is different: the data is intact, just renamed.
            if (confidence == RecoveryConfidence.Overwritten && !options.RecoverAnyway)
            {
                skipped++;
                Console.WriteLine("        skipped - use --recover-anyway to carve it regardless");
                continue;
            }

            if (TryRecover(fileSystem, item, destination, sanitizer, out var written))
            {
                Console.WriteLine($"        -> recovered {written} byte(s)");
                recovered++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{listed} entr(ies) listed, {deletedEntries.Count} deleted entr(ies) found.");

        if (options.RecoverTo is not null)
        {
            Console.WriteLine($"{recovered} file(s) carved into {options.RecoverTo}");

            if (skipped > 0)
                Console.WriteLine($"{skipped} skipped because their clusters were fully reallocated.");

            Console.WriteLine(
                "Recovery assumes the data was not fragmented. 'Likely' means no cluster " +
                "has been reallocated since the delete, not that the file is verified - " +
                "check every result before trusting it.");
        }

        return deletedEntries.Count > 0 ? ExitFindings : ExitClean;
    }

    private static bool TryRecover(
        IRawFileSystem fileSystem, RawEntry entry, string destination,
        NameSanitizer sanitizer, out int written)
    {
        written = 0;

        try
        {
            Directory.CreateDirectory(destination);

            var data = fileSystem.ReadContiguous(entry.FirstCluster, entry.Length);
            if (data.Length == 0) return false;

            // Deleted names collide readily - FAT32 loses the first character of
            // every one - so the cluster number keeps them distinct, and the file
            // is never overwritten if it somehow already exists.
            var safe = sanitizer.Sanitize($"{entry.FirstCluster}_{entry.Name}").Safe;
            var path = Path.Combine(destination, safe);

            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            file.Write(data);

            written = data.Length;
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"        recovery failed: {ex.Message}");
            return false;
        }
    }

    private static bool HasFindings(RecoveryPlan plan) =>
        plan.Anomalies.Count > 0 || plan.Threats.Count > 0;

    private static string JournalPath(char driveLetter) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UsbDoctor", $"journal-{driveLetter}.jsonl");
}
