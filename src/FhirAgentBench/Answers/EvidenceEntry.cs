using System.Text.RegularExpressions;

namespace FhirAgentBench.Answers;

public enum EvidenceKind
{
    Reference,
    Search,
    Invalid,
}

/// <summary>One string from an agent's evidence list: a <c>Type/id</c> reference, a FHIR search, or neither.</summary>
public sealed partial record EvidenceEntry
{
    private EvidenceEntry(string text, EvidenceKind kind, string? compartment, string? resourceType, string? query)
    {
        Text = text;
        Kind = kind;
        Compartment = compartment;
        ResourceType = resourceType;
        Query = query;
    }

    public string Text { get; }

    public EvidenceKind Kind { get; }

    public string? Compartment { get; }

    public string? ResourceType { get; }

    public string? Query { get; }

    public static EvidenceEntry Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        if (ReferencePattern().Match(trimmed) is { Success: true } reference)
        {
            return new EvidenceEntry(trimmed, EvidenceKind.Reference, null, reference.Groups["type"].Value, null);
        }

        if (SearchPattern().Match(trimmed) is { Success: true } search)
        {
            var compartment = search.Groups["compartment"].Success ? search.Groups["compartment"].Value : null;
            return new EvidenceEntry(trimmed, EvidenceKind.Search, compartment, search.Groups["type"].Value, search.Groups["query"].Value);
        }

        return new EvidenceEntry(text, EvidenceKind.Invalid, null, null, null);
    }

    /// <summary>The search with its compartment filled in, so parameter order and a missing compartment do not distinguish two spellings.</summary>
    public string NormalizedSearch(string? defaultCompartment = null)
    {
        if (Kind != EvidenceKind.Search)
        {
            throw new InvalidOperationException($"'{Text}' is not a search.");
        }

        var parameters = Query!.Split('&', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal);
        var compartment = Compartment ?? defaultCompartment;
        return $"{(compartment is null ? "" : compartment + "/")}{ResourceType}?{string.Join('&', parameters)}";
    }

    [GeneratedRegex(@"^(?<type>[A-Z][A-Za-z]+)/[A-Za-z0-9\-.]{1,64}$")]
    private static partial Regex ReferencePattern();

    [GeneratedRegex(@"^(?:(?<compartment>Patient/[A-Za-z0-9\-.]{1,64})/)?(?<type>[A-Z][A-Za-z]+)\?(?<query>[^\s\p{C}\-][^\s\p{C}]*)$")]
    private static partial Regex SearchPattern();
}
