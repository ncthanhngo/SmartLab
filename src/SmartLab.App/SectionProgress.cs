using CommunityToolkit.Mvvm.ComponentModel;

namespace SmartLab.App;

/// <summary>
/// What a section is doing while it does it, and what it did once it stops.
/// </summary>
/// <remarks>
/// <para>
/// One object, held by every section that makes the operator wait. Before this each
/// section answered "am I still working" with a status line that could not be told
/// apart from one that had hung, and answered "did it finish" by going quiet - which
/// is the same thing a crash looks like.
/// </para>
/// <para>
/// The frame draws it, so a section opts in by handing it over and never by laying
/// out a bar of its own. That is what keeps twelve sections agreeing about what
/// progress looks like.
/// </para>
/// </remarks>
public sealed partial class SectionProgress : ObservableObject
{
    /// <summary>True between the first press and the last step.</summary>
    [ObservableProperty] private bool _isRunning;

    /// <summary>
    /// Set for a stretch whose length nobody can know.
    /// </summary>
    /// <remarks>
    /// Waiting on somebody else's process, or walking a tree whose size is only known
    /// once the walk finishes. The bar moves without stating a figure there, because
    /// a bar that reads 60% for a minute teaches the operator that the number means
    /// nothing - and then the honest numbers elsewhere stop being read too.
    /// </remarks>
    [ObservableProperty] private bool _isIndeterminate;

    /// <summary>How far through, 0 to 100, whenever that is a real proportion.</summary>
    [ObservableProperty] private double _percent;

    /// <summary>What is happening right now, in three or four words.</summary>
    [ObservableProperty] private string _stage = string.Empty;

    /// <summary>The verdict, once there is one. Empty until the first run finishes.</summary>
    [ObservableProperty] private string _completion = string.Empty;

    [ObservableProperty] private string _completionDetail = string.Empty;

    /// <summary>"good", "warning" or "alert" - what the lamp shows when it is done.</summary>
    [ObservableProperty] private string _tone = "good";

    /// <summary>
    /// True once something has run to completion here.
    /// </summary>
    /// <remarks>
    /// What keeps the verdict on screen afterwards. A band that vanishes the moment
    /// the work stops has told the operator nothing they did not have to be watching
    /// for.
    /// </remarks>
    [ObservableProperty] private bool _hasRun;

    /// <summary>Starts a run at a step whose length is not knowable.</summary>
    public void Begin(string stage)
    {
        Completion = string.Empty;
        CompletionDetail = string.Empty;
        Stage = stage;
        Percent = 0;
        IsIndeterminate = true;
        IsRunning = true;
    }

    /// <summary>Moves to a named step, stating how far through the run it is.</summary>
    public void Step(string stage, double percent)
    {
        Stage = stage;
        Percent = percent;
        IsIndeterminate = false;
        IsRunning = true;
    }

    /// <summary>Moves to a named step whose length is not knowable.</summary>
    public void Unknown(string stage)
    {
        Stage = stage;
        IsIndeterminate = true;
        IsRunning = true;
    }

    /// <summary>Ends the run with a verdict that stays on screen.</summary>
    public void Finish(string tone, string headline, string detail = "")
    {
        Tone = tone;
        Completion = headline;
        CompletionDetail = detail;
        Percent = 100;
        IsIndeterminate = false;
        IsRunning = false;
        HasRun = true;
    }

    /// <summary>Clears everything, for when what it described no longer applies.</summary>
    public void Reset()
    {
        IsRunning = false;
        IsIndeterminate = false;
        Percent = 0;
        Stage = string.Empty;
        Completion = string.Empty;
        CompletionDetail = string.Empty;
        Tone = "good";
        HasRun = false;
    }
}
