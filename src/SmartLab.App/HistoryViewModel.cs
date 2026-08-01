using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartLab.Core.Abstractions;
using SmartLab.Engine.Journal;

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
        $"{Writes} write(s), {Failures} failed" + (Run.Unfinished ? ", unfinished" : string.Empty);

    public IReadOnlyList<JournalLineViewModel> Lines { get; } =
        run.Records.Select(r => new JournalLineViewModel(r)).ToArray();
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
                .SelectMany(file => JournalReader.Runs(JournalReader.Read(file)))
                .OrderByDescending(r => r.StartedUtc)
                .ToArray()).ConfigureAwait(true);

            foreach (var run in runs) Runs.Add(new JournalRunViewModel(run));

            SelectedRun = Runs.FirstOrDefault();

            RunCount = Runs.Count;
            FailureCount = Runs.Sum(r => r.Failures);

            UpdateHeadline();

            Status = Runs.Count == 0
                ? $"No journals in {Folder}."
                : $"{Runs.Count} run(s) recorded, {FailureCount} failed write(s).";
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
            return ($"{failures} write(s) failed",
                $"Across {runs} run(s). A failed write means the machine was not changed the way " +
                "the run intended, whatever the screen said at the time - open the run to see " +
                "which one and why.",
                "warning");
        }

        return ("Every write succeeded",
            $"{runs} run(s) recorded, and nothing in them failed.",
            "good");
    }

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
