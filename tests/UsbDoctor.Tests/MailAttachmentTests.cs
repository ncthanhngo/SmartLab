using UsbDoctor.App;
using UsbDoctor.Maintenance;
using Xunit;

namespace UsbDoctor.Tests;

/// <summary>
/// The one rule that matters in the Mail Attachments section.
/// </summary>
/// <remarks>
/// "Mail attachments" reads to most people as their mail. The feature reaches exactly
/// one folder - the copies Outlook makes when someone opens an attachment - and must
/// never reach a mailbox. An OST is a cache in Outlook's vocabulary but the mailbox in
/// the user's, and a PST is often the only copy of mail no server still has.
/// </remarks>
public sealed class MailAttachmentTests
{
    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\Microsoft\Outlook\me@work.com.ost")]
    [InlineData(@"C:\Users\me\Documents\Outlook Files\archive.pst")]
    [InlineData(@"C:\Users\me\AppData\Local\Microsoft\Outlook\me.nst")]
    [InlineData(@"C:\anywhere\ARCHIVE.PST")]
    public void MailboxFilesAreNeverOffered(string path)
    {
        Assert.False(OutlookCache.IsSafeToOffer(path));
    }

    [Theory]
    [InlineData(@"C:\cache\Content.Outlook\ABC123\quote.pdf")]
    [InlineData(@"C:\cache\Content.Outlook\ABC123\drawing.dwg")]
    [InlineData(@"C:\cache\Content.Outlook\ABC123\no-extension")]
    public void OpenedAttachmentsAreOffered(string path)
    {
        Assert.True(OutlookCache.IsSafeToOffer(path));
    }

    [Fact]
    public void TheProtectedListCoversEveryMailboxExtension()
    {
        // Named rather than counted, so adding a format is a deliberate edit here
        // rather than something a future scan quietly starts deleting.
        Assert.Contains(".ost", OutlookCache.ProtectedExtensions);
        Assert.Contains(".pst", OutlookCache.ProtectedExtensions);
    }

    [Fact]
    public void TheHeadingSaysWhatIsNotBeingTouched()
    {
        var summary = MailAttachmentsViewModel.Summarise(found: 40, ticked: 40);

        Assert.Contains("mail itself is untouched", summary.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BeforeScanningNothingIsClaimed()
    {
        var summary = MailAttachmentsViewModel.Summarise(found: 0, ticked: 0);

        Assert.Equal("Not scanned yet", summary.Headline);
    }
}

/// <summary>
/// Trash Bins, where the app's purpose and a cleaner's instincts disagree.
/// </summary>
public sealed class TrashBinTests
{
    private static RecycleBinInfo Bin(bool removable, long items = 10) =>
        new(@"E:\", "STICK", Bytes: 1024 * 1024, Items: items, IsRemovable: removable);

    [Fact]
    public void EveryBinStartsUnticked()
    {
        // The single most important default in this section. This tool carves deleted
        // files back off a volume; the bin is where Windows already keeps them intact.
        Assert.False(new TrashBinViewModel(Bin(removable: false)).IsSelected);
        Assert.False(new TrashBinViewModel(Bin(removable: true)).IsSelected);
    }

    [Fact]
    public void ARemovableBinIsCalledOutInTheHeading()
    {
        var summary = TrashBinsViewModel.Summarise(bins: 3, ticked: 1, removable: 1, items: 40);

        Assert.Contains("removable", summary.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithNoRemovableDriveTheExtraWarningIsAbsent()
    {
        var summary = TrashBinsViewModel.Summarise(bins: 2, ticked: 1, removable: 0, items: 40);

        Assert.DoesNotContain("removable", summary.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheHeadingAlwaysSaysEmptyingCannotBeUndone()
    {
        var summary = TrashBinsViewModel.Summarise(bins: 2, ticked: 2, removable: 0, items: 12);

        Assert.Contains("cannot be undone", summary.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnEmptyMachineIsNotReportedAsReadyToEmpty()
    {
        var summary = TrashBinsViewModel.Summarise(bins: 3, ticked: 0, removable: 0, items: 0);

        Assert.Equal("Every bin is empty", summary.Headline);
    }
}
