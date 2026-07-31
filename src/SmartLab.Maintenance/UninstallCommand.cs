namespace SmartLab.Maintenance;

/// <param name="FileName">Executable to launch.</param>
/// <param name="Arguments">Everything after it, possibly empty.</param>
public readonly record struct UninstallCommand(string FileName, string Arguments)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(FileName);
}

/// <summary>
/// Splits a registered uninstall string into an executable and its arguments.
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
                return new UninstallCommand(
                    text[1..closing],
                    text[(closing + 1)..].Trim());
            }

            // Unbalanced quote: treat the remainder as the path rather than guessing.
            return new UninstallCommand(text.Trim('"'), string.Empty);
        }

        // Unquoted. Split after the first ".exe", which handles both a bare
        // executable and a path with spaces followed by switches.
        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe >= 0)
        {
            var end = exe + 4;
            return new UninstallCommand(text[..end], text[end..].Trim());
        }

        // No .exe at all - MSI-style or a shell verb. Split at the first space.
        var space = text.IndexOf(' ');
        return space < 0
            ? new UninstallCommand(text, string.Empty)
            : new UninstallCommand(text[..space], text[(space + 1)..].Trim());
    }
}
