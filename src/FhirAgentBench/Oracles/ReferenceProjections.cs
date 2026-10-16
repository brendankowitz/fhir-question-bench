using System.Text.Json;
using FhirAgentBench.Corpus;

namespace FhirAgentBench.Oracles;

internal static class ReferenceProjections
{
    private const string AddressPrefix = "address.";
    private const string IdentifierPrefix = "identifier:";
    private const string CodePrefix = "code:";

    public static Func<JsonElement, object?> Parse(OracleParameters parameters)
    {
        if (!parameters.IsMap("project"))
        {
            return ParseSingle(parameters.GetString("project"));
        }

        var fields = parameters.GetNested("project");
        var projections = fields.Keys.ToDictionary(name => name, name => ParseSingle(fields.GetString(name)));
        return resource =>
        {
            var values = projections.ToDictionary(p => p.Key, p => p.Value(resource));
            return values.Values.Any(value => value is null) ? null : values;
        };
    }

    private static Func<JsonElement, object?> ParseSingle(string projection)
    {
        if (projection == "name")
        {
            return Name;
        }

        if (projection == "value")
        {
            return Value;
        }

        if (projection == "date")
        {
            return resource => ReferenceRules.Instant(resource) is { } when ? FhirTime.ToDate(when) : null;
        }

        if (projection.StartsWith(AddressPrefix, StringComparison.Ordinal))
        {
            var field = projection[AddressPrefix.Length..];
            return resource => AddressField(resource, field);
        }

        if (projection.StartsWith(IdentifierPrefix, StringComparison.Ordinal))
        {
            var system = projection[IdentifierPrefix.Length..];
            return resource => IdentifierIn(resource, system);
        }

        if (projection.StartsWith(CodePrefix, StringComparison.Ordinal))
        {
            var system = projection[CodePrefix.Length..];
            return resource => CodeIn(resource, system);
        }

        throw new InvalidDataException(
            $"Unknown projection '{projection}'; use name, value, date, address.<field>, identifier:<system> or code:<system>.");
    }

    private static string? Name(JsonElement resource)
    {
        if (!resource.TryGetProperty("name", out var name))
        {
            return null;
        }

        if (name.ValueKind == JsonValueKind.String)
        {
            return name.GetString();
        }

        if (name.ValueKind != JsonValueKind.Array || name.GetArrayLength() == 0)
        {
            return null;
        }

        var entries = name.EnumerateArray().ToList();
        var chosen = entries.FirstOrDefault(entry => FhirElements.String(entry, "use") == "official");
        if (chosen.ValueKind != JsonValueKind.Object)
        {
            chosen = entries[0];
        }

        var parts = new List<string>();
        if (chosen.TryGetProperty("given", out var given) && given.ValueKind == JsonValueKind.Array)
        {
            parts.AddRange(given.EnumerateArray().Where(g => g.ValueKind == JsonValueKind.String).Select(g => g.GetString()!));
        }

        if (FhirElements.String(chosen, "family") is { Length: > 0 } family)
        {
            parts.Add(family);
        }

        return parts.Count > 0 ? string.Join(' ', parts) : FhirElements.String(chosen, "text");
    }

    private static object? Value(JsonElement resource)
    {
        if (!resource.TryGetProperty("valueQuantity", out var quantity)
            || !quantity.TryGetProperty("value", out var value)
            || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        var unit = FhirElements.String(quantity, "code") ?? FhirElements.String(quantity, "unit");
        return unit is null ? null : new { value = value.GetDecimal(), unit };
    }

    private static string? AddressField(JsonElement resource, string field)
    {
        if (!resource.TryGetProperty("address", out var address))
        {
            return null;
        }

        if (address.ValueKind == JsonValueKind.Array)
        {
            address = address.GetArrayLength() > 0 ? address[0] : default;
        }

        return address.ValueKind == JsonValueKind.Object ? FhirElements.String(address, field) : null;
    }

    private static object? IdentifierIn(JsonElement resource, string system)
    {
        if (!resource.TryGetProperty("identifier", out var identifiers) || identifiers.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var found = identifiers.EnumerateArray().FirstOrDefault(i => FhirElements.String(i, "system") == system);
        return found.ValueKind == JsonValueKind.Object && FhirElements.String(found, "value") is { } value
            ? new { system, value }
            : null;
    }

    private static object? CodeIn(JsonElement resource, string system)
    {
        var property = resource.TryGetProperty("code", out _) ? "code" : "vaccineCode";
        return FhirElements.CodesIn(resource, property, system).FirstOrDefault() is { } code ? new { system, code } : null;
    }
}
