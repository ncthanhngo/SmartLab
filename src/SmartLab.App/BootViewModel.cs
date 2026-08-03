using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartLab.Maintenance;

namespace SmartLab.App;

/// <summary>One offered fix, with the operator's decision attached.</summary>
public sealed partial class BootFixViewModel(BootFix fix) : ObservableObject
{
    public BootFix Fix { get; } = fix;

    public string Title => Fix.Title;
    public string Detail => Fix.Detail;

    /// <summary>
    /// Unticked. Always.
    /// </summary>
    /// <remarks>
    /// These rewrite a partition table or a boot sector. Nothing in this app that
    /// cannot be undone arrives already chosen, and this is the least undoable thing
    /// in it.
    /// </remarks>
    [ObservableProperty] private bool _isSelected;

    [ObservableProperty] private string _outcome = string.Empty;
}

/// <summary>
/// Whether a PC will start from the selected stick, and what would make it.
/// </summary>
/// <remarks>
/// Lives beside the rest of Repair because it answers the same question about the same
/// drive: this half asks whether the files survived, that half asks whether the machine
/// will still start from it. A stick cleaned of a worm that no longer boots has been
/// half repaired.
/// </remarks>
public sealed partial class BootViewModel(MainViewModel shell) : ObservableObject
{
    public ObservableCollection<BootFixViewModel> Fixes { get; } = [];

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private bool _hasChecked;

    [ObservableProperty] private string _headline = "Not checked";

    [ObservableProperty] private string _detail =
        "Reads how the drive is flagged, whether its boot sector is signed, and which loaders are on it. " +
        "Checking writes nothing.";

    /// <summary>"good", "warning", "alert" or "neutral" - what the lamp shows.</summary>
    [ObservableProperty] private string _tone = "neutral";

    [ObservableProperty] private string _status = string.Empty;

    private BootHealth? _health;

    public bool HasFixes => Fixes.Count > 0;

    [RelayCommand]
    private async Task CheckAsync()
    {
        if (shell.SelectedDrive is not { } drive)
        {
            Reset("No drive selected", "Choose a drive above, then check.");
            return;
        }

        IsBusy = true;
        Status = $"Reading {drive.Root}...";

        try
        {
            var health = await Task.Run(() => BootScanner.Inspect(drive)).ConfigureAwait(true);
            var verdict = BootAssessment.Evaluate(health);

            _health = health;

            Headline = verdict.Headline;
            Detail = verdict.Detail;
            Tone = verdict.Tone;

            Fixes.Clear();

            foreach (var fix in verdict.Fixes)
            {
                var row = new BootFixViewModel(fix);

                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(BootFixViewModel.IsSelected)) OnFixesTicked();
                };

                Fixes.Add(row);
            }

            HasChecked = true;
            OnPropertyChanged(nameof(HasFixes));

            // The list this button acts on has just been replaced, and the new rows
            // arrive unticked. Without this the button keeps the count from the check
            // before it - which after an apply is the count it just finished applying.
            OnFixesTicked();

            Status = verdict.Fixes.Count == 0
                ? "Nothing here can be repaired by flipping a flag or rewriting boot code."
                : $"{verdict.Fixes.Count} fix(es) offered. Nothing is ticked.";
        }
        catch (Exception ex)
        {
            Reset("Could not be read", ex.Message);
        }
        finally
        {
            IsBusy = false;
            ApplyCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanApply() => !IsBusy && _health is not null && Fixes.Any(f => f.IsSelected);

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (shell.SelectedDrive is not { } drive || _health is not { } health) return;

        // Checked again at the moment of writing rather than only when the fixes were
        // offered: the drive selection can have moved to a fixed disk in between.
        if (BootRepairRunner.Refuse(drive) is { } refusal)
        {
            Status = refusal;
            return;
        }

        var chosen = Fixes.Where(f => f.IsSelected).ToArray();
        if (chosen.Length == 0) return;

        IsBusy = true;

        try
        {
            var done = 0;

            foreach (var row in chosen)
            {
                Status = $"{row.Title} on {drive.Root}...";

                // The check was the dry run, and a fix only exists here because that
                // check offered it and the operator ticked it. Writing is the whole
                // point of the second press.
                var result = await BootRepairRunner
                    .ApplyAsync(row.Fix, drive, health, dryRun: false)
                    .ConfigureAwait(true);

                row.Outcome = result.Output;
                if (result.Succeeded) done++;
            }

            Status = $"{done} of {chosen.Length} fix(es) applied. Each result is on its row.";

            // What was true before an apply is not what is true after it.
            await CheckAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status = $"Boot repair failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Clears the verdict, for when the drive it described is no longer selected.</summary>
    public void Reset(string headline = "Not checked", string? detail = null)
    {
        _health = null;

        Fixes.Clear();
        HasChecked = false;
        Headline = headline;
        Tone = "neutral";

        Detail = detail ??
            "Reads how the drive is flagged, whether its boot sector is signed, and which loaders are " +
            "on it. Checking writes nothing.";

        Status = string.Empty;

        OnPropertyChanged(nameof(HasFixes));
        OnFixesTicked();
    }

    /// <summary>What the button will do, and to how many findings.</summary>
    public string ActionLabel => ActionWording.For("Fix", TickedCount, "item");

    /// <summary>Whether it has anything to act on, which is what lights it up.</summary>
    public bool HasTicked => TickedCount > 0;

    private int TickedCount => Fixes.Count(f => f.IsSelected);

    /// <summary>Everything that follows the ticks, said in one place.</summary>
    /// <remarks>
    /// The list changes in three ways - a row ticked, a check that replaces it, a reset
    /// that empties it - and all three have to reach the button. Kept together so a
    /// fourth one cannot answer only half.
    /// </remarks>
    private void OnFixesTicked()
    {
        ApplyCommand.NotifyCanExecuteChanged();

        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(HasTicked));
    }

    partial void OnIsBusyChanged(bool value) => ApplyCommand.NotifyCanExecuteChanged();
}
