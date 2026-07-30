using System.Windows;
using Microsoft.Win32;

// WinForms is referenced for the tray icon, which makes Application ambiguous.
using Application = System.Windows.Application;

namespace UsbDoctor.App.Theming;

public enum AppTheme
{
    Dark,
    Light,
}

/// <summary>
/// Swaps the palette dictionary at runtime and remembers the choice.
/// </summary>
/// <remarks>
/// <para>
/// The two palettes declare the same keys, so switching is a whole-dictionary
/// replacement rather than a per-key edit. That only reaches the interface because
/// brushes are referenced with DynamicResource: a StaticResource is resolved once
/// when the element is parsed and would keep the colour it was born with.
/// </para>
/// <para>
/// The first run follows Windows. After that the app's own setting wins - someone
/// who has chosen light here did not choose it for every app on the machine.
/// </para>
/// </remarks>
public static class ThemeManager
{
    private const string SettingsKey = @"Software\USB Doctor";
    private const string ValueName = "Theme";

    private const string WindowsPersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>Matched against a merged dictionary's Source to find the palette.</summary>
    private const string PaletteMarker = "Palette.";

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static bool IsLight => Current == AppTheme.Light;

    /// <summary>Raised after the palette has been swapped in.</summary>
    /// <remarks>
    /// For the few brushes built in code rather than resolved from a dictionary.
    /// Anything drawn from XAML needs no notification - DynamicResource has already
    /// re-resolved it by the time this runs.
    /// </remarks>
    public static event Action? ThemeChanged;

    /// <summary>Applies the stored choice, falling back to the Windows setting.</summary>
    public static void ApplyStartupTheme() => Apply(Stored() ?? WindowsPreference());

    public static void Apply(AppTheme theme)
    {
        var dictionaries = Application.Current?.Resources.MergedDictionaries;
        if (dictionaries is null) return;

        // An absolute pack URI, not a relative one. A ResourceDictionary built in
        // code has no XAML document to resolve a relative path against, so the
        // relative form loads nothing and fails without raising anything.
        var replacement = new ResourceDictionary
        {
            Source = new Uri(
                theme == AppTheme.Light
                    ? "pack://application:,,,/UsbDoctor.App;component/Themes/Palette.Light.xaml"
                    : "pack://application:,,,/UsbDoctor.App;component/Themes/Palette.Dark.xaml",
                UriKind.Absolute),
        };

        // Removed and appended rather than replaced in place. Among merged
        // dictionaries the last one wins, so a palette added anywhere but the end
        // would be quietly overruled by the one already there - which is not a
        // crash, just an app that ignores the switch.
        for (var i = dictionaries.Count - 1; i >= 0; i--)
        {
            var source = dictionaries[i].Source?.OriginalString;
            if (source?.Contains(PaletteMarker, StringComparison.OrdinalIgnoreCase) == true)
                dictionaries.RemoveAt(i);
        }

        dictionaries.Add(replacement);

        Current = theme;
        Store(theme);
        ThemeChanged?.Invoke();
    }

    private static AppTheme? Stored()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);

            return (key?.GetValue(ValueName) as string) switch
            {
                nameof(AppTheme.Light) => AppTheme.Light,
                nameof(AppTheme.Dark) => AppTheme.Dark,
                _ => null,
            };
        }
        catch (Exception)
        {
            // A readable interface matters more than a remembered preference.
            return null;
        }
    }

    private static void Store(AppTheme theme)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            key?.SetValue(ValueName, theme.ToString());
        }
        catch (Exception)
        {
            // Ignored: failing to persist must not stop the theme being applied.
        }
    }

    /// <summary>
    /// What Windows itself is set to, for the first run only.
    /// </summary>
    /// <remarks>
    /// AppsUseLightTheme is 0 for dark and 1 for light, and is absent on installs
    /// that have never been changed - which historically meant light.
    /// </remarks>
    private static AppTheme WindowsPreference()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(WindowsPersonalizeKey);

            return key?.GetValue("AppsUseLightTheme") is int value && value == 0
                ? AppTheme.Dark
                : AppTheme.Light;
        }
        catch (Exception)
        {
            return AppTheme.Dark;
        }
    }
}
