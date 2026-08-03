using System.Windows;

namespace SmartLab.App.Controls;

/// <summary>
/// Whether a button that acts on a selection has anything selected.
/// </summary>
/// <remarks>
/// <para>
/// An attached property rather than a name the shared style binds to directly. Two of
/// these buttons can share one view model - the Updater's two tabs do, and so do Repair
/// and Deleted - so a style that looked for a property called <c>HasTicked</c> would
/// force those pairs to share one answer, or force every section to invent the same
/// name and then find it already taken.
/// </para>
/// <para>
/// Typed as a bool for the same reason it is not simply <c>Tag</c>: a trigger comparing
/// <c>Value="True"</c> against an <c>object</c> compares a bool with a string, never
/// matches, and fails by quietly doing nothing.
/// </para>
/// </remarks>
public static class ActionState
{
    public static readonly DependencyProperty IsArmedProperty =
        DependencyProperty.RegisterAttached(
            "IsArmed", typeof(bool), typeof(ActionState), new PropertyMetadata(false));

    public static void SetIsArmed(DependencyObject element, bool value) =>
        element.SetValue(IsArmedProperty, value);

    public static bool GetIsArmed(DependencyObject element) =>
        (bool)element.GetValue(IsArmedProperty);
}
