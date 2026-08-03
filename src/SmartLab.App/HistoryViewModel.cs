using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartLab.Core.Abstractions;
using SmartLab.Core.Paths;
using SmartLab.Engine.Journal;
using SmartLab.Win32.Io;
using SmartLab.Core.Text;

namespace SmartLab.App;

/// <summary>One recorded write, with the tone its outcome earned.</summary>
public sealed class JournalLineViewModel(JournalRecord record)
{
    public string Time => record.TimestampUtc.ToLocalTime().ToString("HH:mm:ss");

    public string Kind => record.Kind;

    public string Target => record.Target;

    /// <summary>Why it failed, or what it did. Empty when there is nothing to add.</summary>
    public string Detail => record.Detail ?? string.Empty;

    public bool HasDetail => !string.IsNullOrWhiteSpace(record.Detail);

    /// <summary>The name a file had before it was sanitised for its destination.</summary>
    public string Original => record.OriginalName ?? string.Empty;

    public bool HasOriginal => !string.IsNullOrWhiteSpace(record.OriginalName);

    public string Tone => record.Success ? "good" : "alert";
}

/// <summary>One run of a plan, as the journal recorded it.</summary>
public sealed class JournalRunViewModel(JournalRun run)
{
    public JournalRun Run { get; } = run;


    public string Started => Run.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string Volume => Run.Volume;

    /// <summary>
    /// What the run amounted to, in the words the journal itself used.
    /// </summary>
    /// <remarks>
    /// The plan-end's own detail is preferred over anything recomputed here: it is
    /// what the executor concluded at the time, and a summary that disagrees with the
    /// record it summarises is worse than no summary.
    /// </remarks>
    public string Outcome => Run.Outcome
        ?? (Run.Unfinished ? "never finished - the app closed or stopped mid-run" : "no outcome recorded");

    public int Writes => Run.Records.Count(r => r.Kind is not ("plan-begin" or "plan-end"));

    public int Failures => Run.Failures;

    public string Tone => Run.Failures > 0 ? "alert" : Run.Unfinished ? "warning" : "good";

    public string Detail =>
        $"{Plural.Of(Writes, "write")}, {Failures} failed" + (Run.Unfinished ? ", unfinished" : string.Empty);

    public IReadOnlyList<JournalLineViewModel> Lines { get; } =
        run.Records.Select(r => new JournalLineViewModel(r)).ToArray();

    /// <summary>
    /// What this run moved into quarantine, as pairs of where it was and where it is.
    /// </summary>
    /// <remarks>
    /// Read off the journal rather than off the quarantine folder, because the folder
    /// holds sanitised names and nothing that says where each came from. The record
    /// does: a quarantine is a copy whose detail is the destination, followed by a
    /// delete of the original.
    /// </remarks>
    public IReadOnlyList<(string Original, string Stored)> Quarantined =>
        Run.Records
            .Where(r => r.Kind == "copy" && r.Success && r.Detail is { Length: > 0 })
            .Select(r => (Original: r.Target, Stored: DestinationOf(r.Detail!)))
            .Where(p => p.Stored.EndsWith(".quarantined", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    /// <summary>True when there is something here that can be put back.</summary>
    public bool CanRestore => Quarantined.Any(p => File.Exists(p.Stored));

    /// <summary>The destination out of a journal detail, which reads "-&gt; path".</summary>
    private static string DestinationOf(string detail)
    {
        var arrow = detail.IndexOf("-> ", StringComparison.Ordinal);
        if (arrow < 0) return string.Empty;

        var rest = detail[(arrow + 3)..].Trim();

        // The note the gate appends, when there is one, follows the path in brackets.
        var bracket = rest.IndexOf(" (", StringComparison.Ordinal);

        return bracket < 0 ? rest : rest[..bracket];
    }
}

/// <summary>
/// What this app has actually done to this machine.
/// </summary>
/// <remarks>
/// <para>
/// Every mutating call has always gone through one write gate and been journalled,
/// and none of it was ever on screen. That is how three separate repairs of one
/// infected stick could each record <c>0 succeeded, 3 failed</c> while the window
/// said nothing was wrong: the quarantine folder could not be created, every action
/// stopped on the first, and the operator was left believing the scanner was at
/// fault. The scanner had been right every time.
/// </para>
/// <para>
/// Read-only, and it fills itself in when the section opens: reading files this app
/// wrote changes nothing, and a screen whose only content is a button that fills it
/// in has asked the operator to do the one thing it could have done itself.
/// </para>
/// </remarks>
public sealed partial class HistoryViewModel : ObservableObject
{
    public ObservableCollection<JournalRunViewModel> Runs { get; } = [];

    [ObservableProperty] private JournalRunViewModel? _selectedRun;

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string _status =
        "Every write this app makes is recorded. This is the record.";

    [ObservableProperty] private int _runCount;
    [ObservableProperty] private int _failureCount;

    [ObservableProperty] private string _headline = "Nothing recorded yet";

    [ObservableProperty] private string _headlineDetail =
        "Repairing a volume writes a journal beside the app's own data. Nothing has been " +
        "written to one yet.";

    [ObservableProperty] private string _headlineTone = "neutral";

    /// <summary>Where the journals are, shown so the files can be found without this screen.</summary>
    public string Folder => JournalReader.DefaultFolder;

    private Task? _loading;

    /// <summary>Reads the journals the first time the section is opened.</summary>
    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsBusy = true;
        Runs.Clear();

        try
        {
            Status = "Reading the journals...";

            var runs = await Task.Run(() => JournalReader.Files()
                .SelectMany(file => JournalReader.Runs(JournalReader.Read(file), file))
                .OrderByDescending(r => r.StartedUtc)
                .ToArray()).ConfigureAwait(true);

            foreach (var run in runs) Runs.Add(new JournalRunViewModel(run));

            SelectedRun = Runs.FirstOrDefault();

            RunCount = Runs.Count;
            FailureCount = Runs.Sum(r => r.Failures);

            UpdateHeadline();

            Status = Runs.Count == 0
                ? $"No journals in {Folder}."
                : $"{Plural.Of(Runs.Count, "run")} recorded, " +
                  $"{FailureCount} failed {Plural.Word(FailureCount, "write")}.";
        }
        catch (Exception ex)
        {
            Status = $"Could not read the journals: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// The heading, which leads with failures when there are any.
    /// </summary>
    /// <remarks>
    /// A history screen that opens on "12 runs" when three of them failed has buried
    /// the only line worth reading. The count of runs is context; a failed write is
    /// the finding.
    /// </remarks>
    private void UpdateHeadline()
    {
        (Headline, HeadlineDetail, HeadlineTone) = Summarise(RunCount, FailureCount);
    }

    public static (string Headline, string Detail, string Tone) Summarise(int runs, int failures)
    {
        if (runs == 0)
        {
            return ("Nothing recorded yet",
                "Repairing a volume writes a journal beside the app's own data. Nothing has " +
                "been written to one yet.",
                "neutral");
        }

        if (failures > 0)
        {
            return ($"{Plural.Of(failures, "write")} failed",
                $"Across {Plural.Of(runs, "run")}. A failed write means the machine was not changed the way " +
                "the run intended, whatever the screen said at the time - open the run to see " +
                "which one and why.",
                "warning");
        }

        return ("Every write succeeded",
            $"{Plural.Of(runs, "run")} recorded, and nothing in them failed.",
            "good");
    }

    private bool CanRestoreRun() => SelectedRun is { CanRestore: true } && !IsBusy;

    /// <summary>
    /// Puts back what a run moved into quarantine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Quarantine has always been a move rather than a delete, and nothing ever moved
    /// anything back. An operator who quarantined a file that turned out to be theirs
    /// had a folder full of sanitised names and no record of where any of them came
    /// from - except the journal, which is what this reads.
    /// </para>
    /// <para>
    /// It writes through the same gate as everything else and appends to the same
    /// journal the run came from, because a restore is a change to the machine and the
    /// record has to account for it too.
    /// </para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRestoreRun))]
    private async Task RestoreRunAsync()
    {
        if (SelectedRun is not { } run) return;

        IsBusy = true;

        try
        {
            var pairs = run.Quarantined.Where(p => File.Exists(p.Stored)).ToArray();

            if (pairs.Length == 0)
            {
                Status = "Nothing from this run is still in quarantine.";
                return;
            }

            var path = string.IsNullOrEmpty(run.Run.SourceFile)
                ? Path.Combine(Folder, "journal-restore.jsonl")
                : run.Run.SourceFile;

            await using var journal = new JsonlJournal(path);
            var gate = new Win32WriteGate(journal, dryRun: false);

            int back = 0, failed = 0;

            foreach (var (original, stored) in pairs)
            {
                Status = $"Putting back {original}...";

                // Refused rather than overwritten: something already at the original
                // path is not this file, and replacing it would be a second mistake
                // on top of the one being undone.
                if (File.Exists(original))
                {
                    failed++;
                    continue;
                }

                var copied = await gate.CopyFileAsync(
                    ExtendedPath.From(stored), ExtendedPath.From(original), null, default)
                    .ConfigureAwait(true);

                if (!copied.Succeeded)
                {
                    failed++;
                    continue;
                }

                await gate.DeleteFileAsync(ExtendedPath.From(stored), default).ConfigureAwait(true);
                back++;
            }

            Status = failed == 0
                ? $"{Plural.Of(back, "file")} put back where they were."
                : $"{back} put back, {failed} could not be - something is already at their old path, " +
                  "or the copy was refused.";

            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status = $"Could not put anything back: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSelectedRunChanged(JournalRunViewModel? value) =>
        RestoreRunCommand.NotifyCanExecuteChanged();

    partial void OnIsBusyChanged(bool value) => RestoreRunCommand.NotifyCanExecuteChanged();

    /// <summary>Opens the journal folder, for anyone who wants the files themselves.</summary>
    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            Process.Start(new ProcessStartInfo(Folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = $"Could not open {Folder}: {ex.Message}";
        }
    }
}
