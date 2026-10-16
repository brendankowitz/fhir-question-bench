using FhirAgentBench.Answers;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed class EncounterQueryOracle : IOracle
{
    private static readonly string[] Returns = ["count", "latest_date"];

    public string Name => "encounter-query";

    public Answer Compute(OracleContext context, Question question)
    {
        var parameters = question.Oracle.Parameters;
        var returns = Events.RequireReturn(question, Returns);
        var classes = parameters.Has("class") ? parameters.GetStringOrList("class").ToHashSet(StringComparer.Ordinal) : null;
        var types = parameters.Has("type_snomed") ? parameters.GetStrings("type_snomed").ToHashSet(StringComparer.Ordinal) : null;
        var from = context.WindowStart(parameters);

        var matching = new List<TimedEvent>();
        foreach (var encounter in context.Corpus.ForPatient(context.PatientKey(question.Patient), "Encounter"))
        {
            if (Encounters.IsCounted(encounter)
                && Encounters.StartedAt(encounter) is { } when
                && Events.IsInWindow(context, when, from)
                && (classes is null || Encounters.ClassCodes(encounter).Any(classes.Contains))
                && (types is null || Encounters.SnomedTypeCodes(encounter).Any(types.Contains)))
            {
                matching.Add(new TimedEvent(encounter, when));
            }
        }

        return Events.Summarize(context, returns, matching);
    }
}
