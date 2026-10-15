using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FhirAgentBench.Corpus;

public sealed record CorpusFile(string Path, long Size, string Sha256);

public sealed record CorpusManifest(IReadOnlyList<CorpusFile> Files)
{
    private static readonly JsonSerializerOptions Options =
        new(JsonSerializerDefaults.Web) { WriteIndented = true, NewLine = "\n" };

    [JsonIgnore]
    public string Sha256 =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, Options))));

    public static CorpusManifest Create(string dataDirectory) =>
        new(EnumerateData(dataDirectory)
            .Select(path => new CorpusFile(Relative(dataDirectory, path), new FileInfo(path).Length, HashFile(path)))
            .ToList());

    public static CorpusManifest Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<CorpusManifest>(File.ReadAllText(path), Options)
                ?? throw new InvalidDataException($"Corpus manifest '{path}' is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Corpus manifest '{path}' is corrupt: {ex.Message}", ex);
        }
    }

    public void Save(string path)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Options) + "\n");
        File.Move(temp, path, overwrite: true);
    }

    public IReadOnlyList<string> Verify(string dataDirectory)
    {
        var problems = new List<string>();
        var expected = Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        foreach (var path in EnumerateData(dataDirectory))
        {
            var relative = Relative(dataDirectory, path);
            if (!expected.Remove(relative, out var file))
            {
                problems.Add($"{relative}: unexpected file");
            }
            else if (new FileInfo(path).Length != file.Size || HashFile(path) != file.Sha256)
            {
                problems.Add($"{relative}: content differs from manifest");
            }
        }

        problems.AddRange(expected.Keys.Select(missing => $"{missing}: missing"));
        return problems;
    }

    internal static IEnumerable<string> EnumerateData(string dataDirectory) =>
        Directory.EnumerateFiles(dataDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal);

    internal static string Relative(string root, string path) =>
        System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');

    internal static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
