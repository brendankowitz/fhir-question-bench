using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed class ImmunizationHistoryOracle : IOracle
{
    private static readonly string[] Returns = ["latest_date", "first_date", "count", "days_since_latest"];

    public string Name => "immunization-history";

    public Answer Compute(OracleContext context, Question question)
    {
        var parameters = question.Oracle.Parameters;
        var returns = Events.RequireReturn(question, Returns);
        var cvx = parameters.GetStrings("cvx").ToHashSet(StringComparer.Ordinal);
        var from = context.WindowStart(parameters);

        var matching = new List<TimedEvent>();
        foreach (var immunization in context.Corpus.ForPatient(context.PatientKey(question.Patient), "Immunization"))
        {
            if (FhirElements.String(immunization, "status") == "completed"
                && FhirElements.String(immunization, "occurrenceDateTime") is { } occurrence
                && FhirTime.Parse(occurrence) is var when
                && Events.IsInWindow(context, when, from)
                && FhirElements.HasCoding(immunization, "vaccineCode", FhirElements.Cvx, cvx))
            {
                matching.Add(new TimedEvent(immunization, when));
            }
        }

        return Events.Summarize(context, returns, matching);
    }
}
