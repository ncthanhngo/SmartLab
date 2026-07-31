using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartLab.Maintenance;

namespace SmartLab.App;

/// <summary>One browser extension. No decision attached - there is nothing to decide here.</summary>
public sealed class ExtensionViewModel(BrowserExtension extension)
{
    public BrowserExtension Extension { get; } = extension;

    public string Browser => Extension.Browser;
    public string Name => Extension.Name;
    public string Version => Extension.Version;
    public string Id => Extension.Id;
    public bool ReadsEverySite => Extension.ReadsEverySite;
    public string PermissionSummary => Extension.PermissionSummary;
}

/// <summary>
/// Shows what is installed in the browsers and in Explorer, and removes nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only by design, not by omission.</b> An extension's stored state lives in
/// the browser profile alongside cookies, saved logins and history - the files this
/// codebase has always refused to touch. Reaching in to disable one means writing to
/// that profile, and a browser rewrites its preferences on exit, so the edit would be
/// discarded without saying so.
/// </para>
/// <para>
/// So the section does the part that is genuinely useful and cannot go wrong: it says
/// what is installed and what each one is allowed to see. Removal stays with the
/// browser, which is one click away and knows how.
/// </para>
/// </remarks>
public sealed partial class ExtensionsViewModel : ObservableObject
{
    public ExtensionsViewModel()
    {
        GroupedExtensions.Source = Extensions;

        GroupedExtensions.SortDescriptions.Add(new SortDescription(
            nameof(ExtensionViewModel.Browser), ListSortDirection.Ascending));
        GroupedExtensions.SortDescriptions.Add(new SortDescription(
            nameof(ExtensionViewModel.Name), ListSortDirection.Ascending));

        GroupedExtensions.GroupDescriptions.Add(new PropertyGroupDescription(
            nameof(ExtensionViewModel.Browser)));
    }

    public ObservableCollection<ExtensionViewModel> Extensions { get; } = [];

    /// <summary><see cref="Extensions"/> grouped by browser.</summary>
    public CollectionViewSource GroupedExtensions { get; } = new();

    public ObservableCollection<ShellExtension> ShellExtensions { get; } = [];

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string _status =
        "Lists what is installed. Removing an extension stays with the browser that owns it.";

    [ObservableProperty] private int _extensionCount;
    [ObservableProperty] private double _gaugePercent;
    [ObservableProperty] private string _headline = "Not scanned yet";

    [ObservableProperty] private string _headlineDetail =
        "Reads each extension's manifest to show what it is allowed to see. Nothing in the " +
        "browser profile is modified or deleted.";

    [RelayCommand]
    private async Task ScanAsync()
    {
        IsBusy = true;
        Extensions.Clear();
        ShellExtensions.Clear();

        try
        {
            Status = "Reading extension manifests...";

            var browser = await Task.Run(BrowserExtensionScanner.Scan).ConfigureAwait(true);
            var shell = await Task.Run(ShellExtensionScanner.Scan).ConfigureAwait(true);

            foreach (var extension in browser) Extensions.Add(new ExtensionViewModel(extension));
            foreach (var extension in shell) ShellExtensions.Add(extension);

            UpdateSummary();

            Status = browser.Count == 0
                ? "No browser extensions found in Chrome or Edge."
                : $"{browser.Count} browser extension(s), {shell.Count} shell extension(s) listed.";
        }
        catch (Exception ex)
        {
            Status = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateSummary()
    {
        ExtensionCount = Extensions.Count;

        var broad = Extensions.Count(e => e.ReadsEverySite);

        // The ring is the share that can read every site - the one proportion on this
        // screen worth a glance, since it is what an operator would act on.
        GaugePercent = ExtensionCount > 0 ? (double)broad / ExtensionCount : 0;

        (Headline, HeadlineDetail) = Summarise(ExtensionCount, broad, ShellExtensions.Count);
    }

    /// <summary>The heading above the dial.</summary>
    public static (string Headline, string Detail) Summarise(int found, int readsEverySite, int shell)
    {
        if (found == 0)
        {
            return ("Not scanned yet",
                "Reads each extension's manifest to show what it is allowed to see. Nothing in " +
                "the browser profile is modified or deleted.");
        }

        var detail = $"{found} browser extension(s) and {shell} shell extension(s). ";

        detail += readsEverySite == 0
            ? "None of them asked to read every site."
            : $"{readsEverySite} can read and change data on every site you visit.";

        detail += " Remove them in the browser - this section never writes to a profile.";

        return (readsEverySite > 0 ? "Some can see everything" : "Nothing far-reaching", detail);
    }
}
