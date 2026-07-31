using System.Diagnostics;
using System.Text;
using System.Windows.Diagnostics;

namespace SmartLab.App;

/// <summary>
/// Collects WPF's binding failures so a capture run can report them.
/// </summary>
/// <remarks>
/// <para>
/// A broken binding is the quietest fault this interface can have. A missing
/// <c>StaticResource</c> throws and takes the window down, which at least announces
/// itself; a binding to a property that does not exist renders as an empty string and
/// nothing else happens. Seventeen stages were rewritten against these view models,
/// and reading each screenshot for a blank where a number should be is not a method.
/// </para>
/// <para>
/// Attached to <c>--screenshot</c> rather than to normal startup, because that run
/// already visits every section and populates most of them - which is exactly the
/// traversal a binding check needs. Configuring this through app.config, the usual
/// way, does not work: .NET dropped the trace-source configuration section, so the
/// listener has to be installed in code.
/// </para>
/// </remarks>
internal sealed class BindingErrorLog : TraceListener
{
    private readonly List<string> _lines = [];
    private readonly StringBuilder _pending = new();

    public IReadOnlyList<string> Errors => _lines;

    /// <summary>Starts listening, and returns the log to read afterwards.</summary>
    public static BindingErrorLog Attach()
    {
        var log = new BindingErrorLog();

        // Refresh is required: the source caches its switch level on first use, and
        // WPF has usually already bound something by the time this runs.
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(log);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

        return log;
    }

    public void Detach()
    {
        Flush();
        PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
    }

    /// <remarks>
    /// WPF emits one binding failure as several Write calls followed by a WriteLine,
    /// so the fragments are accumulated and only counted when the line ends.
    /// </remarks>
    public override void Write(string? message) => _pending.Append(message);

    public override void WriteLine(string? message)
    {
        _pending.Append(message);

        var line = _pending.ToString().Trim();
        _pending.Clear();

        if (line.Length > 0) _lines.Add(line);
    }

    public override void Flush()
    {
        if (_pending.Length == 0) return;

        var line = _pending.ToString().Trim();
        _pending.Clear();

        if (line.Length > 0) _lines.Add(line);
    }
}
