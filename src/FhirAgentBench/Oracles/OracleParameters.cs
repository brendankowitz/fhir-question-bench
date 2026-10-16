using System.Globalization;

namespace FhirAgentBench.Oracles;

public sealed class OracleParameters(IReadOnlyDictionary<string, object> values)
{
    public static OracleParameters Empty { get; } = new(new Dictionary<string, object>());

    public string GetString(string name) =>
        Get(name) as string ?? throw new InvalidDataException($"Oracle parameter '{name}' must be a single value.");

    public IEnumerable<string> Keys => values.Keys;

    public bool Has(string name) => values.ContainsKey(name);

    public bool IsMap(string name) => Get(name) is Dictionary<object, object>;

    public string? GetOptionalString(string name) => Has(name) ? GetString(name) : null;

    public decimal GetDecimal(string name) =>
        decimal.TryParse(GetString(name), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidDataException($"Oracle parameter '{name}' value '{GetString(name)}' is not a decimal number.");

    public int GetInt(string name) =>
        int.TryParse(GetString(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidDataException($"Oracle parameter '{name}' value '{GetString(name)}' is not an integer.");

    public int? GetOptionalInt(string name) => Has(name) ? GetInt(name) : null;

    public IReadOnlyList<string> GetStringOrList(string name) =>
        Get(name) is string single ? [single] : GetStrings(name);

    public IReadOnlyDictionary<string, string> GetMap(string name) =>
        Get(name) is Dictionary<object, object> map
            ? map.ToDictionary(
                pair => pair.Key as string ?? throw new InvalidDataException($"Oracle parameter '{name}' must be a map of names to codes."),
                pair => pair.Value as string ?? throw new InvalidDataException($"Oracle parameter '{name}' must be a map of names to codes."))
            : throw new InvalidDataException($"Oracle parameter '{name}' must be a map.");

    public OracleParameters GetNested(string name) =>
        Get(name) is Dictionary<object, object> map
            ? new OracleParameters(map.ToDictionary(
                pair => pair.Key as string ?? throw new InvalidDataException($"Oracle parameter '{name}' must be a map keyed by names."),
                pair => pair.Value))
            : throw new InvalidDataException($"Oracle parameter '{name}' must be a map.");

    public IReadOnlyList<OracleParameters> GetNestedList(string name) =>
        Get(name) is List<object> list
            ? list.Select(item => item is Dictionary<object, object> map
                    ? new OracleParameters(map.ToDictionary(
                        pair => pair.Key as string ?? throw new InvalidDataException($"Oracle parameter '{name}' must be a list of maps keyed by names."),
                        pair => pair.Value))
                    : throw new InvalidDataException($"Oracle parameter '{name}' must be a list of maps."))
                .ToList()
            : throw new InvalidDataException($"Oracle parameter '{name}' must be a list.");

    public IReadOnlyList<string> GetStrings(string name) =>
        Get(name) is List<object> list
            ? list.Select(item => item as string ?? throw new InvalidDataException($"Oracle parameter '{name}' must be a list of values.")).ToList()
            : throw new InvalidDataException($"Oracle parameter '{name}' must be a list.");

    private object Get(string name) =>
        values.TryGetValue(name, out var value) ? value : throw new InvalidDataException($"Oracle parameter '{name}' is required.");
}
