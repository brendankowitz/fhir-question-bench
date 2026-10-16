using FhirAgentBench.Answers;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed class ProcedureHistoryOracle : IOracle
{
    private static readonly string[] Returns = ["latest_date", "count"];

    public string Name => "procedure-history";

    public Answer Compute(OracleContext context, Question question)
    {
        var parameters = question.Oracle.Parameters;
        var returns = Events.RequireReturn(question, Returns);
        var snomed = parameters.GetStrings("snomed").ToHashSet(StringComparer.Ordinal);
        var from = context.WindowStart(parameters);

        var matching = new List<TimedEvent>();
        foreach (var procedure in context.Corpus.ForPatient(context.PatientKey(question.Patient), "Procedure"))
        {
            if (FhirElements.String(procedure, "status") == "completed"
                && Procedures.PerformedAt(procedure) is { } when
                && Events.IsInWindow(context, when, from)
                && FhirElements.HasCoding(procedure, "code", FhirElements.Snomed, snomed))
            {
                matching.Add(new TimedEvent(procedure, when));
            }
        }

        return Events.Summarize(context, returns, matching);
    }
}
