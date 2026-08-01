using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartLab.Maintenance;

namespace SmartLab.App;

/// <summary>One upgradable package, with the operator's decision attached.</summary>
public sealed partial class PackageViewModel(UpgradablePackage package) : ObservableObject
{
    public UpgradablePackage Package { get; } = package;

    public string Id => Package.Id;
    public string Name => Package.Name;
    public string Installed => Package.Installed;
    public string Available => Package.Available;
    public bool NotFromWinget => Package.NotFromWinget;

    /// <summary>
    /// Ticked, except for packages winget did not install.
    /// </summary>
    /// <remarks>
    /// An upgrade replaces a program with a newer build of the same program, which is
    /// what the operator opened this screen for. The exception is a package winget
    /// merely recognises: upgrading that swaps a hand-placed build for the store's,
    /// which is occasionally the one thing someone was avoiding.
    /// </remarks>
    [ObservableProperty]
    private bool _isSelected = !package.NotFromWinget;

    [ObservableProperty] private string _outcome = string.Empty;
}

public sealed partial class UpdaterViewModel : ObservableObject
{
    public ObservableCollection<PackageViewModel> Packages { get; } = [];

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string _status =
        "Lists what winget would upgrade. Nothing is installed until you say so.";

    [ObservableProperty] private int _packageCount;
    [ObservableProperty] private double _gaugePercent;
    [ObservableProperty] private string _headline = "Not checked yet";

    [ObservableProperty] private string _headlineDetail =
        "Upgrades run through winget, which is what installed most of these in the first place. " +
        "This app never downloads anything itself.";

    /// <summary>What this section is doing, and what it did. Drawn by the frame.</summary>
    public SectionProgress Progress { get; } = new();

    [RelayCommand]
    private async Task CheckAsync()
    {
        IsBusy = true;
        Packages.Clear();

        // winget answers when it answers, and says nothing on the way. The bar moves
        // without a figure rather than pretending to know how far in it is.
        Progress.Begin("Asking winget what is out of date");

        try
        {
            Status = "Asking winget what is out of date...";

            var (packages, error) = await Task.Run(WingetBridge.ListUpgrades).ConfigureAwait(true);

            foreach (var package in packages)
            {
                var row = new PackageViewModel(package);

                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(PackageViewModel.IsSelected)) UpdateSummary();
                };

                Packages.Add(row);
            }

            UpdateSummary();

            // A missing winget must never read as "everything is up to date". The
            // whole value of this section is knowing which of the two it is.
            Status = error is { Length: > 0 }
                ? error
                : packages.Count == 0
                    ? "Nothing to upgrade - winget reports every package current."
                    : $"{packages.Count} package(s) have a newer version.";

            // A missing winget is its own verdict. "Nothing to upgrade" from a tool
            // that never ran is the one answer this section must never give.
            Progress.Finish(
                error is { Length: > 0 } ? "alert" : packages.Count == 0 ? "good" : "warning",
                error is { Length: > 0 }
                    ? "winget could not answer"
                    : packages.Count == 0 ? "Everything is current" : $"{packages.Count} out of date",
                error is { Length: > 0 }
                    ? error
                    : packages.Count == 0
                        ? "winget reports every package it manages is on its newest version."
                        : "Tick what should be upgraded. Packages winget only recognises, rather " +
                          "than installed, start unticked.");
        }
        catch (Exception ex)
        {
            Status = $"Check failed: {ex.Message}";
            Progress.Finish("alert", "Check failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanUpgrade() => Packages.Count > 0 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUpgrade))]
    private async Task UpgradeTickedAsync()
    {
        var chosen = Packages.Where(p => p.IsSelected).ToArray();
        if (chosen.Length == 0)
        {
            Status = "Nothing ticked.";
            return;
        }

        // The check was the dry run: it asked winget what is out of date and installed
        // nothing. This button does not exist until that list does, and each row is
        // ticked by hand - the ones winget did not install start unticked.
        IsBusy = true;
        Progress.Begin($"Upgrading {chosen.Length} package(s)");

        try
        {
            var done = 0;
            var failed = 0;

            // One at a time, with its own result. A batch that fails halfway would
            // leave the operator unable to tell which packages actually changed.
            foreach (var row in chosen)
            {
                Status = $"Upgrading {row.Name}...";
                row.Outcome = "upgrading";

                Progress.Step($"Upgrading {row.Name}", 100.0 * (done + failed) / chosen.Length);

                var (succeeded, detail) = await Task.Run(() => WingetBridge.Upgrade(row.Id))
                    .ConfigureAwait(true);

                row.Outcome = succeeded ? "upgraded" : detail;

                if (succeeded) done++; else failed++;
            }

            Status = failed == 0
                ? $"{done} package(s) upgraded."
                : $"{done} upgraded, {failed} failed. Each result is on its row.";

            Progress.Finish(failed == 0 ? "good" : "warning",
                failed == 0 ? $"{done} upgraded" : $"{done} upgraded, {failed} failed",
                "Each result is on its own row. winget did the installing; this app only asked.");
        }
        catch (Exception ex)
        {
            Status = $"Upgrade failed: {ex.Message}";
            Progress.Finish("alert", "Upgrade failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateSummary()
    {
        PackageCount = Packages.Count;

        var ticked = Packages.Count(p => p.IsSelected);
        GaugePercent = PackageCount > 0 ? (double)ticked / PackageCount : 0;

        (Headline, HeadlineDetail) = Summarise(
            PackageCount, ticked, Packages.Count(p => p.NotFromWinget));

        UpgradeTickedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The heading above the dial.</summary>
    public static (string Headline, string Detail) Summarise(int found, int ticked, int foreign)
    {
        if (found == 0)
        {
            return ("Not checked yet",
                "Upgrades run through winget, which is what installed most of these in the first " +
                "place. This app never downloads anything itself.");
        }

        var detail = $"{ticked} of {found} ticked for upgrade.";

        if (foreign > 0)
        {
            detail += $" {foreign} were not installed by winget and start unticked - upgrading " +
                      "those replaces whatever build is there with the store's.";
        }

        return (ticked == 0 ? "Nothing ticked" : "Ready to upgrade", detail);
    }

    partial void OnIsBusyChanged(bool value) => UpgradeTickedCommand.NotifyCanExecuteChanged();
}
