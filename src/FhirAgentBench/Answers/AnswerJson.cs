using System.Text.Json;

namespace FhirAgentBench.Answers;

public static class AnswerJson
{
    public static Answer Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json.TrimStart('\uFEFF'));
        }
        catch (JsonException ex)
        {
            throw new FormatException($"answer.json is not valid JSON: {ex.Message}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("answer.json must be a JSON object with a 'status' property.");
            }

            var status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            var parsedStatus = status switch
            {
                "answered" => AnswerStatus.Answered,
                "insufficient_data" => AnswerStatus.InsufficientData,
                _ => throw new FormatException($"answer.json 'status' must be 'answered' or 'insufficient_data', not '{status}'."),
            };

            var hasAnswer = root.TryGetProperty("answer", out var a);
            if (parsedStatus == AnswerStatus.Answered && !hasAnswer)
            {
                throw new FormatException("answer.json with status 'answered' must include an 'answer' property (null if there is no such result).");
            }

            JsonElement? value = hasAnswer && a.ValueKind != JsonValueKind.Null ? a.Clone() : null;
            var evidence = root.TryGetProperty("evidence", out var e) && e.ValueKind == JsonValueKind.Array
                ? e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                : [];
            return new Answer(parsedStatus, value, evidence);
        }
    }

    public static string Serialize(Answer answer) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["status"] = answer.Status == AnswerStatus.Answered ? "answered" : "insufficient_data",
            ["answer"] = answer.Value,
            ["evidence"] = answer.Evidence,
        });
}
