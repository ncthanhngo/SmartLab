using System.Reflection;
using System.Windows.Threading;
using SmartLab.App;
using Xunit;

namespace SmartLab.Tests;

/// <summary>
/// The front door's two-step flow.
/// </summary>
/// <remarks>
/// Home can act now, which it could not before. What keeps that safe is not the
/// absence of a verb but the shape of it: measuring and acting are separate presses,
/// the second is impossible until the first has finished, and it works only on what
/// the first found. These tests hold that shape.
/// </remarks>
public sealed class SmartScanTests
{
    [Fact]
    public void MeasuringAndActingAreSeparateCommands()
    {
        // The single most important property of this screen. One command that both
        // scanned and applied would be the "Fix everything" button that
        // plan-then-approve exists to prevent, whatever it happened to be called.
        var commands = typeof(SmartScanViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => typeof(System.Windows.Input.ICommand).IsAssignableFrom(p.PropertyType))
            .Select(p => p.Name)
            .Order()
            .ToArray();

        Assert.Equal(
            ["ApplyCommand", "CancelCommand", "OpenCommand", "OpenPillarCommand", "ScanCommand"],
            commands);
    }

    [Fact]
    public void NothingCanBeAppliedBeforeAScanHasRun()
    {
        // Apply works from what the scan found. With nothing found there is nothing
        // to work from, and the button must be dead rather than merely unhelpful.
        var scan = new MainViewModel().SmartScan;

        Assert.Equal(ScanPhase.Ready, scan.Phase);
        Assert.False(scan.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public void ConfirmingActsOnTheScanAndDoesNotMeasureAgain()
    {
        // The second press, driven end to end - the one flow nobody had ever taken
        // past the first button. What is held here is the shape: a confirm works from
        // the rows the scan produced, and never returns to Scanning on the way.
        //
        // Nothing on this machine is touched, and what stops it is the same thing that
        // stops it for an operator. A row here says "act on this section"; what that
        // section then does is decided by its own ticks, and every one of them is
        // cleared below. There is no dry run left to hide behind - a measure that
        // writes nothing is the dry run now - so this test has to be as deliberate as
        // the person it stands in for.
        OnDispatcher(async () =>
        {
            var shell = new MainViewModel();
            var scan = shell.SmartScan;

            var phases = new List<ScanPhase>();
            scan.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SmartScanViewModel.Phase)) phases.Add(scan.Phase);
            };

            await scan.ScanCommand.ExecuteAsync(null);

            Assert.True(scan.Phase == ScanPhase.Reviewing, scan.Status);
            Assert.NotEmpty(scan.Results);

            var reviewed = scan.Results.Select(r => r.Title).ToArray();

            // Untick every section's own list. Each verb below then finds nothing
            // chosen and returns without touching the machine - which is exactly the
            // guard the operator has, and the one this test would otherwise spend five
            // minutes proving by upgrading their packages.
            foreach (var category in shell.Cleanup.Categories) category.IsSelected = false;
            foreach (var bin in shell.TrashBins.Bins) bin.IsSelected = false;
            foreach (var item in shell.Optimization.Items) item.IsSelected = false;
            foreach (var package in shell.Updater.Packages) package.IsSelected = false;
            foreach (var action in shell.Actions) action.IsSelected = false;

            // Ticked by hand, standing in for the operator: this machine has nothing
            // that needs repairing, so nothing arrives actionable on its own. Both
            // flags, because apply acts on the intersection - a row that is actionable
            // but unticked is one the operator looked at and left alone.
            foreach (var row in scan.Results)
            {
                row.IsActionable = true;
                row.IsSelected = true;
            }

            Assert.True(scan.ApplyCommand.CanExecute(null));

            await scan.ApplyCommand.ExecuteAsync(null);

            Assert.Equal(ScanPhase.Done, scan.Phase);
            Assert.Equal(reviewed, scan.Results.Select(r => r.Title).ToArray());

            // A re-scan would have to pass back through Scanning, and would replace
            // the rows the operator reviewed with a freshly gathered set.
            Assert.DoesNotContain(ScanPhase.Scanning, phases.SkipWhile(p => p != ScanPhase.Applying));
            Assert.All(scan.Results, r => Assert.False(r.IsActionable));
        });
    }

    /// <summary>
    /// Runs async work on an STA thread with a real dispatcher, and rethrows what it
    /// threw.
    /// </summary>
    /// <remarks>
    /// The view models await with the calling context captured, which in the app is
    /// the UI thread. A test host has no such context, so continuations land on the
    /// thread pool and the first grouped collection view they touch throws - a
    /// failure about threads, from code that is not threaded, in a flow that works.
    /// The view model is built inside the thread too: a collection view belongs to
    /// the thread that created it.
    /// </remarks>
    private static void OnDispatcher(Func<Task> work)
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));

            _ = dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await work();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    dispatcher.InvokeShutdown();
                }
            });

            Dispatcher.Run();
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        // Generous: the scan runs winget, which talks to a package source.
        Assert.True(thread.Join(TimeSpan.FromMinutes(5)), "The scan did not finish.");

        if (failure is not null) throw failure;
    }

    [Fact]
    public void NothingCanBeAppliedWhileTheScanIsStillRunning()
    {
        var scan = new MainViewModel().SmartScan;
        scan.Phase = ScanPhase.Scanning;

        Assert.False(scan.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public void ScanningIsNotOfferedWhileAlreadyScanningOrApplying()
    {
        var scan = new MainViewModel().SmartScan;

        scan.Phase = ScanPhase.Scanning;
        Assert.False(scan.ScanCommand.CanExecute(null));

        scan.Phase = ScanPhase.Applying;
        Assert.False(scan.ScanCommand.CanExecute(null));

        scan.Phase = ScanPhase.Reviewing;
        Assert.True(scan.ScanCommand.CanExecute(null));
    }

    [Fact]
    public void APhaseCarriesExactlyOneMeaning()
    {
        // Scanning and reviewing must never both be true: the button reads Run in one
        // and Confirm in the other, and a screen showing both has lied about which
        // press the operator is about to make.
        var scan = new MainViewModel().SmartScan;

        foreach (var phase in Enum.GetValues<ScanPhase>())
        {
            scan.Phase = phase;
            Assert.False(scan.IsScanning && scan.IsReviewing);
        }
    }

    [Fact]
    public void ASkippedSectionIsNeverActionable()
    {
        // It could not look, so it has nothing to act on. Applying it would act on
        // whatever stale state the section happened to be holding.
        var outcome = new SectionOutcome("Updater", 0, "neutral", "winget missing", Skipped: true);

        Assert.False(outcome.IsActionable);
    }

    // ---- the headline ------------------------------------------------------------

    [Fact]
    public void ASkippedSectionIsNeverCountedAsClean()
    {
        // The easiest lie a summary screen can tell, and the hardest for a reader to
        // notice: six green rows, one of which never ran.
        var summary = SmartScanViewModel.Summarise(
            findings: 0, sections: 6, skipped: 2, worstTone: "good", ScanPhase.Reviewing);

        Assert.NotEqual("Nothing needs attention", summary.Headline);
        Assert.Contains("not counted as clean", summary.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("warning", summary.Tone);
    }

    [Fact]
    public void NothingFoundAndNothingSkippedIsClean()
    {
        var summary = SmartScanViewModel.Summarise(0, 6, 0, "good", ScanPhase.Reviewing);

        Assert.Equal("Nothing needs attention", summary.Headline);
        Assert.Equal("good", summary.Tone);
    }

    [Fact]
    public void TheWorstToneWins()
    {
        // A machine with one worm and five tidy sections is not "mostly fine", and an
        // average would say it was.
        var summary = SmartScanViewModel.Summarise(3, 6, 0, "danger", ScanPhase.Reviewing);

        Assert.Equal("danger", summary.Tone);
        Assert.Equal("Needs attention now", summary.Headline);
    }

    [Fact]
    public void BeforeRunningNothingIsClaimed()
    {
        var summary = SmartScanViewModel.Summarise(0, 0, 0, "neutral", ScanPhase.Ready);

        Assert.Equal("Ready when you are", summary.Headline);
        Assert.Equal("neutral", summary.Tone);
    }

    [Fact]
    public void WhileScanningTheHeadlineSaysNothingIsBeingChanged()
    {
        var summary = SmartScanViewModel.Summarise(0, 0, 0, "neutral", ScanPhase.Scanning);

        Assert.Contains("nothing is being changed", summary.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheReviewHeadlineSaysNothingHasHappenedYet()
    {
        // The moment the operator decides. It has to be unambiguous that the scan
        // changed nothing and that the next press is what will.
        var summary = SmartScanViewModel.Summarise(4, 6, 0, "warning", ScanPhase.Reviewing);

        Assert.Contains("nothing has been changed", summary.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("confirm", summary.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASkippedOutcomeIsDistinctFromZeroFindings()
    {
        var skipped = new SectionOutcome("Updater", 0, "neutral", "winget missing", Skipped: true);
        var clean = new SectionOutcome("Updater", 0, "good", "everything current");

        Assert.True(skipped.Skipped);
        Assert.False(clean.Skipped);
        Assert.NotEqual(skipped, clean);
    }
}
