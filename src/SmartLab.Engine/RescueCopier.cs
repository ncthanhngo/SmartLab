using SmartLab.Core.Abstractions;
using SmartLab.Core.Model;
using SmartLab.Core.Naming;
using SmartLab.Core.Paths;

namespace SmartLab.Engine;

public sealed record RescueProgress(int FilesCopied, long BytesCopied, string CurrentPath);

public sealed record RescueFailure(string Source, int Win32Error, string Message);

/// <param name="OriginalPath">Source path, exactly as read from the damaged volume.</param>
/// <param name="StoredAs">Path actually created on the destination.</param>
public sealed record RenameRecord(string OriginalPath, string StoredAs);

public sealed record RescueReport(
    int FilesCopied,
    long BytesCopied,
    int DirectoriesCreated,
    IReadOnlyList<RescueFailure> Failures,
    IReadOnlyList<RenameRecord> Renames)
{
    public bool AnyFailures => Failures.Count > 0;
}

/// <summary>
/// Copies a tree off a damaged volume, tolerating per-entry failures.
/// </summary>
/// <remarks>
/// <para>
/// The design goal is that no single bad entry can stop the rescue. A file that
/// cannot be read, a name NTFS refuses, a directory that vanishes mid-walk — each
/// costs exactly that entry and is recorded in the report. On the volume this
/// tool was built from, two files were physically unreadable and an entire
/// subtree held names made of arbitrary bytes; a copier that aborted on any of
/// them would have rescued nothing.
/// </para>
/// <para>
/// Iterative rather than recursive: a corrupt filesystem can present a directory
/// graph with far more depth than it should have, and recursion would meet the
/// stack limit before the walk finished.
/// </para>
/// </remarks>
public sealed class RescueCopier(IVolumeReader reader, IWriteGate gate, IJournal journal)
{
    public async Task<RescueReport> CopyTreeAsync(
        ExtendedPath source,
        ExtendedPath destination,
        IProgress<RescueProgress>? progress = null,
        CancellationToken ct = default)
    {
        var failures = new List<RescueFailure>();
        var renames = new List<RenameRecord>();
        var files = 0;
        var directories = 0;
        long bytes = 0;

        var root = await gate.CreateDirectoryAsync(destination, ct).ConfigureAwait(false);
        if (!root.Succeeded)
        {
            failures.Add(new RescueFailure(destination.ForDisplay(), root.Win32Error,
                root.Message ?? "Could not create the rescue destination."));
            return new RescueReport(0, 0, 0, failures, renames);
        }

        directories++;

        var pending = new Stack<(ExtendedPath Source, ExtendedPath Destination)>();
        pending.Push((source, destination));

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (currentSource, currentDestination) = pending.Pop();

            // Names only have to be unique within one directory, so the sanitiser
            // is scoped to this directory. A process-wide one would append
            // suffixes to unrelated files that merely share a name.
            var sanitizer = new NameSanitizer();

            await foreach (var item in reader.EnumerateAsync(currentSource, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();

                if (item is EnumEntry.Damaged damaged)
                {
                    failures.Add(new RescueFailure(
                        damaged.Parent.ForDisplay() + "\\" + (damaged.RawName ?? "?"),
                        damaged.Win32Error, damaged.Message));
                    continue;
                }

                if (item is not EnumEntry.Ok ok) continue;
                var entry = ok.Entry;

                if (entry.Attributes.HasFlag(EntryAttributes.ReparsePoint)) continue;

                var safe = sanitizer.Sanitize(entry.Name);
                var target = currentDestination.Child(safe.Safe);

                if (safe.WasChanged)
                {
                    renames.Add(new RenameRecord(entry.Path.ForDisplay(), target.ForDisplay()));
                }

                if (entry.IsDirectory)
                {
                    var created = await gate.CreateDirectoryAsync(target, ct).ConfigureAwait(false);
                    if (created.Succeeded)
                    {
                        directories++;
                        pending.Push((entry.Path, target));
                    }
                    else
                    {
                        // Losing one directory must not lose its siblings, so the
                        // subtree is skipped rather than the walk abandoned.
                        failures.Add(new RescueFailure(
                            entry.Path.ForDisplay(), created.Win32Error,
                            created.Message ?? "Could not create destination directory."));
                    }
                    continue;
                }

                var copied = await gate.CopyFileAsync(entry.Path, target, null, ct).ConfigureAwait(false);
                if (copied.Succeeded)
                {
                    files++;
                    bytes += entry.Length;
                    progress?.Report(new RescueProgress(files, bytes, entry.Path.ForDisplay()));
                }
                else
                {
                    failures.Add(new RescueFailure(
                        entry.Path.ForDisplay(), copied.Win32Error,
                        copied.Message ?? "Copy failed."));
                }
            }
        }

        await journal.AppendAsync(
            JournalRecord.For("rescue-complete", destination.ForDisplay(), failures.Count == 0,
                $"{files} file(s), {bytes} byte(s), {directories} dir(s), " +
                $"{failures.Count} failure(s), {renames.Count} rename(s)"),
            ct).ConfigureAwait(false);

        return new RescueReport(files, bytes, directories, failures, renames);
    }
}
