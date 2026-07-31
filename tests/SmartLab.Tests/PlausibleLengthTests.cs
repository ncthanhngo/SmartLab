using SmartLab.Fat;
using Xunit;

namespace SmartLab.Tests;

/// <summary>
/// The bound on a size read off a damaged volume.
/// </summary>
/// <remarks>
/// Every length the carve receives comes from a directory entry on a volume that is
/// damaged by definition - that is why the entry is being carved. A corrupt size field
/// is the expected input here, not an edge case, and allocating one unbounded turns
/// four bad bytes into an OutOfMemoryException that ends the recovery run.
/// </remarks>
public sealed class PlausibleLengthTests
{
    private const long FourGigabyteStick = 4L * 1024 * 1024 * 1024;

    /// <summary>Small enough that the array ceiling is not what decides the answer.</summary>
    private const long OneGigabyteStick = 1024L * 1024 * 1024;

    [Fact]
    public void AnOrdinaryFileOnTheDeviceIsAllowed()
    {
        Assert.True(RawFileSystem.IsPlausibleLength(2 * 1024 * 1024, FourGigabyteStick));
    }

    [Fact]
    public void AFileFillingTheWholeDeviceIsAllowed()
    {
        // A single-file volume is unusual but not impossible, and refusing it would
        // lose the one thing somebody came to recover.
        Assert.True(RawFileSystem.IsPlausibleLength(OneGigabyteStick, OneGigabyteStick));
    }

    [Fact]
    public void AFileOverTwoGigabytesIsRefusedEvenWhenItFitsTheDevice()
    {
        // A real limit of the carve, not of this check: ReadContiguous builds one
        // byte[], and .NET caps an array below 2 GB. Refusing is what this makes it
        // do instead of throwing OutOfMemoryException part-way through a recovery.
        Assert.False(RawFileSystem.IsPlausibleLength(3L * 1024 * 1024 * 1024, FourGigabyteStick));
    }

    [Fact]
    public void ALengthLargerThanTheDeviceIsRefused()
    {
        Assert.False(RawFileSystem.IsPlausibleLength(OneGigabyteStick + 1, OneGigabyteStick));
    }

    [Fact]
    public void ACorruptSixtyFourBitSizeIsRefused()
    {
        // exFAT stores the size in 64 bits, so a damaged entry can claim more bytes
        // than exist anywhere.
        Assert.False(RawFileSystem.IsPlausibleLength(long.MaxValue, FourGigabyteStick));
    }

    [Fact]
    public void AnythingPastTheMaximumArrayIsRefusedEvenOnAHugeDevice()
    {
        // The allocation cannot succeed regardless of what the device reports, so the
        // device check alone is not enough.
        Assert.False(RawFileSystem.IsPlausibleLength((long)Array.MaxLength + 1, long.MaxValue));
    }

    [Fact]
    public void AnUnknownDeviceLengthStillRefusesTheImpossible()
    {
        // A stream that will not report its length must not become a licence to
        // allocate anything at all.
        Assert.False(RawFileSystem.IsPlausibleLength(long.MaxValue, deviceLength: 0));
        Assert.True(RawFileSystem.IsPlausibleLength(1024, deviceLength: 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void NothingAtOrBelowZeroIsAllowed(long length)
    {
        Assert.False(RawFileSystem.IsPlausibleLength(length, FourGigabyteStick));
    }
}
