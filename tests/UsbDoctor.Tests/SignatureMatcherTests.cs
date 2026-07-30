using System.Text;
using UsbDoctor.Core.Model;
using UsbDoctor.Core.Paths;
using UsbDoctor.Signatures;
using Xunit;

namespace UsbDoctor.Tests;

public class SignatureMatcherTests
{
    private static readonly SignatureMatcher Matcher = new(SignatureSet.LoadBuiltIn());

    private static FileEntry File(string name, EntryAttributes attributes = EntryAttributes.Archive) =>
        new(ExtendedPath.From(@"E:\").Child(name), name, 100, attributes, null);

    private static FileEntry Directory(string name, EntryAttributes attributes) =>
        new(ExtendedPath.From(@"E:\").Child(name), name, 0,
            attributes | EntryAttributes.Directory, null);

    private static Func<Stream> Content(string text) =>
        () => new MemoryStream(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Built_in_signatures_load()
    {
        var set = SignatureSet.LoadBuiltIn();
        Assert.NotEmpty(set.Signatures);
        Assert.All(set.Signatures, s => Assert.NotEmpty(s.AnyOf));
    }

    [Fact]
    public void Fake_recycler_folder_is_flagged()
    {
        var entry = Directory("RECYCLER.BIN", EntryAttributes.Hidden | EntryAttributes.System);

        var hits = Matcher.Match(entry, isRoot: true, openContent: null);

        Assert.Contains(hits, h => h.SignatureId == "fake-recycler-bin");
        Assert.All(hits, h => Assert.True(h.IsDirectory));
    }

    [Fact]
    public void A_real_recycle_bin_is_not_flagged()
    {
        var entry = Directory("$RECYCLE.BIN", EntryAttributes.Hidden | EntryAttributes.System);

        var hits = Matcher.Match(entry, isRoot: true, openContent: null);

        Assert.DoesNotContain(hits, h => h.SignatureId == "fake-recycler-bin");
    }

    [Fact]
    public void Recycle_bin_clsid_in_desktop_ini_is_flagged()
    {
        var entry = File("desktop.ini", EntryAttributes.Hidden | EntryAttributes.ReadOnly);
        var content = Content("[.ShellClassInfo]\r\nCLSID={645FF040-5081-101B-9F08-00AA002F954E}\r\n");

        var hits = Matcher.Match(entry, isRoot: false, content);

        Assert.Contains(hits, h => h.SignatureId == "recycle-bin-clsid-disguise");
    }

    [Fact]
    public void An_ordinary_desktop_ini_is_not_flagged()
    {
        var entry = File("desktop.ini", EntryAttributes.Hidden | EntryAttributes.ReadOnly);
        var content = Content("[.ShellClassInfo]\r\nIconResource=%systemroot%\\system32\\SHELL32.dll,7\r\n");

        var hits = Matcher.Match(entry, isRoot: false, content);

        Assert.DoesNotContain(hits, h => h.SignatureId == "recycle-bin-clsid-disguise");
    }

    /// <summary>
    /// Regression from a live scan on 2026-07-30: a legitimate NHV BOOT rescue
    /// stick was flagged purely because an autorun.inf existed at its root. Bootable
    /// and branded sticks ship one carrying only Icon and Label, so presence alone
    /// is not evidence.
    /// </summary>
    [Fact]
    public void A_branding_only_autorun_is_not_flagged()
    {
        var entry = File("Autorun.inf");
        var content = Content("[Autorun]\r\nIcon=Boot.ico\r\nLabel=NHV-BOOT\r\n");

        var hits = Matcher.Match(entry, isRoot: true, content);

        Assert.DoesNotContain(hits, h => h.SignatureId.StartsWith("autorun", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("[Autorun]\r\nopen=setup.exe\r\n")]
    [InlineData("[Autorun]\r\nShellExecute=payload.exe\r\n")]
    public void An_autorun_that_launches_a_program_is_flagged(string text)
    {
        var entry = File("autorun.inf");

        var hits = Matcher.Match(entry, isRoot: true, Content(text));

        Assert.Contains(hits, h => h.SignatureId == "autorun-inf-launcher");
    }

    [Fact]
    public void Root_only_rules_do_not_fire_in_subdirectories()
    {
        var entry = new FileEntry(
            ExtendedPath.From(@"E:\NHV\autorun.inf"), "autorun.inf", 100, EntryAttributes.Archive, null);

        var hits = Matcher.Match(entry, isRoot: false, Content("[Autorun]\r\nopen=setup.exe\r\n"));

        Assert.DoesNotContain(hits, h => h.SignatureId == "autorun-inf-launcher");
    }

    [Fact]
    public void Known_payload_hash_is_matched_and_recorded()
    {
        // SHA-256 of the byte "A" is not a real payload hash; drive the check with
        // a signature-supplied value instead so the test stays independent of the
        // built-in hash list.
        var set = SignatureSet.Parse("""
            {
              "schemaVersion": 1,
              "signatures": [{
                "id": "test-hash",
                "description": "test",
                "severity": "Critical",
                "action": "Quarantine",
                "anyOf": [{ "type": "sha256",
                  "values": ["559AEAD08264D5795D3909718CDD05ABD49572E84FE55590EEF31A88A08FDFFD"] }]
              }]
            }
            """);

        var matcher = new SignatureMatcher(set);
        var hits = matcher.Match(File("payload.bin"), isRoot: false, Content("A"));

        var hit = Assert.Single(hits);
        Assert.Equal("test-hash", hit.SignatureId);
        Assert.Equal("559AEAD08264D5795D3909718CDD05ABD49572E84FE55590EEF31A88A08FDFFD", hit.Sha256);
    }

    [Fact]
    public void Unreadable_content_is_not_a_match()
    {
        var hits = Matcher.Match(
            File("desktop.ini"), isRoot: false,
            openContent: () => throw new IOException("locked by Defender"));

        Assert.DoesNotContain(hits, h => h.SignatureId == "recycle-bin-clsid-disguise");
    }
}
