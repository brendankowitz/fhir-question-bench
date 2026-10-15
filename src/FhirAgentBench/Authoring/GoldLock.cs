using System.Text.Json;

namespace FhirAgentBench.Authoring;

public static class GoldLock
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static Dictionary<string, string> Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? []
            : [];

    public static void Save(string path, IReadOnlyDictionary<string, string> hashes)
    {
        var sorted = new SortedDictionary<string, string>(hashes.ToDictionary(), StringComparer.Ordinal);
        File.WriteAllText(path, JsonSerializer.Serialize(sorted, Indented) + "\n");
    }
}
