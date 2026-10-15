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
            if (!Patients.IsLiving(patient, context.Now)
                || !Patients.IsInAgeRange(patient, context.Now, ageMin, ageMax)
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
}
