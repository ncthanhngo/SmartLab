using SmartLab.Win32.Devices;
using Xunit;

namespace SmartLab.Tests;

/// <summary>
/// The unit mask decides which drive gets scanned. Getting it wrong means acting
/// on the wrong device, which is not something to find out by plugging things in.
/// </summary>
public class VolumeChangeMessageTests
{
    [Theory]
    [InlineData(0x00000001u, 'A')]
    [InlineData(0x00000004u, 'C')]
    [InlineData(0x00000010u, 'E')]
    [InlineData(0x00000040u, 'G')]
    [InlineData(0x02000000u, 'Z')]
    public void A_single_bit_maps_to_its_drive_letter(uint mask, char expected)
    {
        var letters = VolumeChangeMessage.DecodeUnitMask(mask);

        Assert.Equal(expected, Assert.Single(letters));
    }

    [Fact]
    public void A_multi_partition_device_yields_every_letter()
    {
        // The stick found during development presented two volumes at once.
        var letters = VolumeChangeMessage.DecodeUnitMask(0x00000050u); // E and G

        Assert.Equal(['E', 'G'], letters);
    }

    [Fact]
    public void An_empty_mask_yields_nothing()
    {
        Assert.Empty(VolumeChangeMessage.DecodeUnitMask(0));
    }

    [Fact]
    public void Bits_above_Z_are_ignored()
    {
        // Only 26 letters exist; a stray high bit must not produce a bogus char.
        var letters = VolumeChangeMessage.DecodeUnitMask(0xFC000000u);

        Assert.Empty(letters);
    }

    [Fact]
    public void All_drives_decode_in_order()
    {
        var letters = VolumeChangeMessage.DecodeUnitMask(0x03FFFFFFu);

        Assert.Equal(26, letters.Count);
        Assert.Equal('A', letters[0]);
        Assert.Equal('Z', letters[25]);
    }

    [Fact]
    public void A_message_with_no_payload_is_not_a_volume_event()
    {
        var kind = VolumeChangeMessage.Interpret(0x8000, IntPtr.Zero, out var letters);

        Assert.Equal(VolumeChangeKind.None, kind);
        Assert.Empty(letters);
    }

    [Fact]
    public void An_unrelated_device_message_is_ignored()
    {
        // Device arrivals fire for interfaces, ports and handles too, and those
        // carry a completely different payload.
        var kind = VolumeChangeMessage.Interpret(0x0007, IntPtr.Zero, out _);

        Assert.Equal(VolumeChangeKind.None, kind);
    }
}
