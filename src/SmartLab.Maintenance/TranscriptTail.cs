using System.Text;

namespace SmartLab.Maintenance;

/// <summary>
/// Reads whole lines out of a file another process is still writing to.
/// </summary>
/// <remarks>
/// <para>
/// An elevated process has nowhere to write that its unelevated caller can read -
/// redirection does not cross that boundary - so its output goes through a transcript
/// file instead. Read only once, at the end, that arrangement gives an operation whose
/// entire report arrives after it is over: installing three drivers can take half an
/// hour, and a screen that says nothing for that long cannot be told apart from one
/// that has hung.
/// </para>
/// <para>
/// A partial last line is held back rather than reported. The writer is mid-sentence,
/// not finished with a short one, and half a line in a log is worse than a slow one:
/// it reads as a message that ended where it did not.
/// </para>
/// </remarks>
public sealed class TranscriptTail(string path)
{
    private long _offset;
    private string _partial = string.Empty;

    /// <summary>Whole lines written since the last read.</summary>
    public IReadOnlyList<string> ReadNew() => Read(toTheEnd: false);

    /// <summary>Everything left, including a last line with no newline after it.</summary>
    /// <remarks>
    /// For after the writer has exited. Many tools end without a trailing newline, and
    /// that last line is usually the verdict.
    /// </remarks>
    public IReadOnlyList<string> ReadRest() => Read(toTheEnd: true);

    private IReadOnlyList<string> Read(bool toTheEnd)
    {
        try
        {
            // The writer holds this open, and cmd may replace or delete it - shared
            // every way it can be, or every read of a live transcript throws.
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            if (stream.Length > _offset)
            {
                stream.Seek(_offset, SeekOrigin.Begin);

                var buffer = new byte[stream.Length - _offset];
                var read = stream.Read(buffer, 0, buffer.Length);

                _offset += read;

                // Nul bytes rather than a detected encoding, for the same reason the
                // whole-file read does it: these tools write UTF-16 on some systems and
                // the OEM codepage on others, and stripping survives both.
                _partial += Encoding.UTF8.GetString(buffer, 0, read).Replace("\0", string.Empty);
            }
        }
        catch (IOException)
        {
            // The file is not there yet, or the writer has it locked this instant.
            // Either way the next poll is the answer, not an exception.
        }
        catch (UnauthorizedAccessException)
        {
        }

        return TakeLines(toTheEnd);
    }

    private List<string> TakeLines(bool toTheEnd)
    {
        var lines = new List<string>();
        var pieces = _partial.Split('\n');

        // The last piece is whatever follows the final newline: a line still being
        // written, unless the writer has stopped and there will be no more.
        var complete = toTheEnd ? pieces.Length : pieces.Length - 1;

        for (var i = 0; i < complete; i++)
        {
            var line = pieces[i].TrimEnd('\r').Trim();
            if (line.Length > 0) lines.Add(line);
        }

        _partial = toTheEnd ? string.Empty : pieces[^1];

        return lines;
    }
}
