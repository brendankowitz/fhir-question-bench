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
    DateTime,
    Text,
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

    /// <summary>Absolute tolerance for decimal and quantity values.</summary>
    public decimal Tolerance { get; init; }

    /// <summary>Relative tolerance for decimal values: a value passes when within the larger of <see cref="Tolerance"/> and this fraction of the gold.</summary>
    public decimal RelativeTolerance { get; init; }

    public bool Nullable { get; init; }

    /// <summary>
    /// When the gold is null ("no such result"), also grade <c>insufficient_data</c> as correct. For imported suites whose
    /// upstream gold cannot tell "nothing recorded" from "no such result".
    /// </summary>
    public bool NullAcceptsAbstain { get; init; }

    public NameKind? NameKind { get; init; }

    /// <summary>For a set: the rule each element follows. Without it, elements are strings or codes compared exactly.</summary>
    public AnswerSpec? Items { get; init; }

    public IReadOnlyDictionary<string, AnswerSpec> Fields { get; init; } = new Dictionary<string, AnswerSpec>();
}
