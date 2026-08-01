using SmartLab.App;
using SmartLab.Maintenance;
using Xunit;

namespace SmartLab.Tests;

/// <summary>
/// The scan that goes looking, and the rules that keep it from being reckless.
/// </summary>
/// <remarks>
/// It exists because the narrow scan was not enough in the ordinary case: Zalo's own
/// uninstaller removed its registration and left a gigabyte in
/// <c>%LOCALAPPDATA%\Programs\Zalo</c>, so a scan that only reads what a program says
/// about itself reported a clean removal over 1 GB of files. Going looking means
/// guessing, and what is covered here is the grading of those guesses.
/// </remarks>
public sealed class DeepTraceScannerTests
{
    [Theory]
    [InlineData("Zalo 26.06.11", "Zalo")]
    [InlineData("Python 3.14.6 (64-bit)", "Python")]
    [InlineData("SumatraPDF", "SumatraPDF")]
    [InlineData("Inno Setup version 6.7.3", "Inno Setup version")]
    public void AVersionNumberIsNotPartOfTheName(string displayName, string expected)
    {
        // The registry entry is called "Zalo 26.06.11" and the folder is called "Zalo".
        var names = DeepTraceScanner.NamesFor(new InstalledProgram(displayName, "key"));

        Assert.Contains(expected, names);
    }

    [Fact]
    public void AShortNameIsNotWorthSearchingFor()
    {
        // Two and three letter publishers match half the machine. A scan that proposes
        // to delete everything called "VNG" has stopped being evidence.
        var names = DeepTraceScanner.NamesFor(
            new InstalledProgram("7z", "key") { Publisher = "ABC" });

        Assert.Empty(names);
    }

    [Fact]
    public void ThePublisherIsSearchedForAsWellAsTheProgram()
    {
        var names = DeepTraceScanner.NamesFor(
            new InstalledProgram("Zalo 26.06.11", "key") { Publisher = "Zalo Group" });

        Assert.Equal(["Zalo", "Zalo Group"], names);
    }

    /// <remarks>
    /// The roots are where applications live, so a name match on a root itself is a
    /// proposal to delete every application. Common Files and WindowsApps are worse:
    /// they are shared, and the operator has no way to know that from the row.
    /// </remarks>
    [Theory]
    [InlineData(Environment.SpecialFolder.ProgramFiles)]
    [InlineData(Environment.SpecialFolder.ProgramFilesX86)]
    [InlineData(Environment.SpecialFolder.LocalApplicationData)]
    [InlineData(Environment.SpecialFolder.ApplicationData)]
    [InlineData(Environment.SpecialFolder.CommonApplicationData)]
    [InlineData(Environment.SpecialFolder.UserProfile)]
    [InlineData(Environment.SpecialFolder.Windows)]
    [InlineData(Environment.SpecialFolder.System)]
    public void ARootIsNeverProposed(Environment.SpecialFolder folder)
    {
        Assert.True(DeepTraceScanner.IsRefused(Environment.GetFolderPath(folder)));
    }

    [Fact]
    public void SharedRuntimeFoldersAreNeverProposed()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        Assert.True(DeepTraceScanner.IsRefused(Path.Combine(programFiles, "Common Files")));
        Assert.True(DeepTraceScanner.IsRefused(Path.Combine(programFiles, "WindowsApps")));
    }

    [Fact]
    public void AnOrdinaryApplicationFolderIsNotRefused()
    {
        // The guard has to say no to the roots without saying no to everything.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.False(DeepTraceScanner.IsRefused(Path.Combine(local, "Programs", "Zalo")));
    }

    /// <summary>
    /// Two independent things agreeing about one place stop being a guess.
    /// </summary>
    /// <remarks>
    /// This is what makes the deep scan useful rather than merely thorough. Zalo's
    /// folder is only ever found by its name - the program registered no location - so
    /// on name alone the gigabyte it left arrives unticked and the operator has to
    /// decide about it. A Start Menu entry called Zalo that launches an executable
    /// inside that folder is a second, separate thing saying the folder is Zalo's.
    /// </remarks>
    [Fact]
    public void AShortcutOfTheSameNameLaunchingFromAFolderVouchesForIt()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var folder = Path.Combine(local, "Programs", "Quuxinator");
        var exe = Path.Combine(folder, "Quuxinator.exe");
        var link = Path.Combine(desktop, "Quuxinator.lnk");

        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(exe, "not really an executable");

            if (!TryWriteShortcut(link, exe)) return; // no shell to make one with

            var found = new DeepTraceScanner(new Win32TraceProbe())
                .Scan(new InstalledProgram("Quuxinator", "key"));

            var directory = Assert.Single(found, t => t.Kind == TraceKind.Directory);

            Assert.Equal(TraceEvidence.PointsAtApp, directory.Evidence);
            Assert.False(directory.IsGuess);
        }
        finally
        {
            try { File.Delete(link); } catch { }
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }

    private static bool TryWriteShortcut(string link, string target)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return false;

            dynamic? shell = Activator.CreateInstance(type);
            if (shell is null) return false;

            dynamic shortcut = shell.CreateShortcut(link);
            shortcut.TargetPath = target;
            shortcut.Save();

            return File.Exists(link);
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public void AProgramThatWasNeverOnThisMachineLeavesNothingBehind()
    {
        // Against the real machine: a made-up name must not match anything, which is
        // the property that stops a deep scan from being a machine-wide grep.
        var scanner = new DeepTraceScanner(new Win32TraceProbe());

        var found = scanner.Scan(new InstalledProgram("Zorblatt Quuxinator", "key"));

        Assert.Empty(found);
    }

    /// <remarks>
    /// The whole safety story in one assertion. A name match is shown and left for the
    /// operator; evidence that points into the program's own folder is not a guess and
    /// arrives ready to go.
    /// </remarks>
    [Fact]
    public void OnlyEvidenceStrongerThanANameArrivesTicked()
    {
        var guess = new TraceItemViewModel(new AppTrace(
            TraceKind.RegistryKey, @"HKEY_CURRENT_USER\Software\VNG", "named after the publisher")
        { Evidence = TraceEvidence.NameMatch });

        var pointer = new TraceItemViewModel(new AppTrace(
            TraceKind.File, @"C:\Users\x\Desktop\Zalo.lnk", "points into the program's folder")
        { Evidence = TraceEvidence.PointsAtApp });

        var registered = new TraceItemViewModel(new AppTrace(
            TraceKind.Directory, @"C:\Program Files\Zalo", "the folder it registered")
        { Evidence = TraceEvidence.Registered });

        Assert.False(guess.IsSelected);
        Assert.True(guess.IsGuess);
        Assert.Equal("name only", guess.EvidenceText);

        Assert.True(pointer.IsSelected);
        Assert.False(pointer.IsGuess);

        Assert.True(registered.IsSelected);
    }
}
