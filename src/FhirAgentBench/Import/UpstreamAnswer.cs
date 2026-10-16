using System.Text.RegularExpressions;

namespace FhirAgentBench.Import;

/// <summary>
/// FHIR-AgentBench's <c>true_answer</c>: the SQL result printed as a Python list or NumPy array of one-column rows, e.g.
/// <c>[[5.08751157]]</c>, <c>[['Furosemide']\n ['Insulin']]</c>, <c>[[None]]</c> or <c>[]</c>.
/// </summary>
public static partial class UpstreamAnswer
{
    /// <summary>One entry per row: the unquoted text of the cell, or null for <c>None</c>.</summary>
    public static IReadOnlyList<string?> ParseRows(string trueAnswer)
    {
        var text = trueAnswer.Trim();
        if (text.Length < 2 || text[0] != '[' || text[^1] != ']')
        {
            throw new FormatException($"'{trueAnswer}' is not a list of rows.");
        }

        var body = text[1..^1];
        var rows = new List<string?>();
        var position = 0;
        while (position < body.Length)
        {
            var row = Row().Match(body, position);
            if (!row.Success || row.Index != position)
            {
                throw new FormatException($"'{trueAnswer}' has a row that is not a single value at offset {position + 1}.");
            }

            rows.Add(Cell(row.Groups["cell"].Value));
            position = row.Index + row.Length;
        }

        return rows;
    }

    private static string? Cell(string cell) => cell switch
    {
        "None" => null,
        ['\'', .., '\''] or ['"', .., '"'] => cell[1..^1],
        _ => cell,
    };

    [GeneratedRegex("""\G[\s,]*\[\s*(?<cell>'[^']*'|"[^"]*"|None|-?\d+(?:\.\d*)?(?:[eE][+-]?\d+)?)\s*\][\s,]*""")]
    private static partial Regex Row();
}
