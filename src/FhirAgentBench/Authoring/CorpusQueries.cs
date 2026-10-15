using System.Text.Json;
using FhirAgentBench.Corpus;
using FhirAgentBench.Oracles;

namespace FhirAgentBench.Authoring;

public sealed record PatientRow(string Mrn, string Name, string BirthDate, int Count, string LatestDate);

public sealed record CodeRow(string System, string Code, string Display, int Count);

public static class CorpusQueries
{
    public static DateTimeOffset? LatestObservationTime(CorpusIndex index) =>
        index.OfType("Observation").Select(FhirTime.Effective).Where(t => t is not null).Max();

    public static IReadOnlyList<PatientRow> PatientsWithObservation(OracleContext context, string loinc)
    {
        var rows = new List<PatientRow>();
        foreach (var patient in context.Corpus.OfType("Patient"))
        {
            var observations = Observations.Valid(context, CorpusIndex.KeyOf(patient), loinc).ToList();
            if (observations.Count == 0 || Mrn(patient, context.MrnSystem) is not { } mrn)
            {
                continue;
            }

            rows.Add(new PatientRow(
                mrn,
                Name(patient),
                FhirElements.String(patient, "birthDate") ?? "",
                observations.Count,
                FhirTime.ToDate(observations.Max(o => o.When))));
        }

        return rows.OrderByDescending(r => r.Count).ThenBy(r => r.Mrn, StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<CodeRow> CodeCounts(CorpusIndex index, string type, string displayContains)
    {
        var counts = new Dictionary<(string, string, string), int>();
        foreach (var resource in index.OfType(type))
        {
            var concept = type == "MedicationRequest"
                ? Medications.Concept(index, resource)
                : resource.TryGetProperty("code", out var code) ? code : (JsonElement?)null;
            if (concept is null)
            {
                continue;
            }

            foreach (var coding in FhirElements.Codings(concept.Value))
            {
                var display = coding.Display ?? "";
                if (display.Contains(displayContains, StringComparison.OrdinalIgnoreCase))
                {
                    var key = (coding.System, coding.Code, display);
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
            }
        }

        return counts.Select(c => new CodeRow(c.Key.Item1, c.Key.Item2, c.Key.Item3, c.Value))
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.Code, StringComparer.Ordinal)
            .ToList();
    }

    private static string? Mrn(JsonElement patient, string mrnSystem) =>
        patient.TryGetProperty("identifier", out var identifiers)
            ? identifiers.EnumerateArray()
                .Where(i => FhirElements.String(i, "system") == mrnSystem)
                .Select(i => FhirElements.String(i, "value"))
                .FirstOrDefault()
            : null;

    private static string Name(JsonElement patient)
    {
        if (!patient.TryGetProperty("name", out var names) || names.GetArrayLength() == 0)
        {
            return "";
        }

        var name = names[0];
        var given = name.TryGetProperty("given", out var g) && g.GetArrayLength() > 0 ? g[0].GetString() : "";
        return $"{given} {FhirElements.String(name, "family")}".Trim();
    }
}
