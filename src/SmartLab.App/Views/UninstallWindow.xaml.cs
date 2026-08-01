using System.Windows;
using SmartLab.App.Controls;

namespace SmartLab.App.Views;

/// <summary>
/// The window one removal happens in.
/// </summary>
/// <remarks>
/// <para>
/// Modal, and deliberately: at the end of an uninstall there is a decision to make
/// about what else to remove, and on the section's own stage that decision competes
/// with a list of thirty programs and a button that starts another removal.
/// </para>
/// <para>
/// It carries the section's own view model rather than one of its own. Everything it
/// shows - the progress band, the log, the leftovers - is state the section already
/// keeps, and a second copy of it would be a second thing to keep in step.
/// </para>
/// </remarks>
public partial class UninstallWindow : Window
{
    public UninstallWindow()
    {
        InitializeComponent();

        // The log is read from the bottom, like every other log in this application.
        AutoScroll.SetToNewest(Log, true);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
