using System.Text;
using UsbDoctor.Fat;
using Xunit;

namespace UsbDoctor.Tests;

public class ExFatReaderTests
{
    private static ExFatReader Open(ExFatImageBuilder builder)
    {
        Assert.True(ExFatReader.TryOpen(builder.BuildDeviceStream(), out var reader, out var error), error);
        return reader!;
    }

    [Fact]
    public void Boot_sector_geometry_is_parsed()
    {
        var reader = Open(new ExFatImageBuilder());

        Assert.Equal(512, reader.BootSector.BytesPerSector);
        Assert.Equal(1, reader.BootSector.SectorsPerCluster);
        Assert.Equal(2u, reader.BootSector.RootDirectoryCluster);
        Assert.Contains("exFAT", reader.Describe(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(108, 4)]   // bytes-per-sector shift below 9
    [InlineData(108, 20)]  // above 12
    [InlineData(109, 30)]  // cluster larger than 32 MB
    public void Implausible_geometry_is_rejected(int offset, byte value)
    {
        var image = new ExFatImageBuilder().WithBootSectorByte(offset, value).BuildStream();

        Assert.False(ExFatReader.TryOpen(image, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void A_fat32_volume_is_not_mistaken_for_exfat()
    {
        Assert.False(ExFatReader.TryOpen(new Fat32ImageBuilder().BuildStream(), out _, out var error));
        Assert.Contains("EXFAT", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Entries_and_long_names_are_read()
    {
        var builder = new ExFatImageBuilder()
            .AddEntry(2, "KSCDD200_SampleADesign.kicad_pcb", isDirectory: false, 10, 4096)
            .AddEntry(2, "Simulate", isDirectory: true, 20, 0);

        var entries = Open(builder).EnumerateTree().ToList();

        Assert.Contains(entries, e => e.Name == "KSCDD200_SampleADesign.kicad_pcb" && e.Length == 4096);
        Assert.Contains(entries, e => e is { Name: "Simulate", IsDirectory: true });
    }

    /// <summary>
    /// exFAT keeps the whole name of a deleted file, unlike FAT32 which overwrites
    /// the first character of the 8.3 name. That makes recovery materially better.
    /// </summary>
    [Fact]
    public void A_deleted_entry_keeps_its_full_name_and_location()
    {
        var builder = new ExFatImageBuilder()
            .AddEntry(2, "adobeupdate.dat", isDirectory: false, 30, 160779, deleted: true);

        var entry = Assert.Single(Open(builder).EnumerateTree(), e => e.IsDeleted);

        Assert.Equal("adobeupdate.dat", entry.Name);
        Assert.Equal(30u, entry.FirstCluster);
        Assert.Equal(160779, entry.Length);
    }

    [Fact]
    public void Deleted_entries_can_be_excluded()
    {
        var builder = new ExFatImageBuilder()
            .AddEntry(2, "keep.txt", isDirectory: false, 10, 100)
            .AddEntry(2, "gone.dat", isDirectory: false, 30, 200, deleted: true);

        var entries = Open(builder).EnumerateTree(includeDeleted: false).ToList();

        Assert.Single(entries);
        Assert.Equal("keep.txt", entries[0].Name);
    }

    [Fact]
    public void Subdirectories_are_walked()
    {
        var builder = new ExFatImageBuilder()
            .AddEntry(2, "NHV", isDirectory: true, 3, 0)
            .AddEntry(3, "inner.dat", isDirectory: false, 40, 64);

        builder.EndChain(2);
        builder.EndChain(3);

        var entries = Open(builder).EnumerateTree().ToList();

        Assert.Contains(entries, e => e.Path == @"NHV\inner.dat");
    }

    [Fact]
    public void A_deleted_directory_is_not_descended_into()
    {
        var builder = new ExFatImageBuilder()
            .AddEntry(2, "OLD", isDirectory: true, 3, 0, deleted: true)
            .AddEntry(3, "ghost.dat", isDirectory: false, 40, 64);

        var entries = Open(builder).EnumerateTree().ToList();

        Assert.Contains(entries, e => e.IsDeleted);
        Assert.DoesNotContain(entries, e => e.Name == "ghost.dat");
    }

    [Fact]
    public void Deleted_file_content_is_carved_back_out()
    {
        var content = Encoding.UTF8.GetBytes("payload bytes that outlived the directory entry");

        var builder = new ExFatImageBuilder()
            .AddEntry(2, "gone.bin", isDirectory: false, 40, content.Length, deleted: true)
            .WriteData(40, content);

        var reader = Open(builder);
        var entry = Assert.Single(reader.EnumerateTree(), e => e.IsDeleted);

        var recovered = reader.ReadContiguous(entry.FirstCluster, entry.Length);

        Assert.Equal(content, recovered);
    }

    [Fact]
    public void Recovery_of_an_empty_or_invalid_entry_returns_nothing()
    {
        var reader = Open(new ExFatImageBuilder());

        Assert.Empty(reader.ReadContiguous(0, 100));   // no cluster
        Assert.Empty(reader.ReadContiguous(40, 0));    // no length
    }

    [Fact]
    public void RawFileSystem_picks_the_right_reader()
    {
        Assert.True(RawFileSystem.TryOpen(new ExFatImageBuilder().BuildDeviceStream(), out var exfat, out _));
        Assert.IsType<ExFatReader>(exfat);

        Assert.True(RawFileSystem.TryOpen(new Fat32ImageBuilder().BuildDeviceStream(), out var fat32, out _));
        Assert.IsType<Fat32Reader>(fat32);

        // A device holding neither must say so rather than half-open something.
        var rubbish = new MemoryStream(new byte[1024]);
        Assert.False(RawFileSystem.TryOpen(rubbish, out var none, out var error));
        Assert.Null(none);
        Assert.NotNull(error);
    }
}
