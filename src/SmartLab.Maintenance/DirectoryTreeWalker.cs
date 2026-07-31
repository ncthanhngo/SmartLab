using SmartLab.Core.Abstractions;
using SmartLab.Core.Model;
using SmartLab.Core.Paths;

namespace SmartLab.Maintenance;

/// <summary>One directory and everything under it, with its total size.</summary>
/// <param name="Bytes">This directory and all its descendants.</param>
/// <param name="Files">Files directly in this directory, not recursive.</param>
public sealed record DirectoryNode(string Path, string Name, long Bytes, int Files)
{
    public List<DirectoryNode> Children { get; } = [];

    /// <summary>Bytes held by files directly here rather than in a child.</summary>
    public long OwnBytes { get; init; }

    public string SizeText => Bytes switch
    {
        < 1024 => $"{Bytes} B",
        < 1024 * 1024 => $"{Bytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{Bytes / 1024.0 / 1024:F1} MB",
        _ => $"{Bytes / 1024.0 / 1024 / 1024:F2} GB",
    };
}

/// <param name="Directories">Directories entered so far.</param>
public readonly record struct WalkProgress(int Directories, int Files, string CurrentPath);

/// <summary>
/// Walks a tree and totals it, for the three sections built on file sizes.
/// </summary>
/// <remarks>
/// <para>
/// Built on <see cref="IVolumeReader"/> rather than on
/// <c>Directory.EnumerateFileSystemEntries</c>, and the reason is the same one
/// recorded for the scanner: one corrupt entry makes the framework enumerator throw
/// and discard every readable sibling with it. A disk-usage view that silently loses
/// a folder is worse than useless - it reports free space that is not there.
/// </para>
/// <para>
/// Reparse points are entered but not followed. A junction pointing at its own parent
/// would otherwise recurse until the path length stops it, and a directory linked from
/// three places would be counted three times in a total meant to describe a disk.
/// </para>
/// </remarks>
public sealed class DirectoryTreeWalker(IVolumeReader reader)
{
    /// <summary>How deep to recurse before treating a directory as a leaf.</summary>
    /// <remarks>
    /// A treemap cannot draw more levels than this legibly, and the walk cost grows
    /// with every one. Sizes from deeper directories are still counted - they are
    /// added to their deepest drawn ancestor rather than dropped.
    /// </remarks>
    public const int DefaultMaxDepth = 12;

    /// <summary>Reported every this many entries, matching the scanner's sampling habit.</summary>
    private const int ProgressEvery = 256;

    private int _directories;
    private int _files;

    public async Task<DirectoryNode> WalkAsync(
        string root,
        int maxDepth = DefaultMaxDepth,
        IProgress<WalkProgress>? progress = null,
        Action<FileEntry>? onFile = null,
        CancellationToken ct = default)
    {
        _directories = 0;
        _files = 0;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return await WalkDirectoryAsync(
            ExtendedPath.From(root), Path.GetFileName(root.TrimEnd('\\')) is { Length: > 0 } name ? name : root,
            maxDepth, visited, progress, onFile, ct).ConfigureAwait(false);
    }

    private async Task<DirectoryNode> WalkDirectoryAsync(
        ExtendedPath directory,
        string name,
        int depthLeft,
        HashSet<string> visited,
        IProgress<WalkProgress>? progress,
        Action<FileEntry>? onFile,
        CancellationToken ct)
    {
        var display = directory.ForDisplay();

        long ownBytes = 0;
        var fileCount = 0;
        var children = new List<DirectoryNode>();

        _directories++;

        if (_directories % ProgressEvery == 0)
            progress?.Report(new WalkProgress(_directories, _files, display));

        await foreach (var entry in reader.EnumerateAsync(directory, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            // A damaged entry costs its own size, not its siblings'. Nothing else to
            // do with it here: the disk-usage sections report space, and a size that
            // cannot be read is not space this tool can account for.
            if (entry is not EnumEntry.Ok(var file) || file.IsDotEntry) continue;

            if (file.IsDirectory)
            {
                // Following a reparse point double-counts at best and loops at worst.
                if (file.Attributes.HasFlag(EntryAttributes.ReparsePoint)) continue;

                var key = file.Path.ForDisplay();
                if (!visited.Add(key)) continue;

                if (depthLeft <= 1)
                {
                    // At the depth limit the subtree still has to be measured, or the
                    // parent's total silently under-reports. It is measured without
                    // being drawn.
                    var collapsed = await WalkDirectoryAsync(
                        file.Path, file.Name, int.MaxValue, visited, progress, onFile, ct)
                        .ConfigureAwait(false);

                    ownBytes += collapsed.Bytes;
                    continue;
                }

                var child = await WalkDirectoryAsync(
                    file.Path, file.Name, depthLeft - 1, visited, progress, onFile, ct)
                    .ConfigureAwait(false);

                children.Add(child);
                continue;
            }

            ownBytes += file.Length;
            fileCount++;
            _files++;

            onFile?.Invoke(file);
        }

        var node = new DirectoryNode(
            display, name, ownBytes + children.Sum(c => c.Bytes), fileCount)
        {
            OwnBytes = ownBytes,
        };

        node.Children.AddRange(children.OrderByDescending(c => c.Bytes));

        return node;
    }
}
