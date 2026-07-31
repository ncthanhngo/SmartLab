using System.Text;
using System.Text.Json;
using SmartLab.Core.Abstractions;

namespace SmartLab.Engine.Journal;

/// <summary>
/// Append-only JSON Lines journal.
/// </summary>
/// <remarks>
/// One self-contained JSON object per line, flushed on every write. If the device
/// disappears mid-run — the realistic failure for a dying stick — every completed
/// action is already durable, and the partial last line is the only loss. A single
/// large JSON document would be unreadable after the same interruption.
/// </remarks>
public sealed class JsonlJournal : IJournal, IAsyncDisposable
{
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public JsonlJournal(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _writer = new StreamWriter(
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
    }

    public async Task AppendAsync(JournalRecord record, CancellationToken ct)
    {
        var line = JsonSerializer.Serialize(record, Options);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync().ConfigureAwait(false);
        _lock.Dispose();
    }
}

/// <summary>Discards records. For dry runs and unit tests.</summary>
public sealed class NullJournal : IJournal
{
    public Task AppendAsync(JournalRecord record, CancellationToken ct) => Task.CompletedTask;
}
