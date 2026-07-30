namespace UsbDoctor.Core.Paths;

/// <summary>
/// A path kept permanently in Win32 extended-length form (<c>\\?\</c>).
/// </summary>
/// <remarks>
/// <para>
/// This type exists because the incident that motivated this tool involved a
/// directory whose name was a single U+00A0 NON-BREAKING SPACE. Ordinary Win32
/// path handling strips trailing whitespace from path components, so
/// <c>E:\{U+00A0}</c> silently resolved to <c>E:\</c> — every listing of the
/// folder returned the volume root instead, and the real contents were
/// unreachable. Only the <c>\\?\</c> form, which disables all normalisation,
/// could address it.
/// </para>
/// <para>
/// The rule for the whole codebase: convert to <see cref="ExtendedPath"/> at the
/// boundary, keep it all the way down, and unwrap only when rendering text for a
/// human. Never hand a raw string to a Win32 call.
/// </para>
/// </remarks>
public readonly record struct ExtendedPath
{
    public const string Prefix = @"\\?\";
    public const string UncPrefix = @"\\?\UNC\";

    private readonly string? _value;

    private ExtendedPath(string value) => _value = value;

    /// <summary>The full path, always including the <c>\\?\</c> prefix.</summary>
    public string Value =>
        _value ?? throw new InvalidOperationException("Uninitialised ExtendedPath.");

    /// <summary>
    /// Normalises caller-supplied input (relative paths, <c>..</c>, forward
    /// slashes) into extended form.
    /// </summary>
    /// <remarks>
    /// Runs the input through <see cref="Path.GetFullPath(string)"/>, which
    /// applies Win32 normalisation — including trimming trailing spaces and
    /// dots. That is correct for text a user typed, but destructive for a name
    /// read off a damaged volume. For those, use <see cref="FromRaw"/>.
    /// </remarks>
    public static ExtendedPath From(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (path.StartsWith(Prefix, StringComparison.Ordinal))
            return new ExtendedPath(path);

        // \\server\share  ->  \\?\UNC\server\share
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return new ExtendedPath(UncPrefix + path[2..]);

        return new ExtendedPath(Prefix + Path.GetFullPath(path));
    }

    /// <summary>
    /// Wraps an already-absolute path <b>without any normalisation</b>.
    /// </summary>
    /// <remarks>
    /// Use this for paths assembled from directory-enumeration results. Those
    /// names may legitimately end in a space, contain U+00A0, or hold bytes that
    /// no normaliser should be allowed to touch — normalising them is exactly
    /// how the original data was lost.
    /// </remarks>
    public static ExtendedPath FromRaw(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        return absolutePath.StartsWith(Prefix, StringComparison.Ordinal)
            ? new ExtendedPath(absolutePath)
            : new ExtendedPath(Prefix + absolutePath);
    }

    /// <summary>Builds a child path by plain concatenation — never normalised.</summary>
    public ExtendedPath Child(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var root = Value.EndsWith('\\') ? Value[..^1] : Value;
        return new ExtendedPath(root + '\\' + name);
    }

    /// <summary>The final path component, preserved byte-for-byte.</summary>
    public string Name
    {
        get
        {
            var v = Value;
            var idx = v.LastIndexOf('\\');
            return idx < 0 || idx == v.Length - 1 ? string.Empty : v[(idx + 1)..];
        }
    }

    /// <summary>The parent path, or <c>null</c> at the root.</summary>
    public ExtendedPath? Parent
    {
        get
        {
            var v = Value;
            var idx = v.LastIndexOf('\\');
            if (idx <= Prefix.Length) return null;
            return new ExtendedPath(v[..idx]);
        }
    }

    /// <summary>
    /// The prefix-free form, for logs and UI only. Never pass this to a Win32
    /// call — it is exactly the form that loses pathological names.
    /// </summary>
    public string ForDisplay()
    {
        var v = Value;
        if (v.StartsWith(UncPrefix, StringComparison.Ordinal))
            return @"\\" + v[UncPrefix.Length..];
        return v.StartsWith(Prefix, StringComparison.Ordinal) ? v[Prefix.Length..] : v;
    }

    public override string ToString() => Value;
}
