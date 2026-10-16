using System.Text.Json;

namespace FhirAgentBench.Authoring;

public static class GoldLock
{
    public const string FileName = "gold.lock.json";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, NewLine = "\n" };

    public static Dictionary<string, string> Load(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"Gold lock '{path}' is corrupt: expected a JSON object.");
            }

            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException($"Gold lock '{path}' is corrupt: '{property.Name}' is not a string.");
                }

                if (!hashes.TryAdd(property.Name, property.Value.GetString()!))
                {
                    throw new InvalidDataException($"Gold lock '{path}' is corrupt: duplicate key '{property.Name}'.");
                }
            }

            return hashes;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Gold lock '{path}' is corrupt: {ex.Message}", ex);
        }
    }

    public static void Save(string path, IReadOnlyDictionary<string, string> hashes)
    {
        var sorted = new SortedDictionary<string, string>(hashes.ToDictionary(), StringComparer.Ordinal);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(sorted, Indented) + "\n");
        File.Move(temp, path, overwrite: true);
    }
}
