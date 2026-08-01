using System.Windows.Threading;
using SmartLab.App;
using SmartLab.Maintenance;
using Xunit;

namespace SmartLab.Tests;

/// <summary>
/// The Uninstall section's verb, driven against this machine's real program list.
/// </summary>
/// <remarks>
/// The button is disabled until a program with a registered uninstaller is selected,
/// which is correct and also the most likely thing to be mistaken for a broken
/// feature. These hold the path from selecting a row to the command being runnable.
/// </remarks>
public sealed class UninstallFlowTests
{
    [Fact]
    public void SelectingAProgramWithAnUninstallerEnablesTheButton()
    {
        OnDispatcher(async () =>
        {
            var uninstall = new MainViewModel().Uninstall;

            Assert.False(uninstall.UninstallProgramCommand.CanExecute(null));

            await uninstall.ScanProgramsCommand.ExecuteAsync(null);

            Assert.NotEmpty(uninstall.Programs);

            var removable = uninstall.Programs.FirstOrDefault(p => p.HasUninstaller);
            Assert.NotNull(removable);

            // Still off: a scan lists, it does not choose.
            Assert.False(uninstall.UninstallProgramCommand.CanExecute(null));

            uninstall.SelectedProgram = removable;

            Assert.True(uninstall.UninstallProgramCommand.CanExecute(null));
        });
    }

    [Fact]
    public void AProgramWithNoRegisteredUninstallerLeavesTheButtonOff()
    {
        OnDispatcher(async () =>
        {
            var uninstall = new MainViewModel().Uninstall;

            await uninstall.ScanProgramsCommand.ExecuteAsync(null);

            var stuck = uninstall.Programs.FirstOrDefault(p => !p.HasUninstaller);
            if (stuck is null) return; // Nothing on this machine to check it with.

            uninstall.SelectedProgram = stuck;

            Assert.False(uninstall.UninstallProgramCommand.CanExecute(null));
        });
    }

    [Fact]
    public void OpeningTheSectionFillsTheListWithoutAnyonePressingAnything()
    {
        // The screen used to open on an empty panel with a button that filled it in.
        OnDispatcher(async () =>
        {
            var shell = new MainViewModel();

            Assert.Empty(shell.Uninstall.Programs);

            shell.SelectedSection = shell.Sections.Single(s => s.Key == "uninstall");

            // The selection starts the load; awaiting the same entry point is how a
            // test waits for it without reaching into the command.
            await shell.Uninstall.EnsureLoadedAsync();

            Assert.NotEmpty(shell.Uninstall.Programs);
        });
    }

    /// <summary>
    /// The section's own verb, driven end to end, with the log it produces.
    /// </summary>
    /// <remarks>
    /// The registered command is a harmless one that exits cleanly and the install
    /// folder is a path that does not exist, so this runs the whole thing - launch,
    /// wait, leftover scan - without removing anything from the machine running the
    /// tests. What it holds is that the operator is told what happened at every step:
    /// the log is the feature, and an empty one is the failure mode.
    /// </remarks>
    [Fact]
    public void UninstallingWritesALogOfWhatItRanAndWhereItLooked()
    {
        OnDispatcher(async () =>
        {
            var uninstall = new MainViewModel().Uninstall;

            uninstall.Programs.Add(new InstalledProgram("Stub", @"HKCU\Software\Stub\NotThere")
            {
                UninstallString = "cmd.exe /c exit 0",
                InstallLocation = @"C:\Program Files\Stub That Is Not There",
            });

            uninstall.SelectedProgram = uninstall.Programs[^1];

            await uninstall.UninstallProgramCommand.ExecuteAsync(null);

            Assert.NotEmpty(uninstall.Activity);
            Assert.Contains(uninstall.Activity, s => s.Text.Contains("exit 0", StringComparison.Ordinal));
            Assert.Contains(uninstall.Activity,
                s => s.Text.Contains(@"Stub That Is Not There", StringComparison.Ordinal));
            Assert.Contains(uninstall.Activity,
                s => s.Text.Contains(@"HKCU\Software\Stub\NotThere", StringComparison.Ordinal));

            // Nothing survived a program that was never installed, and the log says so
            // rather than leaving the absence to be inferred.
            Assert.Empty(uninstall.Leftovers);
            Assert.Contains(uninstall.Activity, s => s.Tone == "good");
        });
    }

    /// <summary>
    /// The runner against a real process, which is the half no parser test reaches.
    /// </summary>
    /// <remarks>
    /// Stands in for uninstalling something. The registered command is a harmless one
    /// that exits with a known code, so this exercises parse, launch, wait and exit
    /// code without removing anything from the machine running the tests - which is
    /// the only reason this path had never been driven.
    /// </remarks>
    [Fact]
    public async Task TheRegisteredCommandIsActuallyRunAndItsExitCodeReported()
    {
        var program = new InstalledProgram("Stub", @"HKCU\Software\Stub")
        {
            UninstallString = "cmd.exe /c exit 3",
        };

        var result = await new ProgramUninstaller(new NullTraceProbe())
            .RunAsync(program, quiet: false);

        Assert.Equal(UninstallOutcome.Completed, result.Outcome);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task TheQuietCommandIsPreferredWhenTheVendorRegisteredOne()
    {
        var program = new InstalledProgram("Stub", @"HKCU\Software\Stub")
        {
            UninstallString = "cmd.exe /c exit 1",
            QuietUninstallString = "cmd.exe /c exit 7",
        };

        var result = await new ProgramUninstaller(new NullTraceProbe())
            .RunAsync(program, quiet: true);

        Assert.Equal(7, result.ExitCode);
    }

    /// <summary>
    /// The commentary the section shows while a removal runs.
    /// </summary>
    /// <remarks>
    /// The command line matters more than any other line in it: a silent switch that
    /// turned out not to be silent, or an msiexec argument that opens a repair dialog
    /// instead of removing anything, is visible there and nowhere else.
    /// </remarks>
    [Fact]
    public async Task TheRunReportsTheCommandItRanAndTheCodeItGaveBack()
    {
        var program = new InstalledProgram("Stub", @"HKCU\Software\Stub")
        {
            UninstallString = "cmd.exe /c exit 4",
        };

        var steps = new StepLog();

        await new ProgramUninstaller(new NullTraceProbe()).RunAsync(program, quiet: false, steps);

        Assert.Contains(steps.Lines, s => s.Text.Contains("/c exit 4", StringComparison.Ordinal));
        Assert.Contains(steps.Lines, s => s.Text.Contains("exit code 4", StringComparison.Ordinal));

        // A non-zero code is reported without being called a failure. Vendors use
        // them for "the user cancelled" as readily as for "it broke".
        Assert.DoesNotContain(steps.Lines, s => s.Kind == UninstallStepKind.Failed);
    }

    /// <remarks>
    /// Every place looked at is named, including the ones that came back clean.
    /// "Nothing was left behind" is a claim, and a log that only lists what it found
    /// leaves no way to tell a thorough scan from one that never ran.
    /// </remarks>
    [Fact]
    public void TheLeftoverScanNamesEveryPlaceItLookedEvenWhenAllOfThemAreClean()
    {
        var program = new InstalledProgram("Stub", @"HKCU\Software\Stub\Uninstall")
        {
            InstallLocation = @"C:\Program Files\Stub",
        };

        var steps = new StepLog();

        var leftovers = new ProgramUninstaller(new NullTraceProbe()).ScanLeftovers(program, steps);

        Assert.Empty(leftovers);
        Assert.Contains(steps.Lines, s => s.Text.Contains(program.InstallLocation!, StringComparison.Ordinal));
        Assert.Contains(steps.Lines, s => s.Text.Contains(program.RegistryKeyPath, StringComparison.Ordinal));
    }

    /// <remarks>
    /// Not <see cref="Progress{T}"/>: that one posts each report to a captured
    /// synchronisation context, so under a test runner the lines can still be in
    /// flight when the assertions run. This one records on the calling thread.
    /// </remarks>
    private sealed class StepLog : IProgress<UninstallStep>
    {
        public List<UninstallStep> Lines { get; } = [];

        public void Report(UninstallStep value) => Lines.Add(value);
    }

    [Fact]
    public async Task AProgramWithNoCommandIsReportedRatherThanLaunched()
    {
        var program = new InstalledProgram("Stub", @"HKCU\Software\Stub");

        var result = await new ProgramUninstaller(new NullTraceProbe())
            .RunAsync(program, quiet: true);

        Assert.Equal(UninstallOutcome.NoUninstaller, result.Outcome);
        Assert.Null(result.ExitCode);
    }

    /// <summary>A probe that finds nothing, for tests about running rather than leftovers.</summary>
    private sealed class NullTraceProbe : ITraceProbe
    {
        public bool FileExists(string path) => false;
        public bool DirectoryExists(string path) => false;
        public long DirectorySize(string path) => 0;
        public (long Bytes, int Files) DirectoryStats(string path) => (0, 0);
        public long FileSize(string path) => 0;
        public long RecycleBinSize() => 0;
        public bool RegistryValueExists(string keyPath, string valueName) => false;
        public bool RegistryKeyExists(string keyPath) => false;
    }

    [Fact]
    public void NoProgramOnThisMachineWouldBeAskedToRepairInsteadOfUninstall()
    {
        // Against the real registry rather than a fixture. On the machine this was
        // written on, 99 of 134 MSI entries register /I - install mode - and every
        // one of them would have opened a repair dialog instead of removing
        // anything. This is the assertion that would have caught it.
        var programs = new InstalledProgramScanner().Scan();

        Assert.NotEmpty(programs);

        foreach (var program in programs.Where(p => p.HasUninstaller))
        {
            var command = UninstallCommandParser.Parse(
                program.QuietUninstallString ?? program.UninstallString);

            if (!Path.GetFileNameWithoutExtension(command.FileName)
                    .Equals("msiexec", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Assert.False(
                command.Arguments.StartsWith("/I", StringComparison.OrdinalIgnoreCase) ||
                command.Arguments.StartsWith("-I", StringComparison.OrdinalIgnoreCase),
                $"'{program.DisplayName}' would be asked to repair: {command.Arguments}");
        }
    }

    /// <remarks>
    /// Same reason as <c>SmartScanTests</c>: the view models await with the caller's
    /// context captured, and a grouped collection view belongs to the thread that
    /// created it.
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
                try { await work(); }
                catch (Exception ex) { failure = ex; }
                finally { dispatcher.InvokeShutdown(); }
            });

            Dispatcher.Run();
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "The scan did not finish.");

        if (failure is not null) throw failure;
    }
}
