using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartLab.Maintenance;

namespace SmartLab.App;

/// <summary>One repair command and whatever it last reported.</summary>
public sealed partial class RepairCommandViewModel(RepairCommand command) : ObservableObject
{
    public RepairCommand Command { get; } = command;

    public string Title => Command.Title;
    public string Detail => Command.Detail;
    public bool NeedsElevation => Command.NeedsElevation;
    public string CommandLine => $"{Command.Executable} {Command.Arguments}";

    [ObservableProperty] private string _outcome = "not run";
    [ObservableProperty] private bool _isRunning;
}

/// <summary>
/// Windows' own repair tools, run one at a time.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here repairs anything itself. Each entry launches a Microsoft tool and
/// shows what it printed, which is the same bargain the uninstaller strikes with a
/// vendor's uninstaller: the thing that owns the problem is the thing that should fix
/// it.
/// </para>
/// <para>
/// Output is shown verbatim rather than summarised. These commands report findings
/// this app has no business interpreting, and a paraphrase of "found corrupt files it
/// was unable to fix" is worse than the sentence itself.
/// </para>
/// </remarks>
public sealed partial class MaintenanceViewModel : ObservableObject
{
    public MaintenanceViewModel()
    {
        foreach (var command in RepairCommand.All)
            Commands.Add(new RepairCommandViewModel(command));
    }

    public ObservableCollection<RepairCommandViewModel> Commands { get; } = [];

    public ObservableCollection<string> Transcript { get; } = [];

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string _status =
        "Each of these is a Windows tool run as itself. Nothing here is run for you.";

    [ObservableProperty] private double _gaugePercent;
    [ObservableProperty] private int _completedCount;
    [ObservableProperty] private string _headline = "Nothing run yet";

    [ObservableProperty] private string _headlineDetail =
        "Four repair tools Windows ships with. Three need Administrator, so each one raises its " +
        "own prompt - this app never runs elevated.";

    private bool CanRun() => !IsBusy;

    /// <summary>
    /// Runs one command. Never a batch.
    /// </summary>
    /// <remarks>
    /// DISM after SFC is a sequence an operator chooses once the first has reported
    /// something, not one this app should assume. A "run all" button would also mean
    /// three UAC prompts in a row, which trains people to click through them.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync(RepairCommandViewModel? row)
    {
        if (row is null) return;

        IsBusy = true;
        row.IsRunning = true;
        row.Outcome = "running";

        Transcript.Clear();
        Status = $"Running {row.CommandLine}. This can take several minutes.";

        try
        {
            var result = await RepairCommandRunner.RunAsync(row.Command).ConfigureAwait(true);

            if (!result.Started)
            {
                row.Outcome = result.Error ?? "did not start";
                Status = result.Error ?? "The command did not start.";
                return;
            }

            foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                Transcript.Add(line.TrimEnd());

            row.Outcome = result.ExitCode == 0 ? "completed" : $"exit code {result.ExitCode}";

            Status = result.ExitCode == 0
                ? $"{row.Title} finished. Its output is below, unedited."
                : $"{row.Title} exited with code {result.ExitCode}. Read its output below.";
        }
        catch (Exception ex)
        {
            row.Outcome = "failed";
            Status = $"{row.Title} failed: {ex.Message}";
        }
        finally
        {
            row.IsRunning = false;
            IsBusy = false;
            UpdateSummary();
        }
    }

    private void UpdateSummary()
    {
        CompletedCount = Commands.Count(c => c.Outcome is "completed");
        GaugePercent = Commands.Count > 0 ? (double)CompletedCount / Commands.Count : 0;

        (Headline, HeadlineDetail) = Summarise(CompletedCount, Commands.Count);
    }

    /// <summary>The heading above the dial.</summary>
    public static (string Headline, string Detail) Summarise(int completed, int total)
    {
        if (completed == 0)
        {
            return ("Nothing run yet",
                "Four repair tools Windows ships with. Three need Administrator, so each one raises " +
                "its own prompt - this app never runs elevated.");
        }

        return ($"{completed} of {total} run",
            "Output is shown exactly as the tool printed it. These commands report things this app " +
            "has no business interpreting for you.");
    }

    partial void OnIsBusyChanged(bool value) => RunCommand.NotifyCanExecuteChanged();
}
