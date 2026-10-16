using System.Text.RegularExpressions;

namespace FhirAgentBench.Import;

/// <summary>One row of FHIR-AgentBench's <c>questions_answers_sql_fhir.csv</c>, with the columns the importer uses.</summary>
public sealed partial record UpstreamQuestion(
    string Split,
    string QuestionId,
    string Question,
    string SqlQuery,
    string TrueAnswer,
    string Assumption,
    string PatientFhirId,
    string Template,
    string ValDict,
    string MainTableName,
    bool MappableToFhir,
    string TrueFhirIds)
{
    private static readonly string[] Columns =
    [
        "split", "question_id", "question", "sql_query", "true_answer", "assumption", "patient_fhir_id", "template",
        "val_dict", "main_table_name", "mappable_to_fhir", "true_fhir_ids",
    ];

    public static UpstreamQuestion From(IReadOnlyDictionary<string, string> record)
    {
        var missing = Columns.FirstOrDefault(column => !record.ContainsKey(column));
        if (missing is not null)
        {
            throw new InvalidDataException($"The FHIR-AgentBench CSV has no '{missing}' column.");
        }

        return new UpstreamQuestion(
            record["split"], record["question_id"], record["question"], record["sql_query"], record["true_answer"],
            record["assumption"], record["patient_fhir_id"], record["template"], record["val_dict"], record["main_table_name"],
            string.Equals(record["mappable_to_fhir"], "True", StringComparison.Ordinal), record["true_fhir_ids"]);
    }

    /// <summary>The MIMIC subject_id the question names, from <c>val_dict</c>.</summary>
    public string? SubjectId => SubjectIdPattern().Match(ValDict) is { Success: true } match ? match.Groups[1].Value : null;

    /// <summary>The "Assume the current time is …" time, as written (MIMIC local time), if the assumption states one.</summary>
    public string? CurrentTime => CurrentTimePattern().Match(Assumption) is { Success: true } match ? match.Groups["time"].Value : null;

    /// <summary>The assumption without its current-time sentence, which the prompt carries as "now" instead.</summary>
    public string Hint => string.Join(' ', CurrentTimePattern().Replace(Assumption, " ").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary><c>true_fhir_ids</c> (a Python dict of resource type to ids) as <c>Type/id</c> references.</summary>
    public IReadOnlyList<string> EvidenceReferences =>
        FhirIdGroup().Matches(TrueFhirIds)
            .SelectMany(group => QuotedId().Matches(group.Groups["ids"].Value)
                .Select(id => $"{group.Groups["type"].Value}/{id.Groups[1].Value}"))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    [GeneratedRegex(@"'patient_id':\s*(\d+)")]
    private static partial Regex SubjectIdPattern();

    [GeneratedRegex(@"Assume the current time is (?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\.")]
    private static partial Regex CurrentTimePattern();

    [GeneratedRegex(@"'(?<type>[A-Za-z]+)':\s*\[(?<ids>[^\]]*)\]")]
    private static partial Regex FhirIdGroup();

    [GeneratedRegex(@"'([^']+)'")]
    private static partial Regex QuotedId();
}
