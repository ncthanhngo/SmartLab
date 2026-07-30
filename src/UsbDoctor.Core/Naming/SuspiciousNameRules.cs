using System.Globalization;
using System.Text;

namespace UsbDoctor.Core.Naming;

/// <summary>
/// Classifies file and directory names designed to be unreadable, unreachable,
/// or misleading.
/// </summary>
/// <remarks>
/// The rules here are deliberately additive. New worm families invent new hiding
/// tricks; adding one should mean adding a code point to a set, not restructuring
/// the scanner.
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
    /// volume root instead of the folder's real contents.
    /// </para>
    /// <para>
    /// These are written as <c>\uXXXX</c> escapes, never as literal characters.
    /// A set of invisible code points pasted verbatim into source is
    /// unreviewable in a diff and gets silently mangled by editors and tooling
    /// that normalise whitespace — which is exactly the failure mode this class
    /// exists to detect.
    /// </para>
    /// </remarks>
    private static readonly HashSet<char> InvisibleSpaces =
    [
        '\u00A0', // NO-BREAK SPACE  <- used by the worm in the source incident
        '\u1680', // OGHAM SPACE MARK
        '\u2000', // EN QUAD
        '\u2001', // EM QUAD
        '\u2002', // EN SPACE
        '\u2003', // EM SPACE
        '\u2004', // THREE-PER-EM SPACE
        '\u2005', // FOUR-PER-EM SPACE
        '\u2006', // SIX-PER-EM SPACE
        '\u2007', // FIGURE SPACE
        '\u2008', // PUNCTUATION SPACE
        '\u2009', // THIN SPACE
        '\u200A', // HAIR SPACE
        '\u202F', // NARROW NO-BREAK SPACE
        '\u205F', // MEDIUM MATHEMATICAL SPACE
        '\u3000', // IDEOGRAPHIC SPACE
        '\u200B', // ZERO WIDTH SPACE
        '\u200C', // ZERO WIDTH NON-JOINER
        '\u200D', // ZERO WIDTH JOINER
        '\u2060', // WORD JOINER
        '\uFEFF', // ZERO WIDTH NO-BREAK SPACE / BOM
    ];

    /// <summary>
    /// Bidirectional formatting characters. A right-to-left override makes a file
    /// named <c>invoice{U+202E}gnp.exe</c> display as <c>invoiceexe.png</c>.
    /// </summary>
    private static readonly HashSet<char> BidiControls =
    [
        '\u202A', // LEFT-TO-RIGHT EMBEDDING
        '\u202B', // RIGHT-TO-LEFT EMBEDDING
        '\u202C', // POP DIRECTIONAL FORMATTING
        '\u202D', // LEFT-TO-RIGHT OVERRIDE
        '\u202E', // RIGHT-TO-LEFT OVERRIDE
        '\u2066', // LEFT-TO-RIGHT ISOLATE
        '\u2067', // RIGHT-TO-LEFT ISOLATE
        '\u2068', // FIRST STRONG ISOLATE
        '\u2069', // POP DIRECTIONAL ISOLATE
    ];

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
    /// <example>
    /// <c>Describe("\u00A0")</c> returns <c>&lt;U+00A0&gt;</c>, and
    /// <c>Describe("data ")</c> returns <c>data&lt;U+0020&gt;</c>.
    /// </example>
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
