using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;

// The tray icon brings WinForms into this project, and it has a ListBox of its own.
using ListBox = System.Windows.Controls.ListBox;

namespace SmartLab.App.Controls;

/// <summary>
/// Keeps a list showing its newest row.
/// </summary>
/// <remarks>
/// For a log, which is the one kind of list where the interesting end is the bottom.
/// Without this, a running commentary scrolls out of sight after the first few lines
/// and the operator has to chase it with the scrollbar - which is exactly when they
/// are least able to, because the thing they are watching is still moving.
/// </remarks>
public static class AutoScroll
{
    public static readonly DependencyProperty ToNewestProperty =
        DependencyProperty.RegisterAttached(
            "ToNewest", typeof(bool), typeof(AutoScroll), new PropertyMetadata(false, OnToNewestChanged));

    public static void SetToNewest(DependencyObject element, bool value) =>
        element.SetValue(ToNewestProperty, value);

    public static bool GetToNewest(DependencyObject element) =>
        (bool)element.GetValue(ToNewestProperty);

    /// <remarks>
    /// <para>
    /// One way, and deliberately: this is set once in a template rather than toggled,
    /// so turning it back off is a case that would only ever exist to be written.
    /// The subscription is on the control's own view of its items rather than on the
    /// bound collection, so it lives and dies with the control and cannot hold a view
    /// model alive after its screen has gone.
    /// </para>
    /// <para>
    /// The scroll is queued rather than done here. <see cref="ListBox.ScrollIntoView"/>
    /// forces a layout pass, and forcing one from inside a CollectionChanged handler
    /// runs it while the item container generator is still working through that same
    /// notification: the generator's count disagrees with the collection's, and WPF
    /// throws "an ItemsControl is inconsistent with its items source".
    /// </para>
    /// <para>
    /// Two lists bound to one collection is what made it certain rather than merely
    /// possible - the uninstall log is on screen twice, in the section and in the
    /// window - because the first list's forced layout ran before the second had been
    /// told anything at all.
    /// </para>
    /// </remarks>
    private static void OnToNewestChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox box || e.NewValue is not true) return;

        ((INotifyCollectionChanged)box.Items).CollectionChanged += (_, args) =>
        {
            if (args.Action != NotifyCollectionChangedAction.Add) return;

            box.Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() =>
                {
                    // Re-read the count: by the time this runs the list may have been
                    // cleared, and scrolling to a row that no longer exists throws in
                    // its own right.
                    if (box.Items.Count > 0) box.ScrollIntoView(box.Items[^1]);
                }));
        };
    }
}
