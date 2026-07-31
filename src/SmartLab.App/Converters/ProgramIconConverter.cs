using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SmartLab.Maintenance;

namespace SmartLab.App.Converters;

/// <summary>
/// Turns a program's registered <c>DisplayIcon</c> into something the list can draw.
/// </summary>
/// <remarks>
/// <para>
/// A converter rather than a property on the view model, for one reason: this is the
/// only part of a program list that needs Win32 and WPF imaging, and putting it on
/// the record would drag both into <c>SmartLab.Maintenance</c>, which has no window
/// to draw in and is tested without one.
/// </para>
/// <para>
/// Every result is cached, including the failures. Thirty programs on a machine where
/// half the registered icon paths point at files that were uninstalled years ago is
/// normal, and a list that retried each of those on every scroll would spend its time
/// failing to open the same missing file.
/// </para>
/// </remarks>
public sealed class ProgramIconConverter : IValueConverter
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is InstalledProgram program ? Load(program) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <summary>
    /// The icon for one program, or null when there is nothing to draw.
    /// </summary>
    /// <remarks>
    /// Falls back from the registered icon to the uninstaller's own executable. A
    /// vendor that registered no DisplayIcon has usually still pointed
    /// UninstallString at a program with an icon in it, and a real icon beats a
    /// placeholder even when it is the uninstaller's.
    /// </remarks>
    public static ImageSource? Load(InstalledProgram program)
    {
        var key = program.DisplayIcon
            ?? program.UninstallString
            ?? program.QuietUninstallString
            ?? program.RegistryKeyPath;

        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
        }

        var icon = FromRegisteredPath(program.DisplayIcon)
            ?? FromRegisteredPath(UninstallCommandParser.Parse(
                program.UninstallString ?? program.QuietUninstallString).FileName);

        lock (Cache)
        {
            Cache[key] = icon;
        }

        return icon;
    }

    /// <summary>
    /// Reads an icon out of a <c>path</c> or <c>path,index</c> value.
    /// </summary>
    /// <remarks>
    /// The path is often quoted, often carries an index, and often points at
    /// something that is no longer there - all of which is normal in this registry
    /// and none of which is worth an exception reaching a list.
    /// </remarks>
    private static ImageSource? FromRegisteredPath(string? registered)
    {
        if (string.IsNullOrWhiteSpace(registered)) return null;

        var text = registered.Trim();
        var index = 0;

        // Split on the last comma, and only when what follows is a number: a path can
        // legitimately contain a comma, an icon index cannot.
        var comma = text.LastIndexOf(',');
        if (comma > 0 && int.TryParse(text[(comma + 1)..].Trim(), out var parsed))
        {
            index = parsed;
            text = text[..comma];
        }

        text = text.Trim().Trim('"');

        if (text.Length == 0 || !File.Exists(text)) return null;

        try
        {
            return Extract(text, index);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ImageSource? Extract(string path, int index)
    {
        var handles = new IntPtr[1];

        // A negative index is a resource id rather than a position, which ExtractIconEx
        // expects to be passed through as-is.
        if (ExtractIconEx(path, index, handles, null, 1) <= 0 || handles[0] == IntPtr.Zero)
            return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                handles[0], System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            // Frozen because the list binds it from the render thread, and an unfrozen
            // source would be copied on every use.
            source.Freeze();

            return source;
        }
        finally
        {
            DestroyIcon(handles[0]);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(
        string file, int index, IntPtr[]? large, IntPtr[]? small, int count);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
