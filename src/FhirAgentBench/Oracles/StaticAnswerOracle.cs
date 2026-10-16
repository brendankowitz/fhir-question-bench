using System.Collections.Concurrent;
using System.Text.Json;
using FhirAgentBench.Answers;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

/// <summary>
/// Gold taken from an external suite rather than computed from the corpus. <c>gold_file</c> (relative to the question file)
/// is a JSON object mapping question ids to answers in the <c>answer.json</c> format; its evidence is the oracle's evidence.
/// </summary>
public sealed class StaticAnswerOracle : IOracle
{
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Answer>> _files = new(StringComparer.OrdinalIgnoreCase);

    public string Name => "static-answer";

    public Answer Compute(OracleContext context, Question question)
    {
        var path = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(question.SourcePath) ?? ".", question.Oracle.Parameters.GetString("gold_file")));
        var answers = _files.GetOrAdd(path, Load);
        return answers.TryGetValue(question.Id, out var gold)
            ? gold
            : throw new InvalidDataException($"Gold file '{path}' has no answer for question '{question.Id}'.");
    }

    private static IReadOnlyDictionary<string, Answer> Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Gold file '{path}' does not exist. Re-run the suite's importer.", path);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"Gold file '{path}' must be a JSON object keyed by question id.");
            }

            return document.RootElement.EnumerateObject().ToDictionary(
                entry => entry.Name,
                entry => AnswerJson.Parse(entry.Value.GetRawText()),
                StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            throw new InvalidDataException($"Gold file '{path}' is corrupt: {ex.Message}", ex);
        }
    }
}
