using FhirAgentBench.Answers;
using FhirAgentBench.Evidence;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Authoring;

public enum SearchCheckOutcome
{
    Match,
    Superset,
    Miss,
    Unresolved,
}

public sealed record SearchCheckResult(
    string QuestionId, SearchCheckOutcome Outcome, IReadOnlyList<string> Missing, IReadOnlyList<string> Extra, string? Reason)
{
    public string Label => Outcome switch
    {
        SearchCheckOutcome.Match => "searches-match",
        SearchCheckOutcome.Superset => "searches-superset",
        SearchCheckOutcome.Miss => "searches-miss",
        _ => "searches-unresolved",
    };

    public bool Fails => Outcome is SearchCheckOutcome.Miss or SearchCheckOutcome.Unresolved;
}

/// <summary>Cross-checks the oracle's evidence against the union of a question's declared searches, as resolved by cli-on-fhir.</summary>
public sealed class SearchChecker(EvidenceResolver resolver, OracleContext context)
{
    public async Task<SearchCheckResult?> CheckAsync(Question question, Answer gold, CancellationToken cancellationToken = default)
    {
        if (question.EvidenceSearches.Count == 0)
        {
            return null;
        }

        var compartment = question.Patient is null ? null : context.PatientKey(question.Patient);
        var resolved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var search in question.EvidenceSearches)
        {
            var resolution = await resolver.ResolveAsync(EvidenceEntry.Parse(search), compartment, cancellationToken).ConfigureAwait(false);
            if (!resolution.IsResolved)
            {
                return new SearchCheckResult(question.Id, SearchCheckOutcome.Unresolved, [], [], $"{search}: {resolution.UnresolvedReason}");
            }

            resolved.UnionWith(resolution.Keys);
        }

        var expected = gold.Evidence.ToHashSet(StringComparer.Ordinal);
        var missing = expected.Except(resolved).Order(StringComparer.Ordinal).ToList();
        var extra = resolved.Except(expected).Order(StringComparer.Ordinal).ToList();
        var outcome = missing.Count > 0 ? SearchCheckOutcome.Miss : extra.Count > 0 ? SearchCheckOutcome.Superset : SearchCheckOutcome.Match;
        return new SearchCheckResult(question.Id, outcome, missing, extra, null);
    }
}
