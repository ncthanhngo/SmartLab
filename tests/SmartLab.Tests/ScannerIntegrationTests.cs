using SmartLab.Maintenance;
using Xunit;

namespace SmartLab.Tests;

/// <summary>
/// The two scanners that had never met a real folder, run against real folders.
/// </summary>
/// <remarks>
/// <para>
/// Their parsing was tested against captured text, which proves the parser and
/// nothing about the walk that feeds it: whether the cache folder is found under its
/// random name, whether the newest version folder of an extension is the one read,
/// whether a mailbox sitting beside the attachments is skipped. This machine has
/// neither Outlook nor a browser extension, so the shape is built in a temp folder
/// and the scanner pointed at it.
/// </para>
/// <para>
/// A temp tree rather than a mock filesystem: the thing under test is how these
/// scanners behave against directories, and a fake would be testing the fake.
/// </para>
/// </remarks>
public sealed class OutlookCacheIntegrationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"smartlab-outlook-{Guid.NewGuid():N}");

    /// <summary>The real shape: INetCache, then a folder whose name is random per machine.</summary>
    private string CacheFolder
    {
        get
        {
            var path = Path.Combine(_root, "Microsoft", "Windows", "INetCache", "Content.Outlook", "A1B2C3D4");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void TheCacheFolderIsFoundUnderItsRandomName()
    {
        // Hardcoding the leaf would find nothing: the suffix differs on every machine.
        _ = CacheFolder;

        Assert.Single(OutlookCache.FindCacheFolders(_root));
    }

    [Fact]
    public void AMissingCacheYieldsNothingRatherThanThrowing()
    {
        Assert.Empty(OutlookCache.FindCacheFolders(Path.Combine(_root, "nowhere")));
        Assert.Empty(OutlookCache.Scan(Path.Combine(_root, "nowhere")));
    }

    [Fact]
    public void OpenedAttachmentsAreReportedWithTheirSizes()
    {
        var folder = CacheFolder;
        File.WriteAllBytes(Path.Combine(folder, "quote.pdf"), new byte[2048]);
        File.WriteAllBytes(Path.Combine(folder, "drawing.dwg"), new byte[4096]);

        var found = OutlookCache.Scan(_root);

        Assert.Equal(2, found.Count);
        Assert.Equal(6144, found.Sum(a => a.SizeBytes));
    }

    [Fact]
    public void AMailboxSittingInTheCacheIsNeverReported()
    {
        // The rule this feature exists under, tested against a file on disk rather
        // than against a string. An OST is a cache to Outlook but the mailbox to the
        // person whose mail it is.
        var folder = CacheFolder;
        File.WriteAllBytes(Path.Combine(folder, "quote.pdf"), new byte[512]);
        File.WriteAllBytes(Path.Combine(folder, "mailbox.ost"), new byte[999_999]);
        File.WriteAllBytes(Path.Combine(folder, "archive.pst"), new byte[999_999]);

        var found = OutlookCache.Scan(_root);

        Assert.Single(found);
        Assert.Equal("quote.pdf", found[0].Name);
    }

    [Fact]
    public void AttachmentsInSubfoldersAreFound()
    {
        // Outlook nests a folder per attachment when names collide.
        var nested = Path.Combine(CacheFolder, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(nested, "report.docx"), new byte[256]);

        Assert.Single(OutlookCache.Scan(_root));
    }
}

/// <summary>Browser extensions, against a real profile shape.</summary>
public sealed class BrowserExtensionIntegrationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"smartlab-ext-{Guid.NewGuid():N}");

    /// <summary>Chrome's layout: Extensions / id / version / manifest.json.</summary>
    private void Install(string browser, string id, string version, string manifest)
    {
        string[] segments = browser == "Chrome"
            ? ["Google", "Chrome", "User Data", "Default", "Extensions"]
            : ["Microsoft", "Edge", "User Data", "Default", "Extensions"];

        var folder = Path.Combine([_root, .. segments, id, version]);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), manifest);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void AnInstalledExtensionIsFoundAndRead()
    {
        Install("Chrome", "abcdefghijklmnop", "2.1.0_0",
            """{"name":"Some Extension","version":"2.1.0","permissions":["storage"]}""");

        var found = BrowserExtensionScanner.Scan(_root);

        Assert.Single(found);
        Assert.Equal("Some Extension", found[0].Name);
        Assert.Equal("Chrome", found[0].Browser);
    }

    [Fact]
    public void TheNewestVersionFolderIsTheOneRead()
    {
        // An extension keeps every version it has had. Reading an old one reports a
        // version the browser is not running and permissions it may no longer ask for.
        Install("Chrome", "abcdefghijklmnop", "1.0.0_0", """{"name":"Old","version":"1.0.0"}""");
        Install("Chrome", "abcdefghijklmnop", "3.2.0_0", """{"name":"New","version":"3.2.0"}""");

        var found = BrowserExtensionScanner.Scan(_root);

        Assert.Single(found);
        Assert.Equal("3.2.0", found[0].Version);
    }

    [Fact]
    public void BothBrowsersAreWalked()
    {
        Install("Chrome", "aaaaaaaaaaaaaaaa", "1_0", """{"name":"From Chrome"}""");
        Install("Edge", "bbbbbbbbbbbbbbbb", "1_0", """{"name":"From Edge"}""");

        var found = BrowserExtensionScanner.Scan(_root);

        Assert.Equal(2, found.Count);
        Assert.Contains(found, e => e.Browser == "Edge");
    }

    [Fact]
    public void AnExtensionWithNoManifestIsSkippedRatherThanCrashing()
    {
        var folder = Path.Combine(
            _root, "Google", "Chrome", "User Data", "Default", "Extensions", "cccccccccccccccc", "1_0");
        Directory.CreateDirectory(folder);

        Assert.Empty(BrowserExtensionScanner.Scan(_root));
    }

    [Fact]
    public void AnExtensionReachingEverySiteIsFlaggedFromDisk()
    {
        Install("Chrome", "dddddddddddddddd", "1_0",
            """{"name":"Broad","host_permissions":["<all_urls>"]}""");

        Assert.True(BrowserExtensionScanner.Scan(_root)[0].ReadsEverySite);
    }

    [Fact]
    public void AProfileWithNoExtensionsFolderYieldsNothing()
    {
        Assert.Empty(BrowserExtensionScanner.Scan(Path.Combine(_root, "nowhere")));
    }
}
