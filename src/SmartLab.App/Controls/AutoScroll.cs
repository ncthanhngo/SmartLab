using System.Collections.Specialized;
using System.Windows;

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
    /// One way, and deliberately: this is set once in a template rather than toggled,
    /// so turning it back off is a case that would only ever exist to be written.
    /// The subscription is on the control's own view of its items rather than on the
    /// bound collection, so it lives and dies with the control and cannot hold a view
    /// model alive after its screen has gone.
    /// </remarks>
    private static void OnToNewestChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox box || e.NewValue is not true) return;

        ((INotifyCollectionChanged)box.Items).CollectionChanged += (_, args) =>
        {
            if (args.Action != NotifyCollectionChangedAction.Add || box.Items.Count == 0) return;

            box.ScrollIntoView(box.Items[^1]);
        };
    }
}
