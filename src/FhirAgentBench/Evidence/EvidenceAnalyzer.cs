using System.Diagnostics;
using FhirAgentBench.Answers;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Evidence;

/// <summary>Describes an attempt's evidence against the oracle's. Diagnostic only: nothing here changes a grade.</summary>
public sealed class EvidenceAnalyzer(EvidenceResolver? resolver, OracleContext context, TimeSpan? answerBudget = null)
{
    public const int MaxEntries = 25;

    public static readonly TimeSpan DefaultAnswerBudget = TimeSpan.FromMinutes(15);

    private readonly TimeSpan _answerBudget = answerBudget ?? DefaultAnswerBudget;

    public async Task<EvidenceDiagnostics> AnalyzeAsync(
        Question question, Answer gold, Answer agent, CancellationToken cancellationToken = default)
    {
        var compartment = question.Patient is null ? null : context.PatientKey(question.Patient);
        var accepted = AcceptedSearches(question, compartment);
        var entries = agent.ClassifiedEvidence();

        var references = 0;
        var searches = 0;
        var invalid = 0;
        var acceptedCited = 0;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var unresolved = new List<string>();
        var notes = new List<string>();
        var clock = Stopwatch.StartNew();

        foreach (var entry in entries.Take(MaxEntries))
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

                    var resolution = await ResolveAsync(entry, compartment, clock, cancellationToken).ConfigureAwait(false);
                    notes.AddRange(resolution.Notes.Select(note => $"{entry.Text}: {note}"));
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

        var (recall, precision) = unresolved.Count > 0 ? ((double?)null, (double?)null) : Score(gold.Evidence, keys);
        return new EvidenceDiagnostics(
            references, searches, invalid, unresolved.Count, acceptedCited, Math.Max(0, entries.Count - MaxEntries),
            keys.Order(StringComparer.Ordinal).ToList(), recall, precision, unresolved, notes);
    }

    public static IReadOnlySet<string> AcceptedSearches(Question question, string? patientCompartment) =>
        question.EvidenceSearches
            .Select(search => EvidenceEntry.Parse(search).NormalizedSearch(patientCompartment))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Recall of the oracle's evidence and precision of the cited keys; null where the denominator is empty.</summary>
    public static (double? Recall, double? Precision) Score(IEnumerable<string> oracleKeys, IReadOnlySet<string> citedKeys)
    {
        var expected = oracleKeys.ToHashSet(StringComparer.Ordinal);
        if (expected.Count == 0)
        {
            return (null, null);
        }

        var hits = citedKeys.Count(expected.Contains);
        return (hits / (double)expected.Count, citedKeys.Count == 0 ? null : hits / (double)citedKeys.Count);
    }

    private async Task<SearchResolution> ResolveAsync(EvidenceEntry entry, string? compartment, Stopwatch clock, CancellationToken cancellationToken)
    {
        if (resolver is null)
        {
            return SearchResolution.Unresolved("no evidence resolver configured");
        }

        var remaining = _answerBudget - clock.Elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            return SearchResolution.Unresolved("the resolver time budget for this answer was used up");
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(remaining);
        try
        {
            return await resolver.ResolveAsync(entry, compartment, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SearchResolution.Unresolved("the resolver time budget for this answer was used up");
        }
    }
}
