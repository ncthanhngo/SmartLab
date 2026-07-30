using System.Runtime.InteropServices;
using UsbDoctor.Core.Abstractions;
using UsbDoctor.Core.Model;
using UsbDoctor.Core.Paths;
using static UsbDoctor.Win32.Native.NativeMethods;

namespace UsbDoctor.Win32.Io;

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

    public Task<WriteResult> CreateDirectoryAsync(ExtendedPath path, CancellationToken ct)
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
