namespace FhirAgentBench.Evidence;

/// <summary>
/// What an attempt's evidence list contained and how much of the oracle's evidence it reaches. Never affects the grade.
/// Recall and precision are null when any search was unresolved (the cited set is incomplete) or the denominator is empty;
/// <c>Ignored</c> counts entries beyond the analysis cap; <c>Notes</c> lists parameters removed before searches were resolved.
/// </summary>
public sealed record EvidenceDiagnostics(
    int References,
    int Searches,
    int Invalid,
    int Unresolved,
    int AcceptedSearches,
    int Ignored,
    IReadOnlyList<string> ResolvedKeys,
    double? Recall,
    double? Precision,
    IReadOnlyList<string> UnresolvedReasons,
    IReadOnlyList<string> Notes);
