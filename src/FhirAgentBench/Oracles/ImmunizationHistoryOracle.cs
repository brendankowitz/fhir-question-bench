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

        var matching = Immunizations.Completed(context, context.PatientKey(question.Patient), cvx, from);

        return Events.Summarize(context, returns, matching);
    }
}
