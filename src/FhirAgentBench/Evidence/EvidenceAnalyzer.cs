using FhirAgentBench.Answers;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Evidence;

/// <summary>Describes an attempt's evidence against the oracle's. Diagnostic only: nothing here changes a grade.</summary>
public sealed class EvidenceAnalyzer(EvidenceResolver? resolver, OracleContext context)
{
    public async Task<EvidenceDiagnostics> AnalyzeAsync(
        Question question, Answer gold, Answer agent, CancellationToken cancellationToken = default)
    {
        var compartment = question.Patient is null ? null : context.PatientKey(question.Patient);
        var accepted = AcceptedSearches(question, compartment);

        var references = 0;
        var searches = 0;
        var invalid = 0;
        var acceptedCited = 0;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var unresolved = new List<string>();

        foreach (var entry in agent.ClassifiedEvidence())
        {
            switch (entry.Kind)
            {
                case EvidenceKind.Reference:
                    references++;
                    keys.Add(entry.Text);
                    break;
                case EvidenceKind.Invalid:
                    invalid++;
                    break;
                default:
                    searches++;
                    if (accepted.Contains(entry.NormalizedSearch(compartment)))
                    {
                        acceptedCited++;
                    }

                    var resolution = resolver is null
                        ? SearchResolution.Unresolved("no evidence resolver configured")
                        : await resolver.ResolveAsync(entry, compartment, cancellationToken).ConfigureAwait(false);
                    if (resolution.IsResolved)
                    {
                        keys.UnionWith(resolution.Keys);
                    }
                    else
                    {
                        unresolved.Add($"{entry.Text}: {resolution.UnresolvedReason}");
                    }

                    break;
            }
        }

        var (recall, precision) = Score(gold.Evidence, keys);
        return new EvidenceDiagnostics(
            references, searches, invalid, unresolved.Count, acceptedCited,
            keys.Order(StringComparer.Ordinal).ToList(), recall, precision, unresolved);
    }

    public static IReadOnlySet<string> AcceptedSearches(Question question, string? patientCompartment) =>
        question.EvidenceSearches
            .Select(search => EvidenceEntry.Parse(search).NormalizedSearch(patientCompartment))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Recall of the oracle's evidence and precision of the cited keys; null where the denominator is empty.</summary>
    public static (double? Recall, double? Precision) Score(IEnumerable<string> oracleKeys, IReadOnlySet<string> citedKeys)
    {
        var expected = oracleKeys.ToHashSet(StringComparer.Ordinal);
        var hits = citedKeys.Count(expected.Contains);
        return (
            expected.Count == 0 ? null : hits / (double)expected.Count,
            citedKeys.Count == 0 ? null : hits / (double)citedKeys.Count);
    }
}
