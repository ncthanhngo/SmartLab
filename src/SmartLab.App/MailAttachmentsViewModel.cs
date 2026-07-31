using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartLab.Maintenance;

namespace SmartLab.App;

/// <summary>One cached attachment, with the operator's decision attached.</summary>
public sealed partial class AttachmentViewModel(CachedAttachment attachment) : ObservableObject
{
    public CachedAttachment Attachment { get; } = attachment;

    public string Name => Attachment.Name;
    public string Path => Attachment.Path;
    public string SizeText => Attachment.SizeText;
    public long SizeBytes => Attachment.SizeBytes;

    public string Age
    {
        get
        {
            var days = (int)(DateTime.Now - Attachment.LastWritten).TotalDays;

            return days switch
            {
                < 1 => "today",
                < 30 => $"{days} days ago",
                < 365 => $"{days / 30} month(s) ago",
                _ => $"{days / 365} year(s) ago",
            };
        }
    }

    /// <summary>
    /// Ticked by default, which is unusual in this app and deliberate here.
    /// </summary>
    /// <remarks>
    /// These are copies. The attachment still exists in the mail item it came from,
    /// and Outlook rewrites this file the next time anyone opens it. That makes the
    /// folder the rare case where the content genuinely is disposable - which is the
    /// same test every entry in the junk catalogue has to pass.
    /// </remarks>
    [ObservableProperty]
    private bool _isSelected = true;
}

public sealed partial class MailAttachmentsViewModel : ObservableObject
{
    public ObservableCollection<AttachmentViewModel> Attachments { get; } = [];

    [ObservableProperty] private bool _isBusy;

    /// <summary>Writing is opt-in, as everywhere else in this app.</summary>
    [ObservableProperty] private bool _dryRun = true;

    [ObservableProperty] private string _status =
        "Scan to measure what opening attachments has left behind. Your mail is never touched.";

    [ObservableProperty] private int _fileCount;
    [ObservableProperty] private double _gaugePercent;
    [ObservableProperty] private string _totalText = "--";
    [ObservableProperty] private string _headline = "Not scanned yet";

    [ObservableProperty] private string _headlineDetail =
        "Reads only the folder Outlook copies an attachment into when you open it. " +
        "Mailbox files are never listed.";

    [RelayCommand]
    private async Task ScanAsync()
    {
        IsBusy = true;
        Attachments.Clear();

        try
        {
            Status = "Reading the attachment cache...";

            // Wrapped in a lambda rather than passed as a method group: the scanner
            // takes an optional root for tests, which makes the group ambiguous.
            var found = await Task.Run(() => OutlookCache.Scan()).ConfigureAwait(true);

            foreach (var attachment in found)
            {
                var row = new AttachmentViewModel(attachment);

                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(AttachmentViewModel.IsSelected)) UpdateSummary();
                };

                Attachments.Add(row);
            }

            UpdateSummary();

            Status = found.Count == 0
                ? "Nothing cached. Either Outlook is not installed, or no attachment has been opened."
                : $"{found.Count} cached attachment(s), {TotalText}. Nothing has been deleted.";
        }
        catch (Exception ex)
        {
            Status = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanClean() => Attachments.Count > 0 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanClean))]
    private async Task CleanAsync()
    {
        var chosen = Attachments.Where(a => a.IsSelected).ToArray();
        if (chosen.Length == 0)
        {
            Status = "Nothing ticked.";
            return;
        }

        if (DryRun)
        {
            Status = $"Dry run: {chosen.Length} file(s) totalling {TotalText} would be deleted. " +
                     "Untick 'Dry run' to apply.";
            return;
        }

        IsBusy = true;

        try
        {
            var (deleted, locked) = await Task.Run(() => Delete(chosen)).ConfigureAwait(true);

            await ScanAsync().ConfigureAwait(true);

            // Locked files are normal on a live machine, so the reason is reported
            // rather than left to read as a failure.
            Status = locked == 0
                ? $"{deleted} file(s) deleted."
                : $"{deleted} deleted, {locked} still in use" +
                  (OutlookCache.IsOutlookRunning() ? " - Outlook is running." : ".");
        }
        catch (Exception ex)
        {
            Status = $"Clean failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static (int Deleted, int Locked) Delete(IReadOnlyList<AttachmentViewModel> chosen)
    {
        int deleted = 0, locked = 0;

        foreach (var row in chosen)
        {
            // Checked again at the point of deletion rather than trusted from the
            // scan: this is the guard that keeps a mailbox out of reach, and it costs
            // one string comparison.
            if (!OutlookCache.IsSafeToOffer(row.Path)) continue;

            try
            {
                File.Delete(row.Path);
                deleted++;
            }
            catch
            {
                locked++;
            }
        }

        return (deleted, locked);
    }

    private void UpdateSummary()
    {
        FileCount = Attachments.Count;

        var measured = Attachments.Sum(a => a.SizeBytes);
        var ticked = Attachments.Where(a => a.IsSelected).Sum(a => a.SizeBytes);

        GaugePercent = measured > 0 ? (double)ticked / measured : 0;

        TotalText = ticked switch
        {
            0 => "0 MB",
            < 1024L * 1024 * 1024 => $"{ticked / 1024.0 / 1024:F0} MB",
            _ => $"{ticked / 1024.0 / 1024 / 1024:F2} GB",
        };

        (Headline, HeadlineDetail) = Summarise(FileCount, Attachments.Count(a => a.IsSelected));

        CleanCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The heading above the dial.</summary>
    /// <remarks>
    /// Says what is not being touched, every time. "Mail attachments" reads to most
    /// people as their mail, and the one thing this section must never be mistaken
    /// for is something that deletes it.
    /// </remarks>
    public static (string Headline, string Detail) Summarise(int found, int ticked)
    {
        if (found == 0)
        {
            return ("Not scanned yet",
                "Reads only the folder Outlook copies an attachment into when you open it. " +
                "Mailbox files are never listed.");
        }

        return (ticked == 0 ? "Nothing ticked" : "Ready to clean",
            $"{ticked} of {found} cached cop(ies) ticked. These are copies Outlook made when " +
            "an attachment was opened - the mail itself is untouched and the file comes back " +
            "the next time you open it.");
    }

    partial void OnIsBusyChanged(bool value) => CleanCommand.NotifyCanExecuteChanged();
}
