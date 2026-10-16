namespace FhirAgentBench.Evidence;

/// <summary>What an attempt's evidence list contained and how much of the oracle's evidence it reaches. Never affects the grade.</summary>
public sealed record EvidenceDiagnostics(
    int References,
    int Searches,
    int Invalid,
    int Unresolved,
    int AcceptedSearches,
    IReadOnlyList<string> ResolvedKeys,
    double? Recall,
    double? Precision,
    IReadOnlyList<string> UnresolvedReasons);
