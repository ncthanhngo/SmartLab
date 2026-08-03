using SmartLab.Core.Model;
using SmartLab.Core.Naming;
using SmartLab.Engine;
using SmartLab.Engine.Journal;
using SmartLab.Fat;
using SmartLab.Maintenance;
using SmartLab.Win32.Io;
using SmartLab.Core.Text;

namespace SmartLab.Cli;

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
                CliCommand.Uninstall => RunUninstallReport(),
                CliCommand.Clean => RunCleanReport(),
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
        var executor = new PlanExecutor(gate, journal, new RescueCopier(reader, gate, journal), reader);

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

        if (!options.Execute) return report.AllSucceeded ? ExitClean : ExitFindings;

        // Re-scan so the run ends with evidence rather than an assumption. "The
        // actions succeeded" and "the volume is clean" are different claims.
        Console.WriteLine();
        Console.WriteLine("Verifying...");

        var after = await ScanRunner.RunAsync(reader, options, quiet: false, ct).ConfigureAwait(false);

        Console.WriteLine(
            $"After repair: {Plural.Of(after.Threats.Count, "threat")}, " +
            $"{Plural.Of(after.Anomalies.Count, "anomaly")}.");

        return report.AllSucceeded && !HasFindings(after) ? ExitClean : ExitFindings;
    }

    private static bool Confirm(RecoveryPlan plan)
    {
        var destructive = plan.ProposedActions.Count(a => a.IsDestructive);

        Console.WriteLine();
        Console.Write(
            $"Apply {Plural.Of(plan.ProposedActions.Count, "action")} to {plan.Volume.Root}" +
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
                Console.WriteLine($"        -> recovered {Plural.Of(written, "byte")}");
                recovered++;
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            $"{Plural.Of(listed, "entry")} listed, " +
            $"{deletedEntries.Count} deleted {Plural.Word(deletedEntries.Count, "entry")} found.");

        if (options.RecoverTo is not null)
        {
            Console.WriteLine($"{Plural.Of(recovered, "file")} carved into {options.RecoverTo}");

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

    /// <summary>
    /// Reports what Smart Lab has left behind and what programs are installed.
    /// </summary>
    /// <remarks>
    /// Read-only on purpose. Removal stays in the app, where the operator can see
    /// the sizes and untick their own rescued data before anything goes. A headless
    /// switch that deletes gigabytes on one command line is not a convenience.
    /// </remarks>
    private static int RunUninstallReport()
    {
        var probe = new Win32TraceProbe();
        var paths = UninstallPaths.ForCurrentUser(AppContext.BaseDirectory);

        Console.WriteLine("USB DOCTOR'S OWN TRACES");

        var traces = new SelfTraceScanner(probe, paths).Scan();

        if (traces.Count == 0)
        {
            Console.WriteLine("  (none - nothing has been left on this machine)");
        }
        else
        {
            foreach (var trace in traces)
            {
                var tag = trace.IsUserData ? "YOUR DATA" : "app state";
                Console.WriteLine($"  [{trace.Kind,-13}] {trace.SizeText,-9} {tag,-9} {trace.Location}");
            }

            var userData = traces.Where(t => t.IsUserData).Sum(t => t.SizeBytes);
            if (userData > 0)
            {
                Console.WriteLine();
                Console.WriteLine(
                    $"  {userData / 1024.0 / 1024 / 1024:F2} GB of that is data rescued off drives. " +
                    "It is never removed unless explicitly ticked in the app.");
            }
        }

        Console.WriteLine();
        Console.WriteLine("INSTALLED PROGRAMS");

        var programs = new InstalledProgramScanner().Scan();
        var orphans = programs.Count(p => !p.HasUninstaller);

        foreach (var program in programs.Take(15))
        {
            var bits = program.Is64Bit ? "x64" : "x86";
            var scope = program.IsPerUser ? "user" : "machine";
            Console.WriteLine(
                $"  {program.DisplayName,-44} {program.Version,-14} {program.SizeText,-9} {bits} {scope}");
        }

        if (programs.Count > 15)
            Console.WriteLine($"  ... and {programs.Count - 15} more");

        Console.WriteLine();
        Console.WriteLine(
            $"{Plural.Of(programs.Count, "program")}; {orphans} registered no uninstaller. " +
            "Windows components and updates are excluded.");

        return ExitClean;
    }

    /// <summary>
    /// Measures each junk category. Read-only; cleaning is in the app.
    /// </summary>
    /// <remarks>
    /// The default column shows what would be ticked in the app rather than being
    /// decoration: a report that lists every category with a size, without saying
    /// which ones the tool would actually act on, invites the reader to add up the
    /// wrong number.
    /// </remarks>
    private static int RunCleanReport()
    {
        var probe = new Win32TraceProbe();
        var categories = JunkCatalogue.ForCurrentUser();
        var findings = new JunkScanner(probe).Scan(categories);

        Console.WriteLine("RECLAIMABLE SPACE");
        Console.WriteLine();

        foreach (var finding in findings)
        {
            var ticked = finding.Category.EnabledByDefault ? "[x]" : "[ ]";
            var admin = finding.Category.NeedsElevation ? " admin" : string.Empty;

            Console.WriteLine(
                $"  {ticked} {finding.Category.Name,-28} {finding.SizeText,10} " +
                $"{finding.Files,8:N0} files{admin}");

            if (finding.Category.Caution is { } caution)
                Console.WriteLine($"        ! {caution}");
        }

        var defaultTotal = findings.Where(f => f.Category.EnabledByDefault).Sum(f => f.Bytes);
        var everything = findings.Sum(f => f.Bytes);

        Console.WriteLine();
        Console.WriteLine($"Ticked by default: {defaultTotal / 1024.0 / 1024:N0} MB");
        Console.WriteLine($"Every category:    {everything / 1024.0 / 1024:N0} MB");

        ReportRecycleBins();
        ReportMailCache();

        Console.WriteLine();
        Console.WriteLine("Read-only. Cleaning is in the app, where each category can be reviewed first.");

        return ExitClean;
    }

    /// <summary>
    /// The bins, listed but never totalled into the reclaimable figure above.
    /// </summary>
    /// <remarks>
    /// Kept out of that sum on purpose. The bin is where deleted files are recovered
    /// from, so counting it as space this tool would reclaim would put the one number
    /// a reader takes away at odds with what the app would actually do.
    /// </remarks>
    private static void ReportRecycleBins()
    {
        var bins = RecycleBin.Enumerate();
        if (bins.Count == 0) return;

        Console.WriteLine();
        Console.WriteLine("RECYCLE BINS  (never ticked - this is where deleted files are recovered from)");
        Console.WriteLine();

        foreach (var bin in bins)
        {
            var removable = bin.IsRemovable ? " removable" : string.Empty;

            Console.WriteLine(
                $"  [ ] {bin.Root,-6} {bin.Label ?? "(no label)",-20} {bin.SizeText,10} " +
                $"{bin.Items,8:N0} items{removable}");
        }
    }

    private static void ReportMailCache()
    {
        var cached = OutlookCache.Scan();
        if (cached.Count == 0) return;

        var bytes = cached.Sum(a => a.SizeBytes);

        Console.WriteLine();
        Console.WriteLine("OUTLOOK ATTACHMENT CACHE  (copies made when an attachment was opened)");
        Console.WriteLine();
        Console.WriteLine(
            $"  {cached.Count,8:N0} {Plural.Word(cached.Count, "file"),-5}  {bytes / 1024.0 / 1024,10:N1} MB");
        Console.WriteLine("  Mailbox files (.ost, .pst) are never counted or listed.");
    }

    private static bool HasFindings(RecoveryPlan plan) =>
        plan.Anomalies.Count > 0 || plan.Threats.Count > 0;

    private static string JournalPath(char driveLetter) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SmartLab", $"journal-{driveLetter}.jsonl");
}
