using System.Globalization;
using System.Text.RegularExpressions;
using FhirAgentBench.Answers;

namespace FhirAgentBench.Import;

public abstract record TypingResult;

public sealed record TypedAnswer(AnswerSpec Spec, object? Gold, int Tier) : TypingResult;

public sealed record SkippedAnswer(string Reason) : TypingResult;

/// <summary>
/// Infers a typed answer spec and gold value for a FHIR-AgentBench row from its SQL and template. The rules, in order:
/// <list type="number">
/// <item><c>SELECT COUNT(*)&gt;0</c>: boolean (0/1).</item>
/// <item><c>SELECT COUNT(</c>: integer.</item>
/// <item>A template starting "Is the" (a comparison of two values): boolean, nullable.</item>
/// <item>gender: text, MIMIC M/F mapped to FHIR's <c>male</c>/<c>female</c>.</item>
/// <item>marital status: a v3-MaritalStatus code, as the FHIR Patient carries it.</item>
/// <item>admission type: skipped; the FHIR conversion carries only lossy class and priority codes.</item>
/// <item>A selected <c>*time</c> column: datetime; drug, specimen, organism, test or care unit names: text; aggregates,
/// <c>valuenum</c>, <c>value</c>, <c>dose_val_rx</c> and date arithmetic: decimal.</item>
/// </list>
/// A template starting "List the" or a <c>SELECT DISTINCT</c> query is a set of those values; any other is single-valued and
/// is skipped when the gold has more than one row. Every type but boolean existence, counts and gender is nullable, so the
/// spec never reveals whether the gold is null; <c>[]</c> and <c>[[None]]</c> become a null gold ("no such result").
/// Tiers approximate the bench's (§5.1): 1 for a template with no filters, 3 for counts, aggregates, comparisons, changes
/// and date arithmetic, 2 otherwise.
/// </summary>
public static partial class AnswerTyping
{
    public const decimal AbsoluteTolerance = 0.01m;

    public const decimal RelativeTolerance = 0.001m;

    public const string MaritalStatusSystem = "http://terminology.hl7.org/CodeSystem/v3-MaritalStatus";

    private static readonly Dictionary<string, string> MaritalStatusCodes = new(StringComparer.Ordinal)
    {
        ["MARRIED"] = "M",
        ["SINGLE"] = "S",
        ["WIDOWED"] = "W",
        ["DIVORCED"] = "D",
    };

    private enum Element
    {
        Decimal,
        DateTime,
        Text,
    }

    public static TypingResult Infer(UpstreamQuestion question)
    {
        IReadOnlyList<string?> rows;
        try
        {
            rows = UpstreamAnswer.ParseRows(question.TrueAnswer);
        }
        catch (FormatException ex)
        {
            return new SkippedAnswer($"answer is not a list of one-column rows: {ex.Message}");
        }

        var sql = Whitespace().Replace(question.SqlQuery.Trim(), " ");
        var template = question.Template;
        var tier = Tier(sql, template);

        if (ExistsQuery().IsMatch(sql))
        {
            return Single(rows, false, value => Flag(value), new AnswerSpec { Type = AnswerType.Boolean }, tier);
        }

        if (CountQuery().IsMatch(sql))
        {
            return Single(rows, false, value => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null,
                new AnswerSpec { Type = AnswerType.Integer }, tier);
        }

        if (template.StartsWith("Is the ", StringComparison.Ordinal))
        {
            return Single(rows, true, value => Flag(value), Nullable(new AnswerSpec { Type = AnswerType.Boolean }), tier);
        }

        if (template.Contains("gender", StringComparison.Ordinal))
        {
            return Single(rows, false, value => value switch { "M" => "male", "F" => "female", _ => null },
                new AnswerSpec { Type = AnswerType.Text }, tier);
        }

        if (template.Contains("marital status", StringComparison.Ordinal))
        {
            return Single(rows, true,
                value => value is not null && MaritalStatusCodes.TryGetValue(value, out var code) ? new { system = MaritalStatusSystem, code } : null,
                Nullable(new AnswerSpec { Type = AnswerType.Code }), tier);
        }

        if (template.Contains("admission type", StringComparison.Ordinal))
        {
            return new SkippedAnswer("admission type: the MIMIC text is not in the FHIR conversion (only lossy Encounter class and priority codes)");
        }

        if (ElementOf(sql) is not { } element)
        {
            return new SkippedAnswer("no typing rule for this SQL shape");
        }

        var itemSpec = ItemSpec(element);
        Func<string?, object?> parse = element switch
        {
            Element.Decimal => value => decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
            Element.DateTime => value => value is not null && LocalDateTime.TryParse(value, out _) ? value : null,
            _ => value => string.IsNullOrWhiteSpace(value) ? null : value,
        };

        var isSet = template.StartsWith("List the ", StringComparison.Ordinal) || sql.StartsWith("SELECT DISTINCT ", StringComparison.Ordinal);
        if (!isSet)
        {
            return Single(rows, true, parse, Nullable(itemSpec), tier);
        }

        var spec = Nullable(new AnswerSpec { Type = AnswerType.Set, Items = itemSpec });
        if (rows.Count == 0 || rows is [null])
        {
            return new TypedAnswer(spec, null, tier);
        }

        var values = new List<object>();
        foreach (var row in rows)
        {
            if (parse(row) is not { } value)
            {
                return new SkippedAnswer($"set row '{row ?? "None"}' is not a {element.ToString().ToLowerInvariant()}");
            }

            values.Add(value);
        }

        return new TypedAnswer(spec, values.Distinct().ToList(), tier);
    }

    private static TypingResult Single(IReadOnlyList<string?> rows, bool nullable, Func<string?, object?> parse, AnswerSpec spec, int tier)
    {
        if (rows.Count > 1)
        {
            return new SkippedAnswer("a single-valued question has more than one gold row");
        }

        var cell = rows.Count == 0 ? null : rows[0];
        if (cell is null)
        {
            return nullable ? new TypedAnswer(spec, null, tier) : new SkippedAnswer("a non-nullable question has an empty or None gold");
        }

        return parse(cell) is { } value
            ? new TypedAnswer(spec, value, tier)
            : new SkippedAnswer($"gold '{cell}' does not fit the inferred {spec.Type.ToString().ToLowerInvariant()} type");
    }

    private static object? Flag(string? value) => value switch
    {
        "1" or "True" => true,
        "0" or "False" => false,
        _ => null,
    };

    private static AnswerSpec ItemSpec(Element element) => element switch
    {
        Element.Decimal => new AnswerSpec { Type = AnswerType.Decimal, Tolerance = AbsoluteTolerance, RelativeTolerance = RelativeTolerance },
        Element.DateTime => new AnswerSpec { Type = AnswerType.DateTime },
        _ => new AnswerSpec { Type = AnswerType.Text },
    };

    private static AnswerSpec Nullable(AnswerSpec spec) => spec with { Nullable = true, NullAcceptsAbstain = true };

    private static Element? ElementOf(string sql)
    {
        if (TimeColumn().IsMatch(sql))
        {
            return Element.DateTime;
        }

        if (NameColumn().IsMatch(sql))
        {
            return Element.Text;
        }

        return NumericSelect().IsMatch(sql) ? Element.Decimal : null;
    }

    private static int Tier(string sql, string template)
    {
        if (!template.Contains('[', StringComparison.Ordinal) && template.Count(c => c == '{') == 1)
        {
            return 1;
        }

        var isAggregate = (CountQuery().IsMatch(sql) && !ExistsQuery().IsMatch(sql))
            || Aggregate().IsMatch(sql)
            || template.StartsWith("Is the ", StringComparison.Ordinal)
            || template.Contains("change", StringComparison.Ordinal);
        return isAggregate ? 3 : 2;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^SELECT COUNT\(\*\)\s*>\s*0\b")]
    private static partial Regex ExistsQuery();

    [GeneratedRegex(@"^SELECT COUNT\(")]
    private static partial Regex CountQuery();

    [GeneratedRegex(@"^SELECT (DISTINCT )?\w+\.\w*time\b")]
    private static partial Regex TimeColumn();

    [GeneratedRegex(@"^SELECT (DISTINCT )?\w+\.(drug|spec_type_desc|org_name|test_name|careunit)\b")]
    private static partial Regex NameColumn();

    [GeneratedRegex(@"^SELECT (DISTINCT )?(\(|AVG\(|SUM\(|MIN\(|MAX\(|\w+\.valuenum\b|\w+\.value\b|\w+\.dose_val_rx\b|\d+ \* \(|strftime\('%J')")]
    private static partial Regex NumericSelect();

    [GeneratedRegex(@"\b(AVG|SUM|MIN|MAX)\(|strftime\('%J'")]
    private static partial Regex Aggregate();
}
