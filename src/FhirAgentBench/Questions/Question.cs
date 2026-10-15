using FhirAgentBench.Answers;
using FhirAgentBench.Oracles;

namespace FhirAgentBench.Questions;

public sealed record PatientContext(string Mrn, string Name, string BirthDate);

public sealed record OracleCall(string Name, OracleParameters Parameters);

public sealed record Question
{
    public required string Id { get; init; }

    public required int Tier { get; init; }

    public required string Template { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public IReadOnlyList<string> ToolGroups { get; init; } = [];

    public required string Persona { get; init; }

    public PatientContext? Patient { get; init; }

    public required string Text { get; init; }

    public required AnswerSpec Answer { get; init; }

    public required OracleCall Oracle { get; init; }

    public bool ExpectEmpty { get; init; }

    public string VerificationStatus { get; init; } = "unverified";

    public required string SourcePath { get; init; }
}
