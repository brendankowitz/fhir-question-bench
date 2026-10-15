using FhirAgentBench.Answers;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed class ObservationWindowStatsOracle : IOracle
{
    private static readonly HashSet<string> Statistics = ["count", "mean", "max", "min", "change", "percent_change"];

    public string Name => "observation-window-stats";

    public Answer Compute(OracleContext context, Question question)
    {
        var parameters = question.Oracle.Parameters;
        var statistic = parameters.GetString("statistic");
        if (!Statistics.Contains(statistic))
        {
            throw new InvalidDataException(
                $"Question '{question.Id}': statistic must be count, mean, max, min, change or percent_change, not '{statistic}'.");
        }

        var matches = parameters.GetOptionalString("comparator") is { } comparator
            ? Comparators.Create(question.Id, comparator, parameters.GetDecimal("threshold"))
            : _ => true;
        var unit = parameters.GetString("unit");
        var from = context.WindowStart(parameters);
        var readings = Observations.Valid(context, context.PatientKey(question.Patient), parameters.GetStringOrList("loinc"))
            .Where(o => o.When > from)
            .Select(o => Observations.InUnit(o, unit))
            .Where(o => matches(o.Value))
            .ToList();
        var evidence = readings.Select(o => o.Key);
        if (statistic == "count")
        {
            return Answer.Of(readings.Count, evidence);
        }

        if (readings.Count == 0 || (statistic is "change" or "percent_change" && readings.Count < 2))
        {
            return Answer.Of(null, []);
        }

        var value = statistic switch
        {
            "mean" => Round(readings.Average(o => o.Value)),
            "max" => readings.Max(o => o.Value),
            "min" => readings.Min(o => o.Value),
            _ => Change(readings, statistic == "percent_change"),
        };
        return Answer.Of(value, evidence);
    }

    private static decimal Change(List<ValidObservation> readings, bool asPercent)
    {
        var earliest = readings.OrderBy(o => o.When).ThenBy(o => o.Key, StringComparer.Ordinal).First().Value;
        var latest = Observations.NewestFirst(readings).First().Value;
        return Round(asPercent ? (latest - earliest) / earliest * 100m : latest - earliest);
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
