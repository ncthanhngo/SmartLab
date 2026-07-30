using UsbDoctor.Fat;
using Xunit;

namespace UsbDoctor.Tests;

public class Fat32ReaderTests
{
    private static Fat32Reader Open(Fat32ImageBuilder builder)
    {
        Assert.True(Fat32Reader.TryOpen(builder.BuildStream(), out var reader, out var error), error);
        return reader!;
    }

    [Fact]
    public void Boot_sector_geometry_is_parsed()
    {
        var reader = Open(new Fat32ImageBuilder());

        Assert.Equal(512, reader.BootSector.BytesPerSector);
        Assert.Equal(1, reader.BootSector.SectorsPerCluster);
        Assert.Equal(2u, reader.BootSector.RootCluster);
        Assert.Equal(64u, reader.BootSector.FirstDataSector);
    }

    [Theory]
    [InlineData(13, 0)]    // sectors-per-cluster of zero
    [InlineData(13, 3)]    // not a power of two
    [InlineData(14, 0)]    // no reserved sectors
    public void Implausible_geometry_is_rejected(int offset, byte value)
    {
        var image = new Fat32ImageBuilder().WithBootSectorByte(offset, value).BuildStream();

        // Accepting a corrupt BPB would send every later cluster calculation to an
        // arbitrary offset on a real device.
        Assert.False(Fat32Reader.TryOpen(image, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void A_non_fat32_volume_is_rejected()
    {
        var image = new Fat32ImageBuilder().WithBootSectorByte(82, (byte)'N').BuildStream();

        Assert.False(Fat32Reader.TryOpen(image, out _, out var error));
        Assert.Contains("FAT32", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Root_directory_entries_are_read()
    {
        var builder = new Fat32ImageBuilder()
            .AddFile(2, "GRLDR", FatAttributes.Archive, 10, 400_000)
            .AddFile(2, "BOOT", FatAttributes.Directory, 11, 0);

        builder.EndChain(2);

        var entries = Open(builder).ReadRootDirectory();

        Assert.Equal(2, entries.Count);
        Assert.Equal("GRLDR", entries[0].Name);
        Assert.Equal(400_000u, entries[0].Length);
        Assert.True(entries[1].IsDirectory);
    }

    [Fact]
    public void Long_names_are_reassembled()
    {
        var builder = new Fat32ImageBuilder()
            .AddLongNamedFile(2, "MBA_LDB_SR_3D_noiday.aedt", "MBA_LD~1.AED", 12, 1234);

        builder.EndChain(2);

        var entry = Assert.Single(Open(builder).ReadRootDirectory());

        Assert.Equal("MBA_LDB_SR_3D_noiday.aedt", entry.Name);
        Assert.Equal("MBA_LD~1.AED", entry.ShortName);
    }

    /// <summary>
    /// The reason this reader exists. A deleted entry is invisible to the mounted
    /// filesystem and is discarded by chkdsk /F, but its name and starting cluster
    /// usually survive - often enough to get the file back.
    /// </summary>
    [Fact]
    public void Deleted_entries_are_surfaced_with_their_starting_cluster()
    {
        var builder = new Fat32ImageBuilder()
            .AddFile(2, "KEEP.TXT", FatAttributes.Archive, 10, 100)
            .AddFile(2, "GONE.DAT", FatAttributes.Archive, 20, 5000, deleted: true);

        builder.EndChain(2);

        var entries = Open(builder).ReadRootDirectory();

        var deleted = Assert.Single(entries, e => e.IsDeleted);
        Assert.Equal(20u, deleted.FirstCluster);
        Assert.Equal(5000u, deleted.Length);

        // The 0xE5 marker overwrote the first character, so it is shown as lost
        // rather than guessed at.
        Assert.StartsWith("_", deleted.Name, StringComparison.Ordinal);
        Assert.EndsWith(".DAT", deleted.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void Deleted_entries_can_be_excluded()
    {
        var builder = new Fat32ImageBuilder()
            .AddFile(2, "KEEP.TXT", FatAttributes.Archive, 10, 100)
            .AddFile(2, "GONE.DAT", FatAttributes.Archive, 20, 5000, deleted: true);

        builder.EndChain(2);

        var entries = Open(builder).ReadRootDirectory(includeDeleted: false);

        Assert.Single(entries);
        Assert.Equal("KEEP.TXT", entries[0].Name);
    }

    [Fact]
    public void A_name_made_of_arbitrary_bytes_is_preserved_not_dropped()
    {
        // The condition found inside the .sd folders on the damaged drive: the
        // entry is real, the name is garbage. It must still be reported so the
        // operator can see the extent of the corruption.
        byte[] garbage = [0x38, 0xCF, 0x44, 0xC1, 0x26, 0x52, 0xE7, 0x4A, 0xE5, 0xD4, 0x6A];

        var builder = new Fat32ImageBuilder().AddRawNamedFile(2, garbage, 30);
        builder.EndChain(2);

        var entry = Assert.Single(Open(builder).ReadRootDirectory());

        Assert.NotEmpty(entry.Name);
        Assert.Equal(30u, entry.FirstCluster);
    }

    [Fact]
    public void Cluster_chains_are_followed()
    {
        var builder = new Fat32ImageBuilder();
        builder.SetFatEntry(10, 11).SetFatEntry(11, 12).EndChain(12);

        var chain = Open(builder).GetChain(10);

        Assert.Equal([10u, 11u, 12u], chain);
    }

    [Fact]
    public void A_chain_that_loops_terminates()
    {
        // Cross-linked and circular chains are exactly what was found on the
        // source volume. An unguarded walk here would hang the tool.
        var builder = new Fat32ImageBuilder();
        builder.SetFatEntry(10, 11).SetFatEntry(11, 12).SetFatEntry(12, 10);

        var chain = Open(builder).GetChain(10);

        Assert.Equal(3, chain.Count);
        Assert.Equal([10u, 11u, 12u], chain);
    }

    [Fact]
    public void The_tree_walk_descends_into_subdirectories()
    {
        var builder = new Fat32ImageBuilder()
            .AddFile(2, "ROOT.TXT", FatAttributes.Archive, 20, 10)
            .AddFile(2, "NHV", FatAttributes.Directory, 3, 0)
            .AddFile(3, "INNER.DAT", FatAttributes.Archive, 21, 20);

        builder.EndChain(2);
        builder.EndChain(3);

        var found = Open(builder).EnumerateTree().ToList();

        Assert.Contains(found, e => e.Path == "ROOT.TXT");
        Assert.Contains(found, e => e.Path == @"NHV\INNER.DAT");
    }

    [Fact]
    public void The_tree_walk_does_not_descend_into_a_deleted_directory()
    {
        // Its clusters may already belong to another file, so descending would
        // report unrelated data under a name that no longer owns it.
        var builder = new Fat32ImageBuilder()
            .AddFile(2, "OLD", FatAttributes.Directory, 3, 0, deleted: true)
            .AddFile(3, "GHOST.DAT", FatAttributes.Archive, 21, 20);

        builder.EndChain(2);
        builder.EndChain(3);

        var found = Open(builder).EnumerateTree().ToList();

        Assert.Contains(found, e => e.Entry.IsDeleted);
        Assert.DoesNotContain(found, e => e.Path.Contains("GHOST", StringComparison.Ordinal));
    }

    /// <summary>
    /// Regression: the first run against real hardware failed with
    /// ERROR_INVALID_PARAMETER because FAT entries were read 4 bytes at a time at
    /// their natural offset. A device permits only sector-aligned reads; a
    /// MemoryStream does not care, so the whole suite passed while the reader
    /// could not read a single real volume.
    /// </summary>
    [Fact]
    public void Everything_works_through_a_device_that_demands_sector_alignment()
    {
        var builder = new Fat32ImageBuilder()
            .AddFile(2, "GRLDR", FatAttributes.Archive, 10, 400_000)
            .AddFile(2, "NHV", FatAttributes.Directory, 3, 0)
            .AddFile(2, "GONE.DAT", FatAttributes.Archive, 20, 5000, deleted: true)
            .AddFile(3, "INNER.DAT", FatAttributes.Archive, 21, 20);

        builder.EndChain(2);
        builder.EndChain(3);
        builder.SetFatEntry(10, 11).EndChain(11);

        using var device = builder.BuildDeviceStream();

        Assert.True(Fat32Reader.TryOpen(device, out var reader, out var error), error);

        var found = reader!.EnumerateTree().ToList();

        Assert.Contains(found, e => e.Path == "GRLDR");
        Assert.Contains(found, e => e.Path == @"NHV\INNER.DAT");
        Assert.Contains(found, e => e.Entry.IsDeleted);

        // Chain following is where the unaligned reads were.
        Assert.Equal([10u, 11u], reader.GetChain(10));
    }

    [Fact]
    public void An_unaligned_read_against_the_device_stream_really_does_throw()
    {
        // Guards the guard: if the fake stopped enforcing alignment, the
        // regression test above would silently stop testing anything.
        using var device = new Fat32ImageBuilder().BuildDeviceStream();
        device.Seek(3, SeekOrigin.Begin);

        Assert.Throws<IOException>(() => device.Read(new byte[512], 0, 512));
    }

    [Fact]
    public void Directory_cycles_do_not_hang_the_walk()
    {
        var builder = new Fat32ImageBuilder()
            .AddFile(2, "SUB", FatAttributes.Directory, 3, 0)
            .AddFile(3, "BACK", FatAttributes.Directory, 2, 0);

        builder.EndChain(2);
        builder.EndChain(3);

        var found = Open(builder).EnumerateTree().ToList();

        Assert.NotEmpty(found);
    }
}
