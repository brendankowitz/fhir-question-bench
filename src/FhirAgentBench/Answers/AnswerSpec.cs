namespace FhirAgentBench.Answers;

public enum AnswerType
{
    Integer,
    Decimal,
    Quantity,
    Date,
    Code,
    Identifier,
    Set,
    Boolean,
    Composite,
    Name,
}

public enum NameKind
{
    Person,
    Text,
}

public sealed record AnswerSpec
{
    public required AnswerType Type { get; init; }

    public string? Unit { get; init; }

    public decimal Tolerance { get; init; }

    public bool Nullable { get; init; }

    public NameKind? NameKind { get; init; }

    public IReadOnlyDictionary<string, AnswerSpec> Fields { get; init; } = new Dictionary<string, AnswerSpec>();
}
