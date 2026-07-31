namespace SmartLab.Maintenance;

/// <param name="FileName">Executable to launch.</param>
/// <param name="Arguments">Everything after it, possibly empty.</param>
public readonly record struct UninstallCommand(string FileName, string Arguments)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(FileName);
}

/// <summary>
/// Splits a registered uninstall string into an executable and its arguments, and
/// makes sure an MSI one actually uninstalls.
/// </summary>
/// <remarks>
/// Vendors write these in every shape the shell tolerates:
/// <c>MsiExec.exe /X{GUID}</c>, a quoted path with spaces, an unquoted path with
/// spaces followed by switches. Passing the whole string as a filename fails on
/// most of them, and passing it to a shell would let a crafted registry value run
/// something else entirely - so it is parsed here instead.
/// </remarks>
public static class UninstallCommandParser
{
    public static UninstallCommand Parse(string? uninstallString)
    {
        if (string.IsNullOrWhiteSpace(uninstallString)) return default;

        var text = uninstallString.Trim();

        // Quoted executable: everything inside the quotes is the path, even if it
        // contains spaces.
        if (text[0] == '"')
        {
            var closing = text.IndexOf('"', 1);
            if (closing > 1)
            {
                return Build(text[1..closing], text[(closing + 1)..].Trim());
            }

            // Unbalanced quote: treat the remainder as the path rather than guessing.
            return Build(text.Trim('"'), string.Empty);
        }

        // Unquoted. Split after the first ".exe", which handles both a bare
        // executable and a path with spaces followed by switches.
        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe >= 0)
        {
            var end = exe + 4;
            return Build(text[..end], text[end..].Trim());
        }

        // No .exe at all - MSI-style or a shell verb. Split at the first space.
        var space = text.IndexOf(' ');
        return space < 0
            ? Build(text, string.Empty)
            : Build(text[..space], text[(space + 1)..].Trim());
    }

    private static UninstallCommand Build(string fileName, string arguments) =>
        new(fileName, IsMsiExec(fileName) ? ForRemoval(arguments) : arguments);

    private static bool IsMsiExec(string fileName) =>
        Path.GetFileNameWithoutExtension(fileName)
            .Equals("msiexec", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Turns an MSI command that would repair into one that removes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Windows writes <c>MsiExec.exe /I{GUID}</c> into the uninstall key for a great
    /// many products - 99 of the 134 MSI entries on the machine this was found on.
    /// <c>/I</c> is install mode: run it and the operator gets a repair or modify
    /// dialog, or for a component with no UI, nothing visible at all. It does not
    /// uninstall, which is the whole reason the button appeared not to work.
    /// </para>
    /// <para>
    /// <c>/X</c> is the removal mode, and <c>/uninstall</c> its long form; the
    /// switch is rewritten and everything after it is left exactly as the vendor
    /// wrote it. Nothing is added - no <c>/qn</c>, no <c>/norestart</c> - so msiexec
    /// still asks before it removes anything, which for an irreversible action is a
    /// prompt worth keeping.
    /// </para>
    /// </remarks>
    public static string ForRemoval(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return arguments;

        var text = arguments.TrimStart();
        if (text.Length < 2 || (text[0] != '/' && text[0] != '-')) return arguments;

        var lead = text[0];
        var rest = text[1..];

        // Long forms first: /package is /i by another name, and both take the product
        // as the next token rather than as part of the switch.
        foreach (var (from, to) in new[] { ("package", "uninstall"), ("update", "uninstall") })
        {
            if (rest.StartsWith(from, StringComparison.OrdinalIgnoreCase) &&
                (rest.Length == from.Length || rest[from.Length] is ' ' or '\t'))
            {
                return $"{lead}{to}{rest[from.Length..]}";
            }
        }

        // Short form. The product code usually follows immediately, with no space:
        // "/I{GUID}". Only the letter changes.
        return rest[0] is 'i' or 'I' ? $"{lead}X{rest[1..]}" : arguments;
    }
}
