using FhirAgentBench.Answers;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed class ObservationWindowStatsOracle : IOracle
{
    public string Name => "observation-window-stats";

    public Answer Compute(OracleContext context, Question question)
    {
        var parameters = question.Oracle.Parameters;
        var from = context.Now.AddDays(-parameters.GetInt("window_days"));
        var inWindow = Observations.Valid(context, context.PatientKey(question.Patient), parameters.GetString("loinc"))
            .Where(o => o.When > from)
            .ToList();
        var evidence = inWindow.Select(o => o.Key);
        var statistic = parameters.GetString("statistic");
        if (statistic == "count")
        {
            return Answer.Of(inWindow.Count, evidence);
        }

        if (inWindow.Count == 0)
        {
            return Answer.Of(null, []);
        }

        decimal value = statistic switch
        {
            "mean" => Math.Round(inWindow.Average(o => o.Value), 2, MidpointRounding.AwayFromZero),
            "max" => inWindow.Max(o => o.Value),
            "min" => inWindow.Min(o => o.Value),
            _ => throw new InvalidDataException($"Question '{question.Id}': statistic must be count, mean, max or min, not '{statistic}'."),
        };
        return Answer.Of(value, evidence);
    }
}
