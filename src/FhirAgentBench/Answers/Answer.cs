using System.Text.Json;

namespace FhirAgentBench.Answers;

public enum AnswerStatus
{
    Answered,
    InsufficientData,
}

public enum GradeStatus
{
    Correct,
    Wrong,
    Abstained,
    FormatInvalid,
    TurnLimit,
    HarnessError,
}

public sealed record Answer(AnswerStatus Status, JsonElement? Value, IReadOnlyList<string> Evidence)
{
    public IReadOnlyList<EvidenceEntry> ClassifiedEvidence() => Evidence.Select(EvidenceEntry.Parse).ToList();

    public static Answer Insufficient() => new(AnswerStatus.InsufficientData, null, []);

    public static Answer Of(object? value, IEnumerable<string> evidence) =>
        new(AnswerStatus.Answered,
            value is null ? null : JsonSerializer.SerializeToElement(value),
            evidence.Distinct(StringComparer.Ordinal).ToList());
}
