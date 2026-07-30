using System.Globalization;
using System.Text;

namespace UsbDoctor.Core.Naming;

/// <summary>
/// Classifies file and directory names designed to be unreadable, unreachable,
/// or misleading.
/// </summary>
/// <remarks>
/// The rules here are deliberately additive. New worm families invent new hiding
/// tricks; adding one should mean adding a code point to a list, not
/// restructuring the scanner.
/// </remarks>
public static class SuspiciousNameRules
{
    /// <summary>
    /// Code points that occupy space but render as blank, so a folder named with
    /// one looks like it has no name at all in Explorer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// U+00A0 is the one that mattered in the originating incident: the worm
    /// named its staging folder with a single non-breaking space. Explorer drew
    /// it as an empty label, and Win32 path resolution discarded it — so
    /// <c>E:\{U+00A0}</c> resolved to <c>E:\</c> and every listing returned the
    /// volume root instead of the folder's real 7 GB of contents.
    /// </para>
    /// <para>
    /// These are declared as numeric code points, never as literal characters and
    /// not as <c>\uXXXX</c> escapes either. The source stays pure ASCII, so it
    /// survives any editor, diff, patch tool, or encoding conversion intact. A
    /// table of invisible characters written literally is unreviewable and gets
    /// silently mangled by tooling that normalises whitespace — which is exactly
    /// the failure mode this class exists to detect.
    /// </para>
    /// </remarks>
    private static readonly HashSet<char> InvisibleSpaces = ToCharSet(
        0x00A0, // NO-BREAK SPACE  <- used by the worm in the source incident
        0x1680, // OGHAM SPACE MARK
        0x2000, // EN QUAD
        0x2001, // EM QUAD
        0x2002, // EN SPACE
        0x2003, // EM SPACE
        0x2004, // THREE-PER-EM SPACE
        0x2005, // FOUR-PER-EM SPACE
        0x2006, // SIX-PER-EM SPACE
        0x2007, // FIGURE SPACE
        0x2008, // PUNCTUATION SPACE
        0x2009, // THIN SPACE
        0x200A, // HAIR SPACE
        0x202F, // NARROW NO-BREAK SPACE
        0x205F, // MEDIUM MATHEMATICAL SPACE
        0x3000, // IDEOGRAPHIC SPACE
        0x200B, // ZERO WIDTH SPACE
        0x200C, // ZERO WIDTH NON-JOINER
        0x200D, // ZERO WIDTH JOINER
        0x2060, // WORD JOINER
        0xFEFF);// ZERO WIDTH NO-BREAK SPACE / BOM

    /// <summary>
    /// Bidirectional formatting characters. A right-to-left override makes a file
    /// named <c>invoice{U+202E}gnp.exe</c> display as <c>invoiceexe.png</c>.
    /// </summary>
    private static readonly HashSet<char> BidiControls = ToCharSet(
        0x202A, // LEFT-TO-RIGHT EMBEDDING
        0x202B, // RIGHT-TO-LEFT EMBEDDING
        0x202C, // POP DIRECTIONAL FORMATTING
        0x202D, // LEFT-TO-RIGHT OVERRIDE
        0x202E, // RIGHT-TO-LEFT OVERRIDE
        0x2066, // LEFT-TO-RIGHT ISOLATE
        0x2067, // RIGHT-TO-LEFT ISOLATE
        0x2068, // FIRST STRONG ISOLATE
        0x2069);// POP DIRECTIONAL ISOLATE

    private static HashSet<char> ToCharSet(params int[] codePoints)
    {
        var set = new HashSet<char>(codePoints.Length);
        foreach (var cp in codePoints) set.Add((char)cp);
        return set;
    }

    public static bool ContainsInvisibleSpace(string name) =>
        name.Any(InvisibleSpaces.Contains);

    public static bool ContainsBidiOverride(string name) =>
        name.Any(BidiControls.Contains);

    public static bool ContainsNonPrintable(string name) =>
        name.Any(c => char.IsControl(c) ||
                      char.GetUnicodeCategory(c) == UnicodeCategory.PrivateUse);

    /// <summary>
    /// True when Win32 path resolution would silently alter this name.
    /// </summary>
    /// <remarks>
    /// Windows strips trailing spaces and dots from path components. A directory
    /// created through the raw <c>\\?\</c> interface can therefore hold a name
    /// that no ordinary path string can address — opening it by its printed name
    /// lands on the parent instead.
    /// </remarks>
    public static bool WouldWin32Trim(string name) =>
        name.Length > 0 && (name[^1] is ' ' or '.' || name[0] is ' ');

    /// <summary>True when every character in the name renders as blank.</summary>
    public static bool IsEffectivelyBlank(string name) =>
        name.Length > 0 && name.All(c => c == ' ' || InvisibleSpaces.Contains(c));

    /// <summary>
    /// Renders a name so an operator can see what it actually is, escaping every
    /// character that would otherwise be invisible or misleading.
    /// </summary>
    /// <remarks>
    /// A name of one U+00A0 renders as <c>&lt;U+00A0&gt;</c>; a name ending in a
    /// plain space renders with a trailing <c>&lt;U+0020&gt;</c>. Without this the
    /// UI shows an empty cell and the operator cannot tell what they are deciding
    /// about.
    /// </remarks>
    public static string Describe(string name)
    {
        if (name.Length == 0) return "<empty>";

        var sb = new StringBuilder(name.Length + 8);

        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];

            // A plain interior space is readable and extremely common; escape it
            // only at the edges, where it changes how the path resolves.
            if (c == ' ')
            {
                var atEdge = i == 0 || i == name.Length - 1;
                sb.Append(atEdge ? "<U+0020>" : " ");
                continue;
            }

            if (InvisibleSpaces.Contains(c) || BidiControls.Contains(c) || char.IsControl(c))
            {
                sb.Append(CultureInfo.InvariantCulture, $"<U+{(int)c:X4}>");
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}
