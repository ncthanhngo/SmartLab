using System.Collections.ObjectModel;
using SmartLab.Maintenance;

namespace SmartLab.App;

/// <summary>
/// The states a capture run would otherwise never reach.
/// </summary>
/// <remarks>
/// <para>
/// A screenshot run opens every section and finds each at rest. Everything that only
/// exists after somebody presses something - a progress band mid-run, a verdict in
/// each tone, a window full of log lines, a list of leftovers - is drawn by templates
/// no automated run has ever instantiated.
/// </para>
/// <para>
/// Three releases in one day shipped faults of exactly that kind: a resource key the
/// frame's template could not resolve, and two lists bound to one collection in a
/// window that only opens during a removal. Both took the window down on sight. Both
/// would have been caught by rendering the state once.
/// </para>
/// <para>
/// So this fills view models with plausible values and hands them to the real
/// templates. Nothing here touches the machine: no program is uninstalled, no file is
/// removed, no volume is read. What it proves is that the interface can draw what the
/// application will ask it to draw.
/// </para>
/// </remarks>
public static class SelfTest
{
    /// <summary>One state worth rendering, and the name its capture is filed under.</summary>
    public readonly record struct State(string Name, Action Arrange);

    /// <summary>
    /// Every state, in the order they are rendered.
    /// </summary>
    /// <remarks>
    /// Arranged rather than acted: each entry sets view model properties directly.
    /// Driving the real commands would mean really uninstalling something, and a
    /// self-test that changes the machine is one nobody dares run.
    /// </remarks>
    public static IReadOnlyList<State> States(MainViewModel shell) =>
    [
        new("uninstall-running", () =>
        {
            var uninstall = shell.Uninstall;

            uninstall.RunningFor = "Some Vendor Tool 4.2";
            uninstall.Activity.Clear();

            Say(uninstall, UninstallStepKind.Info, @"Running: C:\Program Files\Some Vendor\unins000.exe /SILENT");
            Say(uninstall, UninstallStepKind.Info, "Started as process 8124. Answer any prompt it shows.");
            Say(uninstall, UninstallStepKind.Ok, "Uninstaller finished with exit code 0.");
            Say(uninstall, UninstallStepKind.Info, "Looking for what it left behind.");
            Say(uninstall, UninstallStepKind.Warning, @"Still there: C:\Program Files\Some Vendor (48.2 MB)");
            Say(uninstall, UninstallStepKind.Failed, @"Could not remove C:\Program Files\Some Vendor: access denied");

            uninstall.Progress.Step("Deep scan", 60);
        }),

        new("uninstall-finished", () =>
        {
            var uninstall = shell.Uninstall;

            uninstall.Leftovers.Clear();

            Add(uninstall.Leftovers, TraceKind.Directory, @"C:\Program Files\Some Vendor",
                "Install folder left behind", TraceEvidence.PointsAtApp, 50_500_000);

            Add(uninstall.Leftovers, TraceKind.RegistryKey, @"HKEY_CURRENT_USER\Software\Some Vendor",
                "Registry key named after the publisher", TraceEvidence.NameMatch);

            uninstall.Progress.Finish("warning", "Uninstalled, with leftovers",
                "Some Vendor Tool 4.2 is gone. 2 thing(s) it registered are still on disk.");
        }),

        new("updater-running", () =>
        {
            // A driver install part way through: rows in each phase, and the log the
            // worker's transcript feeds. None of it exists until somebody presses the
            // button and answers a UAC prompt, which no automated run can do - so the
            // panel and its tones had never been drawn.
            var updater = shell.Updater;

            updater.ShowingDrivers = true;
            updater.Activity.Clear();
            updater.Drivers.Clear();

            Driver(updater, "Intel Corporation - Display", "installed");
            Driver(updater, "Realtek Semiconductor Corp. - MEDIA", "downloading");
            Driver(updater, "NVIDIA - Display", "waiting");

            updater.Activity.Add(new UpdaterStepViewModel(
                "Asking for Administrator. Nothing is downloaded until it is granted.", "neutral"));
            updater.Activity.Add(new UpdaterStepViewModel(
                "[step] 1/3 downloading Intel Corporation - Display  (118.4 MB)", "neutral"));
            updater.Activity.Add(new UpdaterStepViewModel(
                "[step] 1/3 installing Intel Corporation - Display", "neutral"));
            updater.Activity.Add(new UpdaterStepViewModel(
                "[ok] Intel Corporation - Display", "good"));
            updater.Activity.Add(new UpdaterStepViewModel(
                "[FAIL] Realtek Semiconductor Corp. - MEDIA  Windows Update returned 4, 0x80240022", "alert"));

            // The dial reads from the count rather than from the list, so a capture that
            // skipped it would show three rows under a heading saying nothing is due.
            updater.DriverCount = updater.Drivers.Count;
            updater.DriverGaugePercent = 1;

            (updater.DriverHeadline, updater.DriverHeadlineDetail) =
                UpdaterViewModel.SummariseDrivers(updater.Drivers.Count, updater.Drivers.Count, undriven: 0);

            updater.Progress.Step("Downloading 2 of 3", 100.0 * 1 / 3);
        }),

        // Each tone of the band, because the tone is carried by triggers that only
        // fire on the value they name.
        new("band-good", () => shell.Cleanup.Progress.Finish(
            "good", "Measured", "Nothing has been deleted. What is ticked is what Clean would remove.")),

        new("band-alert", () => shell.Malware.Progress.Finish(
            "alert", "The scan could not run", "Defender is not available on this machine.")),

        new("band-indeterminate", () =>
        {
            shell.SpaceLens.Progress.Begin("Measuring C:\\");
            shell.SpaceLens.Progress.Unknown("Measuring... 3,402 folders, 51,118 files");
        }),

        new("history-populated", () =>
        {
            // A history with a failed run in it, which is the state worth drawing:
            // the empty one is what every machine shows before anything has happened.
            var history = shell.History;

            history.Runs.Clear();
            history.Runs.Add(SampleRun());
            history.SelectedRun = history.Runs[0];

            history.RunCount = 1;
            history.FailureCount = 3;

            (history.Headline, history.HeadlineDetail, history.HeadlineTone) =
                HistoryViewModel.Summarise(1, 3);
        }),
    ];

    /// <summary>One driver row, already in the phase this state wants to draw.</summary>
    private static void Driver(UpdaterViewModel updater, string title, string outcome) =>
        updater.Drivers.Add(new DriverViewModel(new DriverUpdate(
            Guid.NewGuid().ToString(), title, title, "Intel",
            "31.0.101.5333", "2024-06-12", "2026-07-14", 118_400_000))
        {
            Outcome = outcome,
        });

    private static void Say(UninstallViewModel uninstall, UninstallStepKind kind, string text) =>
        uninstall.Activity.Add(new UninstallStepViewModel(new UninstallStep(kind, text)));

    private static void Add(
        ObservableCollection<TraceItemViewModel> into, TraceKind kind, string location,
        string description, TraceEvidence evidence, long bytes = 0) =>
        into.Add(new TraceItemViewModel(new AppTrace(kind, location, description)
        {
            Exists = true,
            SizeBytes = bytes,
            Evidence = evidence,
        }));

    /// <summary>A run shaped like the one that went unseen for a whole day.</summary>
    private static JournalRunViewModel SampleRun()
    {
        var started = DateTimeOffset.Now.AddMinutes(-5);

        Core.Abstractions.JournalRecord Record(string kind, string target, bool ok, string? detail = null) =>
            new(started, kind, target, ok, detail);

        return new JournalRunViewModel(new Engine.Journal.JournalRun(
            started,
            @"E:\",
            [
                Record("plan-begin", @"E:\", true, "3 approved action(s), dryRun=False"),
                Record("create-directory", @"C:\Users\x\SmartLab\quarantine", false,
                    "The system cannot find the path specified."),
                Record("create-directory", @"C:\Users\x\SmartLab\quarantine", false,
                    "The system cannot find the path specified."),
                Record("create-directory", @"C:\Users\x\SmartLab\rescue", false,
                    "The system cannot find the path specified."),
                Record("plan-end", @"E:\", false, "0 succeeded, 3 failed"),
            ],
            "0 succeeded, 3 failed",
            Succeeded: false));
    }
}
