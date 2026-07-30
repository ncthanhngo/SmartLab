using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UsbDoctor.Core.Model;

namespace UsbDoctor.Signatures;

/// <summary>
/// Evaluates signature rules against entries.
/// </summary>
/// <remarks>
/// Hashing is the expensive part, so it is deferred behind a callback and only
/// invoked when a signature actually carries a hash rule and the cheaper name and
/// attribute rules have not already decided the outcome.
/// </remarks>
public sealed class SignatureMatcher(SignatureSet signatures)
{
    private readonly Dictionary<string, Regex> _regexCache = new(StringComparer.Ordinal);

    public IReadOnlyList<ThreatMatch> Match(FileEntry entry, bool isRoot, Func<Stream>? openContent)
    {
        var hits = new List<ThreatMatch>();

        foreach (var sig in signatures.Signatures)
        {
            foreach (var rule in sig.AnyOf)
            {
                if (!Applies(rule, entry, isRoot)) continue;

                var (matched, hash) = Evaluate(rule, entry, openContent);
                if (!matched) continue;

                hits.Add(new ThreatMatch(sig.Id, sig.Severity, entry.Path, sig.Description, hash)
                {
                    IsDirectory = entry.IsDirectory,
                });
                break; // anyOf — one rule is enough
            }
        }

        return hits;
    }

    private static bool Applies(SignatureRule rule, FileEntry entry, bool isRoot)
    {
        if (rule.RootOnly && !isRoot) return false;

        var wantsDirectory = rule.Type == RuleType.DirName;
        if (wantsDirectory != entry.IsDirectory) return false;

        if (rule.RequireAttributes is { Length: > 0 })
        {
            foreach (var name in rule.RequireAttributes)
            {
                if (!Enum.TryParse<EntryAttributes>(name, ignoreCase: true, out var flag)) return false;
                if (!entry.Attributes.HasFlag(flag)) return false;
            }
        }

        return true;
    }

    private (bool Matched, string? Hash) Evaluate(SignatureRule rule, FileEntry entry, Func<Stream>? openContent)
    {
        switch (rule.Type)
        {
            case RuleType.DirName:
            case RuleType.FileName:
                return (rule.Pattern is not null && Regex(rule.Pattern).IsMatch(entry.Name), null);

            case RuleType.Sha256:
            {
                if (rule.Values is not { Length: > 0 } || openContent is null) return (false, null);

                var hash = ComputeSha256(openContent);
                if (hash is null) return (false, null);

                var hit = rule.Values.Any(v => string.Equals(v, hash, StringComparison.OrdinalIgnoreCase));
                return (hit, hit ? hash : null);
            }

            case RuleType.FileContains:
            {
                if (rule.Pattern is null || rule.Contains is null || openContent is null) return (false, null);
                if (!Regex(rule.Pattern).IsMatch(entry.Name)) return (false, null);

                return (ContainsText(openContent, rule.Contains), null);
            }

            default:
                return (false, null);
        }
    }

    private static string? ComputeSha256(Func<Stream> open)
    {
        try
        {
            using var stream = open();
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            // Unreadable content is not a match. Defender also locks files it has
            // already flagged, which surfaces here as a read failure rather than
            // an identification.
            return null;
        }
    }

    private static bool ContainsText(Func<Stream> open, string needle)
    {
        try
        {
            using var stream = open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();

            // desktop.ini is written UTF-16LE by the family this targets; the BOM
            // detection above handles it, and a UTF-8 read of the same bytes would
            // interleave NULs, so compare with those stripped as a fallback.
            return text.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                   text.Replace("\0", "").Contains(needle, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private Regex Regex(string pattern)
    {
        if (_regexCache.TryGetValue(pattern, out var cached)) return cached;

        var regex = new Regex(pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        _regexCache[pattern] = regex;
        return regex;
    }
}
