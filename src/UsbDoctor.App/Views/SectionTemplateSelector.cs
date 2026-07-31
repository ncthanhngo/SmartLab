using System.Windows;
using System.Windows.Controls;

namespace UsbDoctor.App.Views;

/// <summary>
/// Picks a section's stage by its key.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the seventeen visibility triggers a single window would otherwise need.
/// Only the selected section's template is realised, so sixteen unbuilt stages are
/// not constructed at startup and then hidden.
/// </para>
/// <para>
/// Templates are found by name rather than registered here, so adding a section is
/// one resource in one dictionary and nothing else. A key with no template falls back
/// to a stage that says so - which is what a half-finished rail should do rather than
/// showing an empty window with no explanation.
/// </para>
/// </remarks>
public sealed class SectionTemplateSelector : DataTemplateSelector
{
    /// <summary>Resource key prefix, so section templates cannot collide with styles.</summary>
    public const string Prefix = "section-";

    /// <summary>Shown for a section whose stage has not been built yet.</summary>
    public const string FallbackKey = Prefix + "missing";

    public static string ResourceKeyFor(string sectionKey) => Prefix + sectionKey;

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
    {
        if (item is not NavSection section || container is not FrameworkElement element)
            return null;

        // TryFindResource rather than FindResource: a missing template is a section
        // still to be built, not a fault worth throwing over.
        return element.TryFindResource(ResourceKeyFor(section.Key)) as DataTemplate
            ?? element.TryFindResource(FallbackKey) as DataTemplate;
    }
}
