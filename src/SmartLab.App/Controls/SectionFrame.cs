using System.Windows;
using System.Windows.Controls;

namespace SmartLab.App.Controls;

/// <summary>
/// The frame every section sits in: a header band, the content, a status strip.
/// </summary>
/// <remarks>
/// <para>
/// One control rather than the same forty lines of DockPanel repeated seventeen
/// times. It is also what makes the four content shapes possible: with the header
/// and status owned here, a section's template contains only the thing it is
/// actually about.
/// </para>
/// <para>
/// Lookless, with its template in Controls.xaml, so the two palettes keep reaching
/// it through DynamicResource.
/// </para>
/// </remarks>
public sealed class SectionFrame : ContentControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SectionFrame), new PropertyMetadata(string.Empty));

    /// <summary>One line under the title saying what the section does.</summary>
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle), typeof(string), typeof(SectionFrame), new PropertyMetadata(string.Empty));

    /// <summary>
    /// The verbs, right-aligned in the header band.
    /// </summary>
    /// <remarks>
    /// In the header rather than under the content, so the measuring verb and the
    /// acting one are read in that order - and so Wipe's Dry run, the last toggle of
    /// its kind, is read before the button it guards instead of beside it.
    /// </remarks>
    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions), typeof(object), typeof(SectionFrame), new PropertyMetadata(null));

    /// <summary>Live line along the bottom. Empty hides the strip entirely.</summary>
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(string), typeof(SectionFrame), new PropertyMetadata(string.Empty));

    /// <summary>Settings the verbs act on: a drive picker, a folder, a threshold.</summary>
    public static readonly DependencyProperty SettingsProperty = DependencyProperty.Register(
        nameof(Settings), typeof(object), typeof(SectionFrame), new PropertyMetadata(null));

    /// <summary>
    /// True when the content wants the whole stage with no padding.
    /// </summary>
    /// <remarks>
    /// For the canvas shape. A treemap inset by 20 px on every side reads as a picture
    /// of a treemap rather than as the map itself.
    /// </remarks>
    public static readonly DependencyProperty IsCanvasProperty = DependencyProperty.Register(
        nameof(IsCanvas), typeof(bool), typeof(SectionFrame), new PropertyMetadata(false));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    public string Status
    {
        get => (string)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public object? Settings
    {
        get => GetValue(SettingsProperty);
        set => SetValue(SettingsProperty, value);
    }

    public bool IsCanvas
    {
        get => (bool)GetValue(IsCanvasProperty);
        set => SetValue(IsCanvasProperty, value);
    }

    static SectionFrame()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(SectionFrame), new FrameworkPropertyMetadata(typeof(SectionFrame)));
    }
}
