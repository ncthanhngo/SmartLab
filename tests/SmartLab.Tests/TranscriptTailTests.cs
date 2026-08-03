using SmartLab.Maintenance;
using Xunit;

namespace SmartLab.Tests;

/// <summary>
/// Reading a transcript while the process writing it is still running.
/// </summary>
/// <remarks>
/// An elevated process has nowhere to write that its unelevated caller can read, so its
/// output goes through a file. Read once at the end, that gives an operation whose whole
/// report arrives after it is over - and installing drivers can take half an hour, which
/// is a long time for a screen to be indistinguishable from one that has hung.
/// </remarks>
public sealed class TranscriptTailTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"smartlab-tail-test-{Guid.NewGuid():N}.log");

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }

    private void Append(string text) => File.AppendAllText(_path, text);

    [Fact]
    public void EachReadReturnsOnlyWhatWasWrittenSinceTheLast()
    {
        var tail = new TranscriptTail(_path);

        Append("[step] 1/2 downloading Intel\n");
        Assert.Equal(["[step] 1/2 downloading Intel"], tail.ReadNew());

        // Nothing new: an unchanged transcript must not replay what was already read.
        Assert.Empty(tail.ReadNew());

        Append("[step] 1/2 installing Intel\n[ok] Intel\n");
        Assert.Equal(["[step] 1/2 installing Intel", "[ok] Intel"], tail.ReadNew());
    }

    /// <remarks>
    /// The writer is mid-sentence, not finished with a short one. Half a line in a log
    /// reads as a message that ended where it did not.
    /// </remarks>
    [Fact]
    public void ALineStillBeingWrittenIsHeldBackUntilItIsWhole()
    {
        var tail = new TranscriptTail(_path);

        Append("[step] 1/1 downl");
        Assert.Empty(tail.ReadNew());

        Append("oading Intel  (118.4 MB)\n");
        Assert.Equal(["[step] 1/1 downloading Intel  (118.4 MB)"], tail.ReadNew());
    }

    /// <remarks>
    /// Many tools end without a trailing newline, and that last line is usually the
    /// verdict - which is the one line that must not be the one dropped.
    /// </remarks>
    [Fact]
    public void TheLastLineArrivesEvenWithNoNewlineAfterIt()
    {
        var tail = new TranscriptTail(_path);

        Append("[ok] Intel\nA restart is needed");

        Assert.Equal(["[ok] Intel"], tail.ReadNew());
        Assert.Equal(["A restart is needed"], tail.ReadRest());
    }

    [Fact]
    public void ATranscriptThatDoesNotExistYetIsNotAFailure()
    {
        // The process has been started and has not written its first line. Polling that
        // must read as "nothing yet", never as an exception in a section someone is
        // watching.
        var tail = new TranscriptTail(_path);

        Assert.Empty(tail.ReadNew());
        Assert.Empty(tail.ReadRest());
    }

    [Fact]
    public void CarriageReturnsAndNulPaddingDoNotReachTheLog()
    {
        // These tools write UTF-16 on some systems and the OEM codepage on others, and
        // pad with nul bytes; the transcript is redirected by cmd, which ends lines
        // with both characters.
        var tail = new TranscriptTail(_path);

        Append("[ok] Intel\0\0\r\n");

        Assert.Equal(["[ok] Intel"], tail.ReadNew());
    }

    [Fact]
    public void AFileTheWriterStillHasOpenCanStillBeRead()
    {
        // cmd holds the transcript open for the length of the command. A reader that
        // asks for exclusive access gets nothing for the entire run.
        using var writer = new StreamWriter(
            new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true,
        };

        var tail = new TranscriptTail(_path);

        writer.WriteLine("[step] 1/1 installing Intel");

        Assert.Equal(["[step] 1/1 installing Intel"], tail.ReadNew());
    }
}
