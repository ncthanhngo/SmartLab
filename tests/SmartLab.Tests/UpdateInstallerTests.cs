using SmartLab.Maintenance;
using Xunit;

namespace SmartLab.Tests;

/// <summary>
/// The one place this app overwrites itself.
/// </summary>
/// <remarks>
/// An unsigned archive fetched over the network and unpacked over a running tool is
/// the delivery route this app was written to clean up after. Every decision on the
/// way to that is asserted here; what is left unverified is only the download itself.
/// </remarks>
public sealed class UpdatePackageTests
{
    private static ReleaseAsset Asset(string name) =>
        new(name, $"https://example.invalid/{name}", 1024);

    [Fact]
    public void TheWindowsPackageIsChosenOverTheSourceArchiveGitHubAdds()
    {
        // GitHub attaches "Source code (zip)" to every release. Taking the first zip
        // would unpack a folder of C# files over a working installation.
        var assets = new[]
        {
            Asset("Source code (zip)"),
            Asset("SmartLab-1.1.0-win-x64.zip"),
            Asset("SHA256SUMS.txt"),
        };

        Assert.Equal("SmartLab-1.1.0-win-x64.zip", UpdatePackage.SelectPackage(assets)?.Name);
    }

    [Fact]
    public void AReleaseWithNoWindowsPackageOffersNothing()
    {
        var assets = new[] { Asset("Source code (zip)"), Asset("notes.md") };

        Assert.Null(UpdatePackage.SelectPackage(assets));
    }

    [Fact]
    public void TheChecksumListIsFoundByName()
    {
        var assets = new[] { Asset("SmartLab-1.1.0-win-x64.zip"), Asset("SHA256SUMS.txt") };

        Assert.Equal("SHA256SUMS.txt", UpdatePackage.SelectChecksums(assets)?.Name);
    }

    [Theory]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  app.zip")]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855 *app.zip")]
    [InlineData("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855  ./app.zip")]
    public void EveryShapeOfChecksumLineIsRead(string line)
    {
        var sums = UpdatePackage.ParseChecksums(line);

        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            sums["app.zip"]);
    }

    [Theory]
    [InlineData("not a checksum at all")]
    [InlineData("abc123  app.zip")]
    [InlineData("zzzzc44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  app.zip")]
    [InlineData("")]
    public void AnythingThatIsNotAChecksumIsDroppedRatherThanGuessedAt(string text)
    {
        // A checksum read wrongly is worse than one that is missing: it still looks
        // like verification.
        Assert.Empty(UpdatePackage.ParseChecksums(text));
    }

    [Fact]
    public void CommentsAndBlankLinesAreIgnored()
    {
        var sums = UpdatePackage.ParseChecksums(
            "# Smart Lab 1.1.0\n\n" +
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  app.zip\n");

        Assert.Single(sums);
    }

    [Fact]
    public void TheHashMatchesWhatWindowsWouldReport()
    {
        var path = Path.Combine(Path.GetTempPath(), $"smartlab-hash-{Guid.NewGuid():N}.bin");

        try
        {
            File.WriteAllText(path, "smart lab");

            using var sha = System.Security.Cryptography.SHA256.Create();
            var expected = Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(path))).ToLowerInvariant();

            Assert.Equal(expected, UpdatePackage.HashOf(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}

/// <summary>The swap, which runs after this process is gone.</summary>
public sealed class UpdateSwapTests
{
    private static string Script() =>
        UpdateInstaller.SwapScript(4242, @"C:\Temp\staged", @"C:\Apps\SmartLab", "SmartLab.App.exe");

    [Fact]
    public void TheScriptWaitsForThisProcessRatherThanGuessingAtASleep()
    {
        // Copying over a running installation fails silently on the locked files, so
        // "wait a few seconds and hope" is the difference between an update and a
        // half-replaced application.
        var script = Script();

        Assert.Contains("PID eq 4242", script, StringComparison.Ordinal);
        Assert.Contains("goto wait", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheScriptCopiesAndNeverMirrors()
    {
        var script = Script();

        Assert.Contains("robocopy", script, StringComparison.OrdinalIgnoreCase);

        // /MIR deletes anything in the destination that is not in the source, which
        // here means whatever the operator keeps beside the application.
        Assert.DoesNotContain("/MIR", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/PURGE", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheScriptRestartsTheApplicationAndCleansUpAfterItself()
    {
        var script = Script();

        Assert.Contains("SmartLab.App.exe", script, StringComparison.Ordinal);
        Assert.Contains(@"rmdir /s /q ""C:\Temp\staged""", script, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryPathInTheScriptIsQuoted()
    {
        // The installation path contains a space on most machines - "Program Files",
        // or a project folder called "USB Doctor".
        var script = UpdateInstaller.SwapScript(
            1, @"C:\Temp\a b\staged", @"C:\Users\me\USB Doctor\app", "SmartLab.App.exe");

        Assert.Contains(@"""C:\Temp\a b\staged""", script, StringComparison.Ordinal);
        Assert.Contains(@"""C:\Users\me\USB Doctor\app""", script, StringComparison.Ordinal);
    }

    [Fact]
    public void APackageWithoutTheExecutableIsNotAnInstallation()
    {
        var staged = Path.Combine(Path.GetTempPath(), $"smartlab-stage-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(Path.Combine(staged, "docs"));
            File.WriteAllText(Path.Combine(staged, "docs", "readme.txt"), "hello");

            Assert.Null(UpdateInstaller.LocateExecutable(staged, "SmartLab.App.exe"));
        }
        finally
        {
            try { Directory.Delete(staged, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheExecutableIsFoundAtTheRootOrOneFolderIn(bool nested)
    {
        // Both shapes exist in the wild: "zip the publish folder" produces one, "zip
        // its contents" the other.
        var staged = Path.Combine(Path.GetTempPath(), $"smartlab-stage-{Guid.NewGuid():N}");
        var root = nested ? Path.Combine(staged, "SmartLab-1.1.0-win-x64") : staged;

        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllBytes(Path.Combine(root, "SmartLab.App.exe"), [0x4D, 0x5A]);

            Assert.Equal(root, UpdateInstaller.LocateExecutable(staged, "SmartLab.App.exe"));
        }
        finally
        {
            try { Directory.Delete(staged, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ReleaseWithoutChecksumsInstallsNothing()
    {
        var package = new ReleaseAsset("SmartLab-9.9.9-win-x64.zip", "https://example.invalid/p.zip", 10);

        var outcome = await UpdateInstaller.InstallAsync(
            package, checksums: null, Path.GetTempPath(), _ => Task.CompletedTask);

        Assert.False(outcome.Started);
        Assert.Contains("SHA256SUMS.txt", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInstallationThatCannotBeWrittenToIsFoundOutBeforeAnythingIsDownloaded()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"smartlab-nowhere-{Guid.NewGuid():N}");

        Assert.False(UpdateInstaller.CanWriteToInstallation(missing, out var reason));
        Assert.NotNull(reason);

        Assert.True(UpdateInstaller.CanWriteToInstallation(Path.GetTempPath(), out _));
    }
}
