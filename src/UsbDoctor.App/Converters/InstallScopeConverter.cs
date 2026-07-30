using System.Globalization;
using System.Windows.Data;

namespace UsbDoctor.App.Converters;

/// <summary>
/// Turns <c>InstalledProgram.IsPerUser</c> into the heading its group sits under.
/// </summary>
/// <remarks>
/// <para>
/// The grouping earns its place by answering a question the operator would
/// otherwise discover by being refused: a machine-wide program cannot be removed
/// without elevation, and this app runs as the invoking user. Saying so in the
/// heading is cheaper than a failed uninstall.
/// </para>
/// <para>
/// A converter rather than a display property on the record, so the domain model
/// keeps its own vocabulary instead of carrying strings written for a window.
/// </para>
/// </remarks>
public sealed class InstallScopeConverter : IValueConverter
{
    public const string PerUser = "Installed for you";
    public const string MachineWide = "Installed for all users - removal needs administrator";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? PerUser : MachineWide;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Group headings are display-only.");
}
