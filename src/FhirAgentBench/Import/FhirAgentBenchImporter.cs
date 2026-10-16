using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FhirAgentBench.Answers;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Import;

/// <summary>A Patient in the corpus, keyed by the MIMIC subject_id it carries as its MRN.</summary>
public sealed record CorpusPatient(string ResourceId, PatientContext Context);

public sealed record ImportedQuestion(UpstreamQuestion Source, TypedAnswer Answer, CorpusPatient Patient)
{
    public string Id => FhirAgentBenchImporter.QuestionIdPrefix + Source.QuestionId;
}

public sealed record ImportResult(
    string Split,
    int Rows,
    IReadOnlyList<ImportedQuestion> Imported,
    IReadOnlyDictionary<string, int> Skipped,
    IReadOnlyList<ImportedQuestion>? Sample,
    int? Seed)
{
    public string Report()
    {
        var text = new StringBuilder();
        text.AppendLine($"FHIR-AgentBench import, split '{Split}': {Rows} rows, {Imported.Count} imported, {Skipped.Values.Sum()} skipped.");
        text.AppendLine();
        text.AppendLine("Skipped, by reason:");
        foreach (var (reason, count) in Skipped.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            text.AppendLine($"  {count,4}  {reason}");
        }

        AppendTypes(text, $"Answer types, all {Imported.Count} imported:", Imported);
        if (Sample is not null)
        {
            AppendTypes(text, $"Answer types, sample of {Sample.Count} (seed {Seed}):", Sample);
            text.AppendLine();
            text.AppendLine("Sample by main_table_name (sampled / imported):");
            var importedByTable = Imported.GroupBy(q => q.Source.MainTableName).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            foreach (var group in Sample.GroupBy(q => q.Source.MainTableName).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                text.AppendLine($"  {group.Key,-20} {group.Count(),3} / {importedByTable[group.Key]}");
            }
        }

        return text.ToString();
    }

    private static void AppendTypes(StringBuilder text, string title, IReadOnlyList<ImportedQuestion> questions)
    {
        text.AppendLine();
        text.AppendLine(title);
        foreach (var group in questions.GroupBy(q => FhirAgentBenchImporter.Label(q.Answer.Spec)).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            text.AppendLine($"  {group.Count(),4}  {group.Key} ({group.Count(q => q.Answer.Gold is null)} with null gold)");
        }
    }
}

/// <summary>
/// Turns FHIR-AgentBench rows into bench questions over the MIMIC-IV FHIR demo: one question YAML per typeable row, gold in a
/// separate JSON file for the <c>static-answer</c> oracle, and question-set files. Untypeable rows are skipped and counted.
/// </summary>
public static class FhirAgentBenchImporter
{
    public const string QuestionIdPrefix = "fa-";

    public const string Persona = "physician";

    private static readonly TimeZoneInfo MimicTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static readonly JsonSerializerOptions GoldJson = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions YamlString = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static ImportResult Convert(
        IReadOnlyList<UpstreamQuestion> rows, string split, IReadOnlyDictionary<string, CorpusPatient> patientsBySubjectId,
        int? sampleSize, int seed)
    {
        var skipped = new Dictionary<string, int>(StringComparer.Ordinal);
        var imported = new List<ImportedQuestion>();
        var inSplit = rows.Where(row => row.Split == split).ToList();
        foreach (var row in inSplit)
        {
            if (Skip(row, patientsBySubjectId) is { } reason)
            {
                skipped[reason] = skipped.GetValueOrDefault(reason) + 1;
                continue;
            }

            switch (AnswerTyping.Infer(row))
            {
                case TypedAnswer typed:
                    imported.Add(new ImportedQuestion(row, typed, patientsBySubjectId[row.SubjectId!]));
                    break;
                case SkippedAnswer skip:
                    skipped[skip.Reason] = skipped.GetValueOrDefault(skip.Reason) + 1;
                    break;
            }
        }

        var sample = sampleSize is { } size
            ? StratifiedSample.Select(imported, size, seed, q => q.Source.MainTableName, q => q.Id)
            : null;
        return new ImportResult(split, inSplit.Count, imported, skipped, sample, sampleSize is null ? null : seed);
    }

    /// <summary>
    /// Writes <c>questions/&lt;id&gt;.yaml</c>, <c>gold-&lt;split&gt;.json</c>, <c>&lt;split&gt;-all.txt</c>,
    /// <c>&lt;split&gt;-&lt;N&gt;.txt</c> for a sample, and <c>import-report-&lt;split&gt;.txt</c> under the output folder.
    /// </summary>
    public static IReadOnlyList<string> Write(ImportResult result, string outputDirectory, string provenance)
    {
        var questionsDirectory = Directory.CreateDirectory(Path.Combine(outputDirectory, "questions")).FullName;
        var goldFileName = $"gold-{result.Split}.json";
        var written = new List<string>();

        foreach (var question in result.Imported)
        {
            var path = Path.Combine(questionsDirectory, question.Id + ".yaml");
            File.WriteAllText(path, QuestionYaml(question, $"../{goldFileName}", provenance));
        }

        written.Add($"{result.Imported.Count} question files in {questionsDirectory}");

        var gold = result.Imported.ToDictionary(
            q => q.Id,
            q => JsonDocument.Parse(AnswerJson.Serialize(Answer.Of(q.Answer.Gold, q.Source.EvidenceReferences))).RootElement,
            StringComparer.Ordinal);
        written.Add(WriteFile(Path.Combine(outputDirectory, goldFileName), JsonSerializer.Serialize(new SortedDictionary<string, JsonElement>(gold, StringComparer.Ordinal), GoldJson) + "\n"));

        written.Add(WriteSet(outputDirectory, $"{result.Split}-all.txt", result.Imported, $"All {result.Imported.Count} imported '{result.Split}' questions. {provenance}"));
        if (result.Sample is { } sample)
        {
            written.Add(WriteSet(outputDirectory, $"{result.Split}-{sample.Count}.txt", sample,
                $"{sample.Count} '{result.Split}' questions stratified by main_table_name, seed {result.Seed}. {provenance}"));
        }

        written.Add(WriteFile(Path.Combine(outputDirectory, $"import-report-{result.Split}.txt"), result.Report()));
        return written;
    }

    public static string Label(AnswerSpec spec) =>
        (spec.Items is { } items ? $"set of {Name(items.Type)}" : Name(spec.Type)) + (spec.Nullable ? ", nullable" : "");

    /// <summary>The question's "now": its stated MIMIC local time with the New York offset the FHIR conversion uses.</summary>
    public static string? Now(UpstreamQuestion question)
    {
        if (question.CurrentTime is not { } time)
        {
            return null;
        }

        var local = DateTime.ParseExact(time, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return new DateTimeOffset(local, MimicTimeZone.GetUtcOffset(local)).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
    }

    private static string? Skip(UpstreamQuestion row, IReadOnlyDictionary<string, CorpusPatient> patients)
    {
        if (!row.MappableToFhir)
        {
            return "not mappable to FHIR (mappable_to_fhir is False)";
        }

        if (row.SubjectId is not { } subject)
        {
            return "no patient_id in val_dict";
        }

        if (!patients.TryGetValue(subject, out var patient))
        {
            return "patient's subject_id is not an MRN in the corpus";
        }

        return patient.ResourceId == row.PatientFhirId ? null : "patient_fhir_id differs from the corpus Patient with that subject_id";
    }

    private static string QuestionYaml(ImportedQuestion question, string goldFile, string provenance)
    {
        var source = question.Source;
        var patient = question.Patient.Context;
        var yaml = new StringBuilder();
        yaml.AppendLine($"# Imported from FHIR-AgentBench ({provenance}); do not edit, re-run 'fab import fhir-agentbench'.");
        yaml.AppendLine($"# upstream: split={source.Split} question_id={source.QuestionId} main_table_name={source.MainTableName}");
        yaml.AppendLine($"id: {question.Id}");
        yaml.AppendLine($"tier: {question.Answer.Tier}");
        yaml.AppendLine($"template: {Quote(source.Template)}");
        yaml.AppendLine("tags: [EXT]");
        yaml.AppendLine($"persona: {Persona}");
        yaml.AppendLine($"patient: {{ mrn: {Quote(patient.Mrn)}, name: {Quote(patient.Name)}, birth_date: {Quote(patient.BirthDate)} }}");
        if (Now(source) is { } now)
        {
            yaml.AppendLine($"now: {Quote(now)}");
        }

        if (source.Hint.Length > 0)
        {
            yaml.AppendLine($"assumption: {Quote(source.Hint)}");
        }

        yaml.AppendLine($"question: {Quote(source.Question)}");
        yaml.AppendLine($"answer: {SpecYaml(question.Answer.Spec)}");
        yaml.AppendLine($"oracle: {{ name: static-answer, parameters: {{ gold_file: {Quote(goldFile)} }} }}");
        yaml.AppendLine($"expect_empty: {(IsEmpty(question.Answer.Gold) ? "true" : "false")}");
        yaml.AppendLine("verification: { status: upstream }");
        return yaml.ToString();
    }

    private static bool IsEmpty(object? gold) => gold switch
    {
        null => true,
        long count => count == 0,
        decimal value => value == 0,
        System.Collections.ICollection list => list.Count == 0,
        _ => false,
    };

    private static string SpecYaml(AnswerSpec spec)
    {
        var parts = new List<string> { $"type: {Name(spec.Type)}" };
        if (spec.Tolerance > 0)
        {
            parts.Add($"tolerance: {spec.Tolerance.ToString(CultureInfo.InvariantCulture)}");
        }

        if (spec.RelativeTolerance > 0)
        {
            parts.Add($"relative_tolerance: {spec.RelativeTolerance.ToString(CultureInfo.InvariantCulture)}");
        }

        if (spec.Nullable)
        {
            parts.Add("nullable: true");
        }

        if (spec.NullAcceptsAbstain)
        {
            parts.Add("null_accepts_abstain: true");
        }

        if (spec.Items is { } items)
        {
            parts.Add($"items: {SpecYaml(items)}");
        }

        return "{ " + string.Join(", ", parts) + " }";
    }

    private static string Name(AnswerType type) => type.ToString().ToLowerInvariant();

    /// <summary>A JSON string is a valid YAML double-quoted scalar.</summary>
    private static string Quote(string value) => JsonSerializer.Serialize(value, YamlString);

    private static string WriteSet(string outputDirectory, string fileName, IEnumerable<ImportedQuestion> questions, string header) =>
        WriteFile(Path.Combine(outputDirectory, fileName), $"# {header}\n" + string.Concat(questions.Select(q => q.Id + "\n")));

    private static string WriteFile(string path, string content)
    {
        File.WriteAllText(path, content);
        return path;
    }
}
