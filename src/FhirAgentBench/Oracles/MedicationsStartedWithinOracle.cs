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
        var codes = new SortedSet<string>(StringComparer.Ordinal);
        var evidence = new List<string>();
        foreach (var request in context.Corpus.ForPatient(context.PatientKey(question.Patient), "MedicationRequest"))
        {
            var status = FhirElements.String(request, "status");
            if (status == "entered-in-error"
                || (requiredStatus is not null && status != requiredStatus)
                || FhirTime.Effective(request) is not { } when
                || when <= from
                || when > context.Now
                || Medications.RxNormCode(context.Corpus, request) is not { } code)
            {
                continue;
            }

            codes.Add(code);
            evidence.Add(CorpusIndex.KeyOf(request));
        }

        return Answer.Of(codes.Select(code => new { system = FhirElements.RxNorm, code }).ToList(), evidence);
    }
}
