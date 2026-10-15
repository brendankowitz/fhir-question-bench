using System.Text.Json;
using FhirAgentBench.Corpus;

namespace FhirAgentBench.Oracles;

internal record TimedObservation(JsonElement Resource, DateTimeOffset When)
{
    public string Key => CorpusIndex.KeyOf(Resource);
}

internal sealed record ValidObservation(JsonElement Resource, DateTimeOffset When, string Loinc, decimal Value, string Unit)
    : TimedObservation(Resource, When);

internal static class Observations
{
    private const decimal GlucoseMgDlPerMmolL = 18.016m;
    private const decimal CreatinineUmolLPerMgDl = 88.4m;
    private const string Egfr = "33914-3";

    private static readonly HashSet<string> ValidStatuses = ["final", "amended", "corrected"];
    private static readonly HashSet<string> EgfrUnits = ["mL/min", "mL/min/{1.73_m2}"];

    private static readonly Dictionary<(string From, string To), Func<decimal, decimal>> UnitConversions = new()
    {
        [("kg", "[lb_av]")] = value => value * 2.2046226218m,
        [("cm", "[in_i]")] = value => value / 2.54m,
        [("Cel", "[degF]")] = value => value * 9m / 5m + 32m,
    };

    private static readonly Dictionary<(string Loinc, string From, string To), Func<decimal, decimal>> AnalyteConversions = new()
    {
        [("2339-0", "mg/dL", "mmol/L")] = value => value / GlucoseMgDlPerMmolL,
        [("2339-0", "mmol/L", "mg/dL")] = value => value * GlucoseMgDlPerMmolL,
        [("2345-7", "mg/dL", "mmol/L")] = value => value / GlucoseMgDlPerMmolL,
        [("2345-7", "mmol/L", "mg/dL")] = value => value * GlucoseMgDlPerMmolL,
        [("38483-4", "mg/dL", "umol/L")] = value => value * CreatinineUmolLPerMgDl,
        [("2160-0", "mg/dL", "umol/L")] = value => value * CreatinineUmolLPerMgDl,
    };

    public static bool HasValidStatus(JsonElement observation) =>
        ValidStatuses.Contains(FhirElements.String(observation, "status") ?? "");

    public static DateTimeOffset? Instant(JsonElement observation)
    {
        if (FhirElements.String(observation, "effectiveDateTime") is { } effective)
        {
            return FhirTime.Parse(effective);
        }

        if (observation.TryGetProperty("effectivePeriod", out var period) && FhirElements.String(period, "start") is { } start)
        {
            return FhirTime.Parse(start);
        }

        return FhirElements.String(observation, "issued") is { } issued ? FhirTime.Parse(issued) : null;
    }

    public static IEnumerable<TimedObservation> InWindowWithCode(
        OracleContext context, string patientKey, IReadOnlyCollection<string> loincs, DateTimeOffset from)
    {
        var codes = loincs.ToHashSet(StringComparer.Ordinal);
        foreach (var observation in context.Corpus.ForPatient(patientKey, "Observation"))
        {
            if (HasValidStatus(observation)
                && FhirElements.HasCoding(observation, "code", FhirElements.Loinc, codes)
                && Instant(observation) is { } when
                && when <= context.Now
                && when > from)
            {
                yield return new TimedObservation(observation, when);
            }
        }
    }

    public static IEnumerable<T> NewestFirst<T>(IEnumerable<T> source) where T : TimedObservation =>
        source.OrderByDescending(o => o.When).ThenBy(o => o.Key, StringComparer.Ordinal);

    public static IEnumerable<ValidObservation> Valid(OracleContext context, string patientKey, IReadOnlyCollection<string> loincs)
    {
        foreach (var observation in context.Corpus.ForPatient(patientKey, "Observation"))
        {
            if (!HasValidStatus(observation)
                || Instant(observation) is not { } when
                || when > context.Now
                || Reading(observation, loincs) is not { } reading)
            {
                continue;
            }

            yield return new ValidObservation(observation, when, reading.Loinc, reading.Value, reading.Unit);
        }
    }

    public static (decimal Value, string Unit)? Quantity(JsonElement carrier, string owner)
    {
        if (!carrier.TryGetProperty("valueQuantity", out var quantity) || !quantity.TryGetProperty("value", out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidDataException($"{owner} has a valueQuantity.value that is not a number.");
        }

        var unit = FhirElements.String(quantity, "code") ?? FhirElements.String(quantity, "unit") ?? "";
        return (value.GetDecimal(), unit);
    }

    public static ValidObservation InUnit(ValidObservation observation, string? unit)
    {
        if (unit is null || observation.Unit == unit)
        {
            return observation;
        }

        if (observation.Loinc == Egfr && EgfrUnits.Contains(observation.Unit) && EgfrUnits.Contains(unit))
        {
            return observation with { Unit = unit };
        }

        if (UnitConversions.TryGetValue((observation.Unit, unit), out var convert)
            || AnalyteConversions.TryGetValue((observation.Loinc, observation.Unit, unit), out convert))
        {
            return observation with { Value = convert(observation.Value), Unit = unit };
        }

        throw new InvalidOperationException(
            $"{observation.Key} is recorded in '{observation.Unit}' but the question needs '{unit}', and no conversion is defined for LOINC {observation.Loinc}.");
    }

    private static (string Loinc, decimal Value, string Unit)? Reading(JsonElement observation, IReadOnlyCollection<string> loincs)
    {
        var owner = CorpusIndex.KeyOf(observation);
        if (Carried(observation, loincs, owner) is { } onCode)
        {
            return onCode;
        }

        if (observation.TryGetProperty("component", out var components) && components.ValueKind == JsonValueKind.Array)
        {
            foreach (var component in components.EnumerateArray())
            {
                if (Carried(component, loincs, owner) is { } onComponent)
                {
                    return onComponent;
                }
            }
        }

        return null;
    }

    private static (string Loinc, decimal Value, string Unit)? Carried(
        JsonElement carrier, IReadOnlyCollection<string> loincs, string owner)
    {
        if (!carrier.TryGetProperty("code", out var concept)
            || FhirElements.Codings(concept).FirstOrDefault(c => c.System == FhirElements.Loinc && loincs.Contains(c.Code)) is not { Code: { } loinc }
            || Quantity(carrier, owner) is not { } quantity)
        {
            return null;
        }

        return (loinc, quantity.Value, quantity.Unit);
    }
}
