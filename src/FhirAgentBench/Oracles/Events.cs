using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

internal static class Events
{
    public static bool IsInWindow(OracleContext context, DateTimeOffset when, DateTimeOffset from) =>
        when <= context.Now && when > from;

    public static IEnumerable<TimedEvent> NewestFirst(IEnumerable<TimedEvent> source) =>
        source.OrderByDescending(e => e.When).ThenBy(e => e.Key, StringComparer.Ordinal);

    public static IEnumerable<TimedEvent> OldestFirst(IEnumerable<TimedEvent> source) =>
        source.OrderBy(e => e.When).ThenBy(e => e.Key, StringComparer.Ordinal);

    public static string RequireReturn(Question question, IReadOnlyCollection<string> allowed)
    {
        var returns = question.Oracle.Parameters.GetString("return");
        return allowed.Contains(returns)
            ? returns
            : throw new InvalidDataException(
                $"Question '{question.Id}': return must be one of {string.Join(", ", allowed)}, not '{returns}'.");
    }

    public static Answer Summarize(OracleContext context, string returns, IReadOnlyList<TimedEvent> events)
    {
        if (returns == "count")
        {
            return Answer.Of(events.Count, events.Select(e => e.Key));
        }

        if (events.Count == 0)
        {
            return Answer.Of(null, []);
        }

        var chosen = returns == "first_date" ? OldestFirst(events).First() : NewestFirst(events).First();
        return returns == "days_since_latest"
            ? Answer.Of((context.Now.UtcDateTime.Date - chosen.When.UtcDateTime.Date).Days, [chosen.Key])
            : Answer.Of(FhirTime.ToDate(chosen.When), [chosen.Key]);
    }
}
