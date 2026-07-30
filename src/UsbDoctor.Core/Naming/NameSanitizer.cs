using System.Buffers;
using System.Text;

namespace UsbDoctor.Core.Naming;

/// <summary>
/// Produces destination names that NTFS will accept, for source names that a
/// damaged FAT volume produced.
/// </summary>
/// <remarks>
/// Corrupt FAT directory entries surface as names built from arbitrary bytes.
/// Writing them to an NTFS rescue target fails with ERROR_INVALID_NAME (123) or
/// ERROR_DIRECTORY_INVALID (267), and in the originating incident that aborted a
/// multi-gigabyte copy partway through. Every rescued name goes through here, and
/// the original is preserved in the manifest so nothing is silently renamed.
/// </remarks>
public sealed class NameSanitizer
{
    private static readonly SearchValues<char> InvalidChars =
        SearchValues.Create(Path.GetInvalidFileNameChars());

    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    private readonly Dictionary<string, int> _collisions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a name safe to create on NTFS, and whether it had to be changed.
    /// </summary>
    public SanitizedName Sanitize(string original)
    {
        var candidate = BuildCandidate(original, out var changed);

        // Disambiguate within this sanitizer's lifetime, so two different corrupt
        // names cannot collapse onto one destination file and overwrite data.
        lock (_collisions)
        {
            if (_collisions.TryGetValue(candidate, out var seen))
            {
                _collisions[candidate] = seen + 1;
                var ext = Path.GetExtension(candidate);
                var stem = Path.GetFileNameWithoutExtension(candidate);
                candidate = $"{stem}_{seen + 1}{ext}";
                changed = true;
            }
            else
            {
                _collisions[candidate] = 0;
            }
        }

        return new SanitizedName(original, candidate, changed);
    }

    private static string BuildCandidate(string original, out bool changed)
    {
        changed = false;

        if (string.IsNullOrEmpty(original))
        {
            changed = true;
            return "_unnamed";
        }

        var sb = new StringBuilder(original.Length);
        foreach (var c in original)
        {
            if (InvalidChars.Contains(c) || char.IsControl(c))
            {
                sb.Append('_');
                changed = true;
            }
            else
            {
                sb.Append(c);
            }
        }

        var result = sb.ToString();

        // Windows drops trailing dots and spaces; do it explicitly so the name we
        // record is the name that ends up on disk.
        var trimmed = result.TrimEnd(' ', '.');
        if (trimmed.Length != result.Length)
        {
            changed = true;
            result = trimmed;
        }

        if (SuspiciousNameRules.IsEffectivelyBlank(result) || result.Length == 0)
        {
            changed = true;
            result = "_blank";
        }

        var stem = Path.GetFileNameWithoutExtension(result);
        if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
        {
            changed = true;
            result = "_" + result;
        }

        return result;
    }
}

/// <param name="Original">The name exactly as read from the source volume.</param>
/// <param name="Safe">The name actually created on the destination.</param>
/// <param name="WasChanged">True when the two differ, so the manifest can record it.</param>
public readonly record struct SanitizedName(string Original, string Safe, bool WasChanged);
