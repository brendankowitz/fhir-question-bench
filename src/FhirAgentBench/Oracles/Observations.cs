using System.Text.Json;
using FhirAgentBench.Corpus;

namespace FhirAgentBench.Oracles;

internal sealed record ValidObservation(JsonElement Resource, DateTimeOffset When, decimal Value, string Unit)
{
    public string Key => CorpusIndex.KeyOf(Resource);
}

internal static class Observations
{
    private static readonly HashSet<string> ValidStatuses = ["final", "amended", "corrected"];

    public static IEnumerable<ValidObservation> Valid(OracleContext context, string patientKey, string loinc)
    {
        var codes = new HashSet<string> { loinc };
        foreach (var observation in context.Corpus.ForPatient(patientKey, "Observation"))
        {
            if (!ValidStatuses.Contains(FhirElements.String(observation, "status") ?? "")
                || !FhirElements.HasCoding(observation, "code", FhirElements.Loinc, codes)
                || FhirTime.Effective(observation) is not { } when
                || when > context.Now
                || !observation.TryGetProperty("valueQuantity", out var quantity)
                || !quantity.TryGetProperty("value", out var value))
            {
                continue;
            }

            var unit = FhirElements.String(quantity, "code") ?? FhirElements.String(quantity, "unit") ?? "";
            yield return new ValidObservation(observation, when, value.GetDecimal(), unit);
        }
    }
}
