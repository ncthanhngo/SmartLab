# Phase 02 — Cleanup group completed

Adds Mail Attachments and Trash Bins beside the existing System Junk.

Depends on phase 01.

## Context

- `src/UsbDoctor.Maintenance/JunkCategory.cs` — the catalogue and its rules.
- `src/UsbDoctor.Maintenance/RecycleBin.cs` — already queries and empties bins.
- `src/UsbDoctor.App/CleanupViewModel.cs` — the pattern each new section follows.
- `tests/UsbDoctor.Tests/CleanupTests.cs` — the safety assertions to extend.

## Trash Bins

Promotes the Recycle Bin from one row in Cleanup to its own section, broken down per
drive, because a bin on a removable drive is a different decision from the system
one.

- `SHQueryRecycleBin` per fixed and removable drive gives size and item count.
- The dial counts total items; the ring is bins ticked over bins found.
- **Every bin starts unticked.** This is the one place Windows keeps deleted files,
  and the Deleted files section exists to recover from exactly there. The heading has
  to say so, not the docs.
- Emptying stays a shell call, never a file walk. Deleting `$Recycle.Bin` contents
  directly corrupts the index and loses the restore metadata for what remains.
- Remove the `recycle-bin` row from `JunkCatalogue` so it is not offered twice with
  two different defaults.

## Mail Attachments

The macOS feature clears attachments Mail has downloaded. There is no equivalent
store on Windows, so this maps to Outlook's opened-attachment cache and nothing else.

- Location: `%LOCALAPPDATA%\Microsoft\Windows\INetCache\Content.Outlook\<random>`.
  The folder is per-profile and its name is random, so it is discovered rather than
  hardcoded.
- **`.ost` and `.pst` are never touched and never listed.** An OST is a cache in
  Outlook's vocabulary but the mailbox in the user's, and a PST is often the only
  copy of mail that no longer exists on any server. A test asserts both extensions
  are absent from every path this feature reports.
- Attachments still open in Outlook are locked and are skipped and counted, as
  Cleanup already does for temp files.
- If Outlook is running, the section says so rather than silently under-reporting.

## Files

| Action | Path |
| --- | --- |
| create | `src/UsbDoctor.Maintenance/OutlookCache.cs` |
| create | `src/UsbDoctor.App/MailAttachmentsViewModel.cs` |
| create | `src/UsbDoctor.App/TrashBinsViewModel.cs` |
| modify | `src/UsbDoctor.Maintenance/RecycleBin.cs` — per-drive enumeration |
| modify | `src/UsbDoctor.Maintenance/JunkCategory.cs` — drop `recycle-bin` |
| modify | `src/UsbDoctor.App/MainWindow.xaml` — two stages |
| modify | `src/UsbDoctor.Cli/Program.cs` — read-only reporting for both |
| modify | `tests/UsbDoctor.Tests/CleanupTests.cs` |
| create | `tests/UsbDoctor.Tests/MailAttachmentTests.cs` |

## Steps

1. Extend `RecycleBin` to enumerate per drive rather than aggregate.
2. `TrashBinsViewModel` over that, all rows unticked, emptying through the shell.
3. `OutlookCache` discovers the `Content.Outlook` subfolder and measures it,
   excluding `.ost` and `.pst` by extension at the source, not at display time.
4. `MailAttachmentsViewModel` following `CleanupViewModel`'s shape.
5. Two stages in `MainWindow.xaml`, dial plus demoted list.
6. Drop `recycle-bin` from the catalogue and fix `CleanupTests` accordingly.
7. CLI subcommands reporting both, read-only.

## Tests

- No path reported by Mail Attachments ends in `.ost` or `.pst`.
- Every Recycle Bin row starts unticked, and the caution names recovery as the reason.
- `JunkCatalogue` no longer contains `recycle-bin`, so it cannot be offered twice.
- The existing "every category with a caution starts unticked" assertion covers both
  new catalogues.

## Risks and rollback

Emptying a bin is irreversible and this section makes it one press closer than
before. That is the entire reason for unticked-by-default and the explicit heading.

Removing `recycle-bin` from the catalogue changes what Cleanup shows. Anyone relying
on the CLI's `clean` output will see one fewer row; note it in the README rather than
leaving it to be discovered.
