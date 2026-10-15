using System.Text.Json;
using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed class CohortLabAboveOrMissingOracle : IOracle
{
    public string Name => "cohort-lab-above-or-missing";

    public Answer Compute(OracleContext context, Question question)
    {
        var parameters = question.Oracle.Parameters;
        var conditions = parameters.GetStrings("snomed").ToHashSet(StringComparer.Ordinal);
        var loincs = parameters.GetStringOrList("loinc");
        var unit = parameters.GetString("unit");
        var comparator = parameters.GetOptionalString("comparator") ?? "gt";
        var meetsThreshold = Comparators.Create(question.Id, comparator, parameters.GetDecimal("threshold"));
        var from = context.WindowStart(parameters);
        var ageMin = parameters.GetInt("age_min");
        var ageMax = parameters.GetInt("age_max");

        var counted = new List<string>();
        foreach (var patient in context.Corpus.OfType("Patient"))
        {
            var key = CorpusIndex.KeyOf(patient);
            if (!IsAliveAndInAgeRange(patient, context.Now, ageMin, ageMax)
                || !Conditions.HasActive(context, key, conditions))
            {
                continue;
            }

            var latest = Observations.NewestFirst(Observations.Valid(context, key, loincs).Where(o => o.When > from))
                .FirstOrDefault();
            if (latest is null || meetsThreshold(Observations.InUnit(latest, unit).Value))
            {
                counted.Add(key);
            }
        }

        return Answer.Of(counted.Count, counted);
    }

    private static bool IsAliveAndInAgeRange(JsonElement patient, DateTimeOffset now, int ageMin, int ageMax)
    {
        if (FhirElements.String(patient, "deceasedDateTime") is { } died && FhirTime.Parse(died) <= now)
        {
            return false;
        }

        if (patient.TryGetProperty("deceasedBoolean", out var deceased) && deceased.ValueKind == JsonValueKind.True)
        {
            return false;
        }

        if (FhirElements.String(patient, "birthDate") is not { } birthDate)
        {
            return false;
        }

        var age = FhirTime.AgeInYears(birthDate, now);
        return age >= ageMin && age <= ageMax;
    }
}
