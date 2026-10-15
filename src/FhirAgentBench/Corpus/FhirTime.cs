using System.Globalization;
using System.Text.Json;

namespace FhirAgentBench.Corpus;

public static class FhirTime
{
    private static readonly string[] InstantProperties =
        ["effectiveDateTime", "authoredOn", "onsetDateTime", "recordedDate", "occurrenceDateTime", "issued"];

    private static readonly string[] PeriodProperties = ["effectivePeriod", "period"];

    public static DateTimeOffset Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Length switch
        {
            4 => new DateTimeOffset(int.Parse(value, CultureInfo.InvariantCulture), 1, 1, 0, 0, 0, TimeSpan.Zero),
            7 => new DateTimeOffset(
                int.Parse(value[..4], CultureInfo.InvariantCulture),
                int.Parse(value[5..7], CultureInfo.InvariantCulture), 1, 0, 0, 0, TimeSpan.Zero),
            10 => new DateTimeOffset(DateTime.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture), TimeSpan.Zero),
            _ => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
        };
    }

    public static DateTimeOffset? Effective(JsonElement resource)
    {
        foreach (var name in InstantProperties)
        {
            if (resource.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return Parse(value.GetString()!);
            }
        }

        foreach (var name in PeriodProperties)
        {
            if (resource.TryGetProperty(name, out var period)
                && period.TryGetProperty("start", out var start)
                && start.ValueKind == JsonValueKind.String)
            {
                return Parse(start.GetString()!);
            }
        }

        return null;
    }

    public static string ToDate(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static int AgeInYears(string birthDate, DateTimeOffset now)
    {
        var birth = Parse(birthDate).UtcDateTime.Date;
        var today = now.UtcDateTime.Date;
        var age = today.Year - birth.Year;
        return today < birth.AddYears(age) ? age - 1 : age;
    }
}
