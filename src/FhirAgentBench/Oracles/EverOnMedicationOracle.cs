using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed class EverOnMedicationOracle : IOracle
{
    private static readonly HashSet<string> Returns = ["ever_first", "first_date", "latest_date", "order_count"];

    public string Name => "ever-on-medication";

    public Answer Compute(OracleContext context, Question question)
    {
        var parameters = question.Oracle.Parameters;
        var returns = parameters.GetOptionalString("return") ?? "ever_first";
        if (!Returns.Contains(returns))
        {
            throw new InvalidDataException(
                $"Question '{question.Id}': return must be ever_first, first_date, latest_date or order_count, not '{returns}'.");
        }

        var codes = parameters.GetStrings("rxnorm").ToHashSet(StringComparer.Ordinal);
        var from = context.WindowStart(parameters);
        var orders = context.Corpus.ForPatient(context.PatientKey(question.Patient), "MedicationRequest")
            .Where(Medications.IsCountableOrder)
            .Select(r => new { Resource = r, When = FhirTime.Effective(r) })
            .Where(r => r.When is not null
                && r.When <= context.Now
                && r.When > from
                && Medications.RxNormCodes(context.Corpus, r.Resource).Any(codes.Contains))
            .OrderBy(r => r.When)
            .ToList();
        var evidence = orders.Select(o => CorpusIndex.KeyOf(o.Resource));
        var first = orders.Count > 0 ? FhirTime.ToDate(orders[0].When!.Value) : null;
        var latest = orders.Count > 0 ? FhirTime.ToDate(orders[^1].When!.Value) : null;
        return returns switch
        {
            "first_date" => Answer.Of(first, evidence),
            "latest_date" => Answer.Of(latest, evidence),
            "order_count" => Answer.Of(orders.Count, evidence),
            _ => Answer.Of(new { ever = orders.Count > 0, first }, evidence),
        };
    }
}
