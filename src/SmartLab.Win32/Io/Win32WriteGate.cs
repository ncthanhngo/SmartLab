using System.IO;
using System.Runtime.InteropServices;
using SmartLab.Core.Abstractions;
using SmartLab.Core.Model;
using SmartLab.Core.Paths;
using static SmartLab.Win32.Native.NativeMethods;

namespace SmartLab.Win32.Io;

/// <summary>
/// The only component in the application permitted to mutate a filesystem.
/// </summary>
public sealed class Win32WriteGate(IJournal journal, bool dryRun) : IWriteGate
{
    public bool DryRun { get; } = dryRun;

    private const uint InvalidFileAttributes = 0xFFFFFFFF;

    public Task<WriteResult> SetAttributesAsync(ExtendedPath path, EntryAttributes attributes, CancellationToken ct)
        => RunAsync("set-attributes", path, ct,
            () => SetFileAttributesW(path.Value, (uint)attributes));

    public Task<WriteResult> ClearAttributesAsync(ExtendedPath path, EntryAttributes toRemove, CancellationToken ct)
        => RunAsync("clear-attributes", path, ct, () =>
        {
            var current = GetFileAttributesW(path.Value);
            if (current == InvalidFileAttributes) return false;

            var updated = current & ~(uint)toRemove;

            // FILE_ATTRIBUTE_NORMAL is only valid on its own. Stripping the last
            // remaining flag leaves zero, which SetFileAttributesW rejects.
            if (updated == 0) updated = (uint)EntryAttributes.Normal;

            return updated == current || SetFileAttributesW(path.Value, updated);
        },
        detail: $"remove {toRemove}");

    /// <summary>
    /// Renames via a true directory-entry update.
    /// </summary>
    /// <remarks>
    /// <see cref="MoveFileFlags.CopyAllowed"/> is intentionally omitted. With it,
    /// Windows may satisfy the call by copying and deleting — which needs free
    /// space, takes minutes instead of milliseconds, and can leave both source
    /// and destination populated if interrupted. Without it, a move that cannot
    /// be done as a rename fails cleanly and the caller decides what to do.
    /// </remarks>
    public Task<WriteResult> RenameAsync(ExtendedPath from, ExtendedPath to, CancellationToken ct)
        => RunAsync("rename", from, ct,
            () => MoveFileExW(from.Value, to.Value, MoveFileFlags.WriteThrough),
            detail: $"-> {to.ForDisplay()}");

    /// <summary>
    /// Creates a directory, and every parent of it that is missing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>CreateDirectoryW</c> makes exactly one level, and under the <c>\\?\</c>
    /// prefix there is no path normalisation and no implicit parent creation. Asking
    /// it for a folder two levels below anything that exists fails with
    /// ERROR_PATH_NOT_FOUND - "the system cannot find the path specified" - naming the
    /// path it was just asked to create, which reads like nonsense.
    /// </para>
    /// <para>
    /// That is where every quarantine on a fresh machine stopped: the destination is
    /// <c>%USERPROFILE%\SmartLab\quarantine</c>, and <c>SmartLab</c> does not exist
    /// until something makes it. A worm the scan had correctly identified was then
    /// left exactly where it was, and each rescan found it again.
    /// </para>
    /// <para>
    /// Each level is created through the same journaled call as any other write, so
    /// the record still accounts for every directory this app brought into being.
    /// </para>
    /// </remarks>
    public async Task<WriteResult> CreateDirectoryAsync(ExtendedPath path, CancellationToken ct)
    {
        var missing = new Stack<ExtendedPath>();
        ExtendedPath? current = path;

        // Walked upwards and created downwards, so each level exists before the one
        // below it is attempted. The walk stops at the first ancestor that is already
        // there, which on a volume root is immediate.
        while (current is { } level && !Directory.Exists(level.Value))
        {
            missing.Push(level);
            current = level.Parent;
        }

        // Already there. Reported as a success rather than skipped: the caller asked
        // for the directory to exist, and it does.
        if (missing.Count == 0) return await CreateOneAsync(path, ct).ConfigureAwait(false);

        WriteResult result = default!;

        while (missing.Count > 0)
        {
            result = await CreateOneAsync(missing.Pop(), ct).ConfigureAwait(false);

            // A parent that could not be made means none of its children can be, and
            // the failure that matters is the first one.
            if (!result.Succeeded) return result;
        }

        return result;
    }

    private Task<WriteResult> CreateOneAsync(ExtendedPath path, CancellationToken ct)
        => RunAsync("create-directory", path, ct,
            () => CreateDirectoryW(path.Value, IntPtr.Zero) ||
                  Marshal.GetLastWin32Error() == 183 /* ERROR_ALREADY_EXISTS */);

    public Task<WriteResult> DeleteFileAsync(ExtendedPath path, CancellationToken ct)
        => RunAsync("delete", path, ct,
            () => DeleteFileW(path.Value));

    public Task<WriteResult> DeleteEmptyDirectoryAsync(ExtendedPath path, CancellationToken ct)
        => RunAsync("delete-directory", path, ct,
            () => RemoveDirectoryW(path.Value));

    public async Task<WriteResult> CopyFileAsync(
        ExtendedPath from, ExtendedPath to, IProgress<long>? progress, CancellationToken ct)
    {
        if (DryRun)
        {
            var skipped = WriteResult.DryRun("copy", from);
            await JournalAsync(skipped, $"-> {to.ForDisplay()}", ct).ConfigureAwait(false);
            return skipped;
        }

        var result = await Task.Run(() =>
        {
            var cancel = 0;

            uint OnProgress(long totalSize, long transferred, long _, long __,
                            uint ___, uint reason, IntPtr ____, IntPtr _____, IntPtr ______)
            {
                if (ct.IsCancellationRequested) return PROGRESS_CANCEL;
                if (reason == CALLBACK_CHUNK_FINISHED) progress?.Report(transferred);
                return PROGRESS_CONTINUE;
            }

            var ok = CopyFileExW(
                from.Value, to.Value, OnProgress, IntPtr.Zero, ref cancel, CopyFileFlags.Restartable);

            if (ok) return WriteResult.Ok("copy", from);

            // Capture once — the second call would report the error raised by the
            // first, not the one that failed the copy.
            var error = Marshal.GetLastWin32Error();
            return WriteResult.Failed("copy", from, error, Win32VolumeReader.DescribeError(error));
        }, ct).ConfigureAwait(false);

        await JournalAsync(result, $"-> {to.ForDisplay()}", ct).ConfigureAwait(false);
        return result;
    }

    private async Task<WriteResult> RunAsync(
        string operation, ExtendedPath target, CancellationToken ct, Func<bool> action, string? detail = null)
    {
        if (DryRun)
        {
            var skipped = WriteResult.DryRun(operation, target);
            await JournalAsync(skipped, detail, ct).ConfigureAwait(false);
            return skipped;
        }

        WriteResult result;
        if (action())
        {
            result = WriteResult.Ok(operation, target);
        }
        else
        {
            var error = Marshal.GetLastWin32Error();
            result = WriteResult.Failed(operation, target, error, Win32VolumeReader.DescribeError(error));
        }

        await JournalAsync(result, detail, ct).ConfigureAwait(false);
        return result;
    }

    private Task JournalAsync(WriteResult result, string? detail, CancellationToken ct)
    {
        var note = string.Join(' ', new[] { detail, result.Message }.Where(s => !string.IsNullOrEmpty(s)));

        return journal.AppendAsync(
            JournalRecord.For(result.Operation, result.Target.ForDisplay(), result.Succeeded,
                string.IsNullOrEmpty(note) ? null : note),
            ct);
    }
}
