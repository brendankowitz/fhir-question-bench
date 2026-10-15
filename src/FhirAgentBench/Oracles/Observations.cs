using System.Text.Json;
using FhirAgentBench.Corpus;

namespace FhirAgentBench.Oracles;

internal sealed record ValidObservation(JsonElement Resource, DateTimeOffset When, decimal Value, string Unit)
{
    public string Key => CorpusIndex.KeyOf(Resource);
}

internal static class Observations
{
    private const decimal GlucoseMgDlPerMmolL = 18.016m;

    private static readonly HashSet<string> ValidStatuses = ["final", "amended", "corrected"];

    private static readonly Dictionary<(string Loinc, string From, string To), Func<decimal, decimal>> Conversions = new()
    {
        [("2339-0", "mg/dL", "mmol/L")] = value => value / GlucoseMgDlPerMmolL,
        [("2339-0", "mmol/L", "mg/dL")] = value => value * GlucoseMgDlPerMmolL,
        [("2345-7", "mg/dL", "mmol/L")] = value => value / GlucoseMgDlPerMmolL,
        [("2345-7", "mmol/L", "mg/dL")] = value => value * GlucoseMgDlPerMmolL,
    };

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

            if (value.ValueKind != JsonValueKind.Number)
            {
                throw new InvalidDataException($"{CorpusIndex.KeyOf(observation)} has a valueQuantity.value that is not a number.");
            }

            var unit = FhirElements.String(quantity, "code") ?? FhirElements.String(quantity, "unit") ?? "";
            yield return new ValidObservation(observation, when, value.GetDecimal(), unit);
        }
    }

    public static ValidObservation InUnit(ValidObservation observation, string loinc, string unit)
    {
        if (observation.Unit == unit)
        {
            return observation;
        }

        return Conversions.TryGetValue((loinc, observation.Unit, unit), out var convert)
            ? observation with { Value = convert(observation.Value), Unit = unit }
            : throw new InvalidOperationException(
                $"{observation.Key} is recorded in '{observation.Unit}' but the question needs '{unit}', and no conversion is defined for LOINC {loinc}.");
    }
}
