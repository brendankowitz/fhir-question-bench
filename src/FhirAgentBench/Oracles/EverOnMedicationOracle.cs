using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed class EverOnMedicationOracle : IOracle
{
    public string Name => "ever-on-medication";

    public Answer Compute(OracleContext context, Question question)
    {
        var codes = question.Oracle.Parameters.GetStrings("rxnorm").ToHashSet(StringComparer.Ordinal);
        var orders = context.Corpus.ForPatient(context.PatientKey(question.Patient), "MedicationRequest")
            .Where(Medications.IsCountableOrder)
            .Select(r => new { Resource = r, When = FhirTime.Effective(r) })
            .Where(r => r.When is not null
                && r.When <= context.Now
                && Medications.RxNormCodes(context.Corpus, r.Resource).Any(codes.Contains))
            .OrderBy(r => r.When)
            .ToList();
        var first = orders.Count > 0 ? FhirTime.ToDate(orders[0].When!.Value) : null;
        return Answer.Of(new { ever = orders.Count > 0, first }, orders.Select(o => CorpusIndex.KeyOf(o.Resource)));
    }
}
