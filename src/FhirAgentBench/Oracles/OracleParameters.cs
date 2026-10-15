using System.Globalization;

namespace FhirAgentBench.Oracles;

public sealed class OracleParameters(IReadOnlyDictionary<string, object> values)
{
    public static OracleParameters Empty { get; } = new(new Dictionary<string, object>());

    public string GetString(string name) =>
        Get(name) as string ?? throw new InvalidDataException($"Oracle parameter '{name}' must be a single value.");

    public string? GetOptionalString(string name) => values.TryGetValue(name, out var value) ? value as string : null;

    public decimal GetDecimal(string name) =>
        decimal.TryParse(GetString(name), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidDataException($"Oracle parameter '{name}' value '{GetString(name)}' is not a decimal number.");

    public int GetInt(string name) =>
        int.TryParse(GetString(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidDataException($"Oracle parameter '{name}' value '{GetString(name)}' is not an integer.");

    public IReadOnlyList<string> GetStrings(string name) =>
        Get(name) is List<object> list
            ? list.Select(item => item as string ?? throw new InvalidDataException($"Oracle parameter '{name}' must be a list of values.")).ToList()
            : throw new InvalidDataException($"Oracle parameter '{name}' must be a list.");

    private object Get(string name) =>
        values.TryGetValue(name, out var value) ? value : throw new InvalidDataException($"Oracle parameter '{name}' is required.");
}
