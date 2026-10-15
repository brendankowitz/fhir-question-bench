namespace FhirAgentBench.Oracles;

internal static class Comparators
{
    public static Func<decimal, bool> Create(string questionId, string comparator, decimal threshold) => comparator switch
    {
        "gt" => value => value > threshold,
        "ge" => value => value >= threshold,
        "lt" => value => value < threshold,
        "le" => value => value <= threshold,
        _ => throw new InvalidDataException($"Question '{questionId}': comparator must be gt, ge, lt or le, not '{comparator}'."),
    };
}
