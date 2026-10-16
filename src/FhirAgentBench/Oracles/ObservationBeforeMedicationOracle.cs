using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed class ObservationBeforeMedicationOracle : IOracle
{
    public string Name => "observation-before-medication";

    public Answer Compute(OracleContext context, Question question)
    {
        var parameters = question.Oracle.Parameters;
        var patient = context.PatientKey(question.Patient);
        var rxnorm = parameters.GetStrings("rxnorm").ToHashSet(StringComparer.Ordinal);
        var loincs = parameters.GetStringOrList("loinc");

        var anchor = Medications.CountableOrders(context, patient, rxnorm, DateTimeOffset.MinValue)
            .OrderBy(o => o.When).ThenBy(o => o.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        if (anchor is null)
        {
            return Answer.Of(null, []);
        }

        var latest = Observations.NewestFirst(Observations.Valid(context, patient, loincs).Where(o => o.When <= anchor.When))
            .FirstOrDefault();
        if (latest is null)
        {
            return Answer.Of(null, []);
        }

        latest = Observations.InUnit(latest, parameters.GetOptionalString("unit"));
        return Answer.Of(
            new { value = new { value = latest.Value, unit = latest.Unit }, date = FhirTime.ToDate(latest.When) },
            [anchor.Key, latest.Key]);
    }
}
