using UsbDoctor.Core.Model;
using UsbDoctor.Core.Paths;
using UsbDoctor.Engine;
using Xunit;

namespace UsbDoctor.Tests;

public class RescueCopierTests
{
    private static readonly string Nbsp = ((char)0x00A0).ToString();

    private static RescueCopier Copier(FakeFileSystem fs) =>
        new(fs, fs, new RecordingJournal());

    private static ExtendedPath Dest => ExtendedPath.From(@"C:\rescue");

    private static FakeFileSystem WithDestinationRoot()
    {
        var fs = new FakeFileSystem();
        fs.AddDirectory(@"C:\");
        return fs;
    }

    [Fact]
    public async Task Copies_a_nested_tree()
    {
        var fs = WithDestinationRoot()
            .AddDirectory(@"E:\Boot")
            .AddDirectory(@"E:\Boot\Fonts")
            .AddFile(@"E:\Boot\bcd", "boot config")
            .AddFile(@"E:\Boot\Fonts\seg.ttf", "font")
            .AddFile(@"E:\Grldr", "loader");

        var report = await Copier(fs).CopyTreeAsync(ExtendedPath.From(@"E:\"), Dest, null, default);

        Assert.Equal(3, report.FilesCopied);
        Assert.False(report.AnyFailures);
        Assert.True(fs.Exists(@"C:\rescue\Boot\bcd"));
        Assert.True(fs.Exists(@"C:\rescue\Boot\Fonts\seg.ttf"));
        Assert.True(fs.Exists(@"C:\rescue\Grldr"));
    }

    [Fact]
    public async Task An_unreadable_file_costs_only_that_file()
    {
        var fs = WithDestinationRoot()
            .AddFile(@"E:\good1.txt", "a")
            .AddFile(@"E:\broken.aedt", "x")
            .AddFile(@"E:\good2.txt", "b");

        fs.UnreadableFiles.Add(ExtendedPath.From(@"E:\broken.aedt").Value);

        var report = await Copier(fs).CopyTreeAsync(ExtendedPath.From(@"E:\"), Dest, null, default);

        // The whole point: the two readable siblings still arrive.
        Assert.Equal(2, report.FilesCopied);
        Assert.Single(report.Failures);
        Assert.True(fs.Exists(@"C:\rescue\good1.txt"));
        Assert.True(fs.Exists(@"C:\rescue\good2.txt"));
        Assert.False(fs.Exists(@"C:\rescue\broken.aedt"));
    }

    [Fact]
    public async Task A_corrupt_directory_entry_is_recorded_and_the_walk_continues()
    {
        var fs = WithDestinationRoot().AddFile(@"E:\keep.txt", "data");
        fs.DamagedChildren[ExtendedPath.From(@"E:\").Value] = "\u0001garbage\u0002";

        var report = await Copier(fs).CopyTreeAsync(ExtendedPath.From(@"E:\"), Dest, null, default);

        Assert.Equal(1, report.FilesCopied);
        Assert.Single(report.Failures);
        Assert.Equal(1392, report.Failures[0].Win32Error);
        Assert.True(fs.Exists(@"C:\rescue\keep.txt"));
    }

    [Fact]
    public async Task An_invisible_folder_name_is_sanitised_and_recorded()
    {
        var fs = WithDestinationRoot();
        var hidden = ExtendedPath.From(@"E:\").Child(Nbsp);
        fs.AddRawDirectory(hidden, EntryAttributes.Hidden | EntryAttributes.System);
        fs.AddRawFile(hidden.Child("payload.dat"), "content");

        var report = await Copier(fs).CopyTreeAsync(ExtendedPath.From(@"E:\"), Dest, null, default);

        Assert.Equal(1, report.FilesCopied);

        // The rename must be reported, otherwise the operator cannot map what
        // landed on the destination back to what was on the device.
        var rename = Assert.Single(report.Renames);
        Assert.Contains("_blank", rename.StoredAs, StringComparison.Ordinal);
        Assert.True(fs.Exists(@"C:\rescue\_blank\payload.dat"));
    }

    [Fact]
    public async Task Two_names_that_sanitise_alike_do_not_overwrite_each_other()
    {
        var fs = WithDestinationRoot();
        var root = ExtendedPath.From(@"E:\");
        fs.AddRawFile(root.Child("a\u0001b.txt"), "first");
        fs.AddRawFile(root.Child("a\u0002b.txt"), "second");

        var report = await Copier(fs).CopyTreeAsync(root, Dest, null, default);

        Assert.Equal(2, report.FilesCopied);
        Assert.Equal(2, report.Renames.Count);

        // Both must survive; collapsing them onto one destination name would
        // silently destroy data.
        var copied = fs.Nodes.Keys.Count(k =>
            k.StartsWith(@"\\?\C:\rescue\", StringComparison.OrdinalIgnoreCase) &&
            k.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(2, copied);
    }

    [Fact]
    public async Task Reports_nothing_copied_when_the_destination_cannot_be_created()
    {
        // No C:\ root, so the destination's parent does not exist.
        var fs = new FakeFileSystem().AddFile(@"E:\data.txt", "x");

        var report = await Copier(fs).CopyTreeAsync(ExtendedPath.From(@"E:\"), Dest, null, default);

        Assert.Equal(0, report.FilesCopied);
        Assert.Single(report.Failures);
    }

    [Fact]
    public async Task Progress_is_reported_per_file()
    {
        var fs = WithDestinationRoot()
            .AddFile(@"E:\one.txt", "1")
            .AddFile(@"E:\two.txt", "2");

        var seen = new List<int>();
        var progress = new Progress<RescueProgress>(p => seen.Add(p.FilesCopied));

        var report = await Copier(fs).CopyTreeAsync(ExtendedPath.From(@"E:\"), Dest, progress, default);

        Assert.Equal(2, report.FilesCopied);
        await Task.Delay(50); // Progress<T> posts asynchronously
        Assert.NotEmpty(seen);
    }
}
