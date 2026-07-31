using System.Text;
using SmartLab.Core.Abstractions;
using SmartLab.Core.Model;
using SmartLab.Core.Paths;

namespace SmartLab.Tests;

public sealed record FakeNode(bool IsDirectory, byte[] Content, EntryAttributes Attributes);

/// <summary>
/// An in-memory volume implementing both <see cref="IVolumeReader"/> and
/// <see cref="IWriteGate"/>, so the engine can be exercised end to end without a
/// disk.
/// </summary>
/// <remarks>
/// This is a real implementation, not a stub: it enforces that a directory must
/// exist before a child is created, that an empty directory is required before
/// removal, and that a rename actually moves descendants. Tests that pass against
/// it are testing behaviour, not the absence of it. Corrupt entries and unreadable
/// files can be injected to reproduce the conditions found on the drive this tool
/// was built from.
/// </remarks>
public sealed class FakeFileSystem : IVolumeReader, IWriteGate
{
    private readonly Dictionary<string, FakeNode> _nodes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Paths whose reads throw, simulating ERROR_FILE_CORRUPT.</summary>
    public HashSet<string> UnreadableFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Directories that yield one unreadable child entry when enumerated.</summary>
    public Dictionary<string, string> DamagedChildren { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every mutating call, in order, for asserting sequence.</summary>
    public List<string> Operations { get; } = [];

    public bool DryRun { get; init; }

    public VolumeInfo Volume { get; init; } =
        new('E', "TEST", "FAT32", 4_000_000_000, 1_000_000_000, VolumeDriveType.Removable);

    public FakeFileSystem() => _nodes[@"\\?\E:"] = new FakeNode(true, [], EntryAttributes.Directory);

    public IReadOnlyDictionary<string, FakeNode> Nodes => _nodes;

    // ---- construction helpers -------------------------------------------------

    public FakeFileSystem AddDirectory(string path, EntryAttributes attributes = EntryAttributes.Directory)
    {
        _nodes[ExtendedPath.From(path).Value] = new FakeNode(true, [], attributes | EntryAttributes.Directory);
        return this;
    }

    /// <summary>Adds a directory whose name cannot be expressed as a normal path.</summary>
    public FakeFileSystem AddRawDirectory(ExtendedPath path, EntryAttributes attributes)
    {
        _nodes[path.Value] = new FakeNode(true, [], attributes | EntryAttributes.Directory);
        return this;
    }

    public FakeFileSystem AddFile(string path, string content,
        EntryAttributes attributes = EntryAttributes.Archive)
    {
        _nodes[ExtendedPath.From(path).Value] =
            new FakeNode(false, Encoding.UTF8.GetBytes(content), attributes);
        return this;
    }

    public FakeFileSystem AddRawFile(ExtendedPath path, string content,
        EntryAttributes attributes = EntryAttributes.Archive)
    {
        _nodes[path.Value] = new FakeNode(false, Encoding.UTF8.GetBytes(content), attributes);
        return this;
    }

    public bool Exists(string path) => _nodes.ContainsKey(ExtendedPath.From(path).Value);
    public bool ExistsRaw(ExtendedPath path) => _nodes.ContainsKey(path.Value);

    public EntryAttributes AttributesOf(string path) => _nodes[ExtendedPath.From(path).Value].Attributes;

    public int FileCount => _nodes.Count(n => !n.Value.IsDirectory);

    // ---- IVolumeReader --------------------------------------------------------

    public async IAsyncEnumerable<EnumEntry> EnumerateAsync(
        ExtendedPath directory,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();

        if (DamagedChildren.TryGetValue(directory.Value, out var rawName))
        {
            yield return new EnumEntry.Damaged(directory, rawName, 1392,
                "The file or directory is corrupted and unreadable.");
        }

        var prefix = directory.Value.TrimEnd('\\') + "\\";

        foreach (var (path, node) in _nodes.ToArray())
        {
            ct.ThrowIfCancellationRequested();

            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var remainder = path[prefix.Length..];
            if (remainder.Length == 0 || remainder.Contains('\\')) continue; // not a direct child

            yield return new EnumEntry.Ok(new FileEntry(
                ExtendedPath.FromRaw(path), remainder, node.Content.Length, node.Attributes, null));
        }
    }

    public Stream OpenRead(ExtendedPath file)
    {
        if (UnreadableFiles.Contains(file.Value))
            throw new IOException("The file or directory is corrupted and unreadable.");

        return new MemoryStream(_nodes[file.Value].Content, writable: false);
    }

    public VolumeInfo? GetVolume(char driveLetter) =>
        char.ToUpperInvariant(driveLetter) == Volume.DriveLetter ? Volume : null;

    // ---- IWriteGate -----------------------------------------------------------

    public Task<WriteResult> SetAttributesAsync(ExtendedPath path, EntryAttributes attributes, CancellationToken ct)
        => Mutate("set-attributes", path, () =>
        {
            if (!_nodes.TryGetValue(path.Value, out var node)) return false;
            _nodes[path.Value] = node with { Attributes = attributes };
            return true;
        });

    public Task<WriteResult> ClearAttributesAsync(ExtendedPath path, EntryAttributes toRemove, CancellationToken ct)
        => Mutate("clear-attributes", path, () =>
        {
            if (!_nodes.TryGetValue(path.Value, out var node)) return false;
            _nodes[path.Value] = node with { Attributes = node.Attributes & ~toRemove };
            return true;
        });

    public Task<WriteResult> RenameAsync(ExtendedPath from, ExtendedPath to, CancellationToken ct)
        => Mutate("rename", from, () =>
        {
            if (!_nodes.ContainsKey(from.Value) || _nodes.ContainsKey(to.Value)) return false;

            var prefix = from.Value + "\\";
            var moving = _nodes.Keys
                .Where(k => k.Equals(from.Value, StringComparison.OrdinalIgnoreCase) ||
                            k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            foreach (var key in moving)
            {
                var node = _nodes[key];
                _nodes.Remove(key);
                var moved = key.Equals(from.Value, StringComparison.OrdinalIgnoreCase)
                    ? to.Value
                    : to.Value + key[from.Value.Length..];
                _nodes[moved] = node;
            }

            return true;
        });

    public Task<WriteResult> CreateDirectoryAsync(ExtendedPath path, CancellationToken ct)
        => Mutate("create-directory", path, () =>
        {
            if (_nodes.ContainsKey(path.Value)) return true;

            // A real filesystem refuses to create a child of a missing parent.
            var parent = path.Parent;
            if (parent is { } p && !_nodes.ContainsKey(p.Value)) return false;

            _nodes[path.Value] = new FakeNode(true, [], EntryAttributes.Directory);
            return true;
        });

    public Task<WriteResult> CopyFileAsync(
        ExtendedPath from, ExtendedPath to, IProgress<long>? progress, CancellationToken ct)
        => Mutate("copy", from, () =>
        {
            if (UnreadableFiles.Contains(from.Value)) return false;
            if (!_nodes.TryGetValue(from.Value, out var source) || source.IsDirectory) return false;

            var parent = to.Parent;
            if (parent is { } p && !_nodes.ContainsKey(p.Value)) return false;

            _nodes[to.Value] = source with { };
            progress?.Report(source.Content.Length);
            return true;
        });

    public Task<WriteResult> DeleteFileAsync(ExtendedPath path, CancellationToken ct)
        => Mutate("delete", path, () => _nodes.Remove(path.Value));

    public Task<WriteResult> DeleteEmptyDirectoryAsync(ExtendedPath path, CancellationToken ct)
        => Mutate("delete-directory", path, () =>
        {
            var prefix = path.Value + "\\";
            if (_nodes.Keys.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                return false; // ERROR_DIR_NOT_EMPTY

            return _nodes.Remove(path.Value);
        });

    private Task<WriteResult> Mutate(string operation, ExtendedPath target, Func<bool> action)
    {
        Operations.Add($"{operation}:{target.ForDisplay()}");

        if (DryRun) return Task.FromResult(WriteResult.DryRun(operation, target));

        return Task.FromResult(action()
            ? WriteResult.Ok(operation, target)
            : WriteResult.Failed(operation, target, 1, $"{operation} failed"));
    }
}

/// <summary>Journal that keeps records in memory for assertions.</summary>
public sealed class RecordingJournal : IJournal
{
    public List<JournalRecord> Records { get; } = [];

    public Task AppendAsync(JournalRecord record, CancellationToken ct)
    {
        Records.Add(record);
        return Task.CompletedTask;
    }
}
