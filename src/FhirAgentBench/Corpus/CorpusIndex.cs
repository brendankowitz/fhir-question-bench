using System.Text.Json;

namespace FhirAgentBench.Corpus;

public sealed class CorpusIndex
{
    private const string IdentifierQuery = "?identifier=";
    private static readonly string[] PatientReferenceProperties = ["subject", "patient"];

    private readonly Dictionary<string, JsonElement> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _fullUrls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _identifiers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<JsonElement>> _byType = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<JsonElement>> _byPatientAndType = new(StringComparer.Ordinal);

    private CorpusIndex()
    {
    }

    public int Count => _byKey.Count;

    public static CorpusIndex Load(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"Corpus data directory '{directory}' does not exist. Run 'fab corpus fetch <corpus-dir>' first.");
        }

        var index = new CorpusIndex();
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".ndjson", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal);
        foreach (var file in files)
        {
            index.AddFile(file);
        }

        index.IndexPatients();
        return index;
    }

    public static string KeyOf(JsonElement resource) =>
        $"{resource.GetProperty("resourceType").GetString()}/{resource.GetProperty("id").GetString()}";

    public IReadOnlyList<JsonElement> OfType(string type) =>
        _byType.TryGetValue(type, out var list) ? list : [];

    public IReadOnlyList<JsonElement> ForPatient(string patientKey, string type) =>
        _byPatientAndType.TryGetValue($"{patientKey}|{type}", out var list) ? list : [];

    public JsonElement? Resolve(string reference) =>
        ResolveKey(reference) is { } key && _byKey.TryGetValue(key, out var resource) ? resource : null;

    public JsonElement? ResolveFrom(JsonElement container, string reference)
    {
        if (reference.StartsWith('#') && container.TryGetProperty("contained", out var contained))
        {
            foreach (var candidate in contained.EnumerateArray())
            {
                if (candidate.TryGetProperty("id", out var id) && id.GetString() == reference[1..])
                {
                    return candidate;
                }
            }

            return null;
        }

        return Resolve(reference);
    }

    public string? ResolveKey(string reference)
    {
        if (_fullUrls.TryGetValue(reference, out var fromFullUrl))
        {
            return fromFullUrl;
        }

        var query = reference.IndexOf(IdentifierQuery, StringComparison.Ordinal);
        if (query > 0)
        {
            var token = reference[(query + IdentifierQuery.Length)..];
            var bar = token.IndexOf('|');
            return bar >= 0 && _identifiers.TryGetValue($"{reference[..query]}|{token[..bar]}|{token[(bar + 1)..]}", out var byIdentifier)
                ? byIdentifier
                : null;
        }

        return _byKey.ContainsKey(reference) ? reference : null;
    }

    public JsonElement? FindByIdentifier(string type, string system, string value) =>
        _identifiers.TryGetValue($"{type}|{system}|{value}", out var key) ? _byKey[key] : null;

    private void AddFile(string path)
    {
        if (path.EndsWith(".ndjson", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var line in File.ReadLines(path).Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                using var lineDocument = JsonDocument.Parse(line);
                AddResource(lineDocument.RootElement, fullUrl: null, path);
            }

            return;
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        if (root.GetProperty("resourceType").GetString() != "Bundle")
        {
            AddResource(root, fullUrl: null, path);
            return;
        }

        foreach (var entry in root.GetProperty("entry").EnumerateArray())
        {
            var fullUrl = entry.TryGetProperty("fullUrl", out var url) ? url.GetString() : null;
            AddResource(entry.GetProperty("resource"), fullUrl, path);
        }
    }

    private void AddResource(JsonElement source, string? fullUrl, string path)
    {
        var resource = source.Clone();
        if (!resource.TryGetProperty("id", out _))
        {
            throw new InvalidDataException($"A resource in '{path}' has no id.");
        }

        var key = KeyOf(resource);
        if (!_byKey.TryAdd(key, resource))
        {
            throw new InvalidDataException($"Resource '{key}' appears more than once (second copy in '{path}').");
        }

        var type = resource.GetProperty("resourceType").GetString()!;
        if (!_byType.TryGetValue(type, out var list))
        {
            _byType[type] = list = [];
        }

        list.Add(resource);

        if (fullUrl is not null)
        {
            _fullUrls.TryAdd(fullUrl, key);
        }

        if (resource.TryGetProperty("identifier", out var identifiers) && identifiers.ValueKind == JsonValueKind.Array)
        {
            foreach (var identifier in identifiers.EnumerateArray())
            {
                if (identifier.TryGetProperty("system", out var system) && identifier.TryGetProperty("value", out var value))
                {
                    _identifiers.TryAdd($"{type}|{system.GetString()}|{value.GetString()}", key);
                }
            }
        }
    }

    private void IndexPatients()
    {
        foreach (var resource in _byKey.Values)
        {
            var patientKey = PatientKeyOf(resource);
            if (patientKey is null)
            {
                continue;
            }

            var bucket = $"{patientKey}|{resource.GetProperty("resourceType").GetString()}";
            if (!_byPatientAndType.TryGetValue(bucket, out var list))
            {
                _byPatientAndType[bucket] = list = [];
            }

            list.Add(resource);
        }
    }

    private string? PatientKeyOf(JsonElement resource)
    {
        foreach (var name in PatientReferenceProperties)
        {
            if (resource.TryGetProperty(name, out var reference)
                && reference.ValueKind == JsonValueKind.Object
                && reference.TryGetProperty("reference", out var value)
                && ResolveKey(value.GetString()!) is { } key
                && key.StartsWith("Patient/", StringComparison.Ordinal))
            {
                return key;
            }
        }

        return null;
    }
}
