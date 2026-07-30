using System.Text.Json;
using System.Text.Json.Serialization;
using UsbDoctor.Core.Model;

namespace UsbDoctor.Signatures;

public enum SignatureAction { Report, Quarantine, Delete }

public enum RuleType
{
    /// <summary>Regex against a directory name.</summary>
    DirName,
    /// <summary>Regex against a file name.</summary>
    FileName,
    /// <summary>SHA-256 of file content, hex, case-insensitive.</summary>
    Sha256,
    /// <summary>File whose name matches <c>Pattern</c> and whose text contains <c>Contains</c>.</summary>
    FileContains,
}

public sealed record SignatureRule
{
    [JsonPropertyName("type")]
    public RuleType Type { get; init; }

    /// <summary>Regex applied to the entry name.</summary>
    [JsonPropertyName("pattern")]
    public string? Pattern { get; init; }

    /// <summary>Literal substring for <see cref="RuleType.FileContains"/>.</summary>
    [JsonPropertyName("contains")]
    public string? Contains { get; init; }

    /// <summary>Accepted hashes for <see cref="RuleType.Sha256"/>.</summary>
    [JsonPropertyName("values")]
    public string[]? Values { get; init; }

    /// <summary>All listed attributes must be present for the rule to fire.</summary>
    [JsonPropertyName("requireAttributes")]
    public string[]? RequireAttributes { get; init; }

    /// <summary>Restricts the rule to entries directly under the volume root.</summary>
    [JsonPropertyName("rootOnly")]
    public bool RootOnly { get; init; }
}

public sealed record SignatureDefinition
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("severity")]
    public Severity Severity { get; init; } = Severity.Medium;

    [JsonPropertyName("action")]
    public SignatureAction Action { get; init; } = SignatureAction.Report;

    [JsonPropertyName("anyOf")]
    public SignatureRule[] AnyOf { get; init; } = [];
}

public sealed record SignatureSet
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("signatures")]
    public SignatureDefinition[] Signatures { get; init; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public static SignatureSet Parse(string json) =>
        JsonSerializer.Deserialize<SignatureSet>(json, Options)
        ?? throw new InvalidDataException("Signature file deserialised to null.");

    /// <summary>Loads the signatures compiled into the assembly.</summary>
    public static SignatureSet LoadBuiltIn()
    {
        var asm = typeof(SignatureSet).Assembly;
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("builtin-signatures.json", StringComparison.Ordinal));

        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>
    /// Merges user-supplied signatures over the built-ins, so a field update can
    /// ship as a JSON file without rebuilding the application.
    /// </summary>
    public SignatureSet MergeWith(SignatureSet other)
    {
        var byId = Signatures.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var sig in other.Signatures)
            byId[sig.Id] = sig;

        return this with { Signatures = [.. byId.Values] };
    }
}
