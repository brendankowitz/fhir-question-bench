using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed class MedicationsStartedWithinOracle : IOracle
{
    public string Name => "medications-started-within";

    public Answer Compute(OracleContext context, Question question)
    {
        var parameters = question.Oracle.Parameters;
        var from = context.Now.AddDays(-parameters.GetInt("window_days"));
        var requiredStatus = parameters.GetOptionalString("status");

        var ordersByCode = context.Corpus.ForPatient(context.PatientKey(question.Patient), "MedicationRequest")
            .Where(Medications.IsCountableOrder)
            .Select(r => new { Resource = r, When = FhirTime.Effective(r), Code = Medications.RxNormCode(context.Corpus, r) })
            .Where(r => r.Code is not null && r.When is not null && r.When <= context.Now)
            .GroupBy(r => r.Code!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        var codes = new List<string>();
        var evidence = new List<string>();
        foreach (var orders in ordersByCode)
        {
            if (orders.Min(o => o.When) <= from
                || (requiredStatus is not null && !orders.Any(o => FhirElements.String(o.Resource, "status") == requiredStatus)))
            {
                continue;
            }

            codes.Add(orders.Key);
            evidence.AddRange(orders.Select(o => CorpusIndex.KeyOf(o.Resource)));
        }

        return Answer.Of(codes.Select(code => new { system = FhirElements.RxNorm, code }).ToList(), evidence);
    }
}
