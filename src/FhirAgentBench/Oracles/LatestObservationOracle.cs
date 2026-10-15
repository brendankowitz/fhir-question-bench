using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed class LatestObservationOracle : IOracle
{
    public string Name => "latest-observation";

    public Answer Compute(OracleContext context, Question question)
    {
        var patient = context.PatientKey(question.Patient);
        var latest = Observations.Valid(context, patient, question.Oracle.Parameters.GetString("loinc"))
            .OrderByDescending(o => o.When)
            .ThenBy(o => o.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        return latest is null
            ? Answer.Of(null, [])
            : Answer.Of(
                new { value = new { value = latest.Value, unit = latest.Unit }, date = FhirTime.ToDate(latest.When) },
                [latest.Key]);
    }
}
