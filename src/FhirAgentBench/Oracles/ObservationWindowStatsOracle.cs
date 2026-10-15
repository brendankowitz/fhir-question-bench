using FhirAgentBench.Answers;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed class ObservationWindowStatsOracle : IOracle
{
    private static readonly HashSet<string> Statistics = ["count", "mean", "max", "min"];

    public string Name => "observation-window-stats";

    public Answer Compute(OracleContext context, Question question)
    {
        var parameters = question.Oracle.Parameters;
        var statistic = parameters.GetString("statistic");
        if (!Statistics.Contains(statistic))
        {
            throw new InvalidDataException($"Question '{question.Id}': statistic must be count, mean, max or min, not '{statistic}'.");
        }

        var loinc = parameters.GetString("loinc");
        var unit = parameters.GetString("unit");
        var from = context.Now.AddDays(-parameters.GetInt("window_days"));
        var inWindow = Observations.Valid(context, context.PatientKey(question.Patient), loinc)
            .Where(o => o.When > from)
            .Select(o => Observations.InUnit(o, loinc, unit))
            .ToList();
        var evidence = inWindow.Select(o => o.Key);
        if (statistic == "count")
        {
            return Answer.Of(inWindow.Count, evidence);
        }

        if (inWindow.Count == 0)
        {
            return Answer.Of(null, []);
        }

        var value = statistic switch
        {
            "mean" => Math.Round(inWindow.Average(o => o.Value), 2, MidpointRounding.AwayFromZero),
            "max" => inWindow.Max(o => o.Value),
            _ => inWindow.Min(o => o.Value),
        };
        return Answer.Of(value, evidence);
    }
}
