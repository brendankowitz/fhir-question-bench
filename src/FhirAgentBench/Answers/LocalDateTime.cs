using System.Globalization;
using System.Text.RegularExpressions;

namespace FhirAgentBench.Answers;

/// <summary>
/// A datetime answer: <c>YYYY-MM-DD HH:MM[:SS[.fff]]</c>, with <c>T</c> accepted in place of the space. A trailing <c>Z</c>
/// or <c>±HH:MM</c> offset is accepted and ignored: answers are compared as the wall-clock time written, to the minute.
/// </summary>
public static partial class LocalDateTime
{
    public static bool TryParse(string text, out DateTime minute)
    {
        minute = default;
        var match = Pattern().Match(text.Trim());
        if (!match.Success)
        {
            return false;
        }

        var wallClock = $"{match.Groups["date"].Value} {match.Groups["time"].Value}";
        if (!DateTime.TryParseExact(wallClock, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        minute = parsed;
        return true;
    }

    public static bool SameMinute(string gold, string agent) =>
        TryParse(gold, out var expected) && TryParse(agent, out var actual) && expected == actual;

    [GeneratedRegex(@"^(?<date>\d{4}-\d{2}-\d{2})[ T](?<time>\d{2}:\d{2})(?::[0-5]\d(?:\.\d+)?)?(?:Z|[+-]\d{2}:\d{2})?$")]
    private static partial Regex Pattern();
}
