using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FhirAgentBench.Answers;
using FhirAgentBench.Execution;

namespace FhirAgentBench.Evidence;

public sealed record SearchResolution(IReadOnlyList<string> Keys, string? UnresolvedReason)
{
    public bool IsResolved => UnresolvedReason is null;

    public static SearchResolution Resolved(IReadOnlyList<string> keys) => new(keys, null);

    public static SearchResolution Unresolved(string reason) => new([], reason);
}

public sealed record CliOutput(int ExitCode, bool TimedOut, string Stdout, string Stderr = "");

public delegate Task<CliOutput> CliInvoker(IReadOnlyList<string> arguments, CancellationToken cancellationToken);

/// <summary>
/// Turns a FHIR search an agent cited into the resource keys it returned, by running the pinned cli-on-fhir over the corpus.
/// A search that cannot be run or read is unresolved with the reason; a key set is never guessed.
/// </summary>
public sealed partial class EvidenceResolver
{
    private const int PageSize = 50;

    private const int MaxPages = 100;

    private static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(2);

    private readonly CliInvoker _invoke;

    private readonly string _corpusDataDirectory;

    private readonly Dictionary<string, SearchResolution> _resolved = new(StringComparer.Ordinal);

    public EvidenceResolver(CliInvoker invoke, string corpusDataDirectory)
    {
        ArgumentNullException.ThrowIfNull(invoke);
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusDataDirectory);
        _invoke = invoke;
        _corpusDataDirectory = corpusDataDirectory;
    }

    /// <summary>Runs <paramref name="executable"/> as a child process; its index cache lives in <paramref name="cacheDirectory"/>, never in the corpus.</summary>
    public static EvidenceResolver ForExecutable(string executable, string corpusDataDirectory, string cacheDirectory, string scratchDirectory)
    {
        Directory.CreateDirectory(scratchDirectory);
        Directory.CreateDirectory(cacheDirectory);
        var environment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string)(e.Value ?? string.Empty), StringComparer.OrdinalIgnoreCase);
        var calls = 0;
        return new EvidenceResolver(
            async (arguments, cancellationToken) =>
            {
                var call = Interlocked.Increment(ref calls);
                var stdoutPath = Path.Combine(scratchDirectory, $"call-{call}.stdout.json");
                var stderrPath = Path.Combine(scratchDirectory, $"call-{call}.stderr.txt");
                var result = await ProcessRunner.RunAsync(
                    executable, [.. arguments, "--cache-dir", cacheDirectory], environment, scratchDirectory,
                    stdoutPath, stderrPath, CallTimeout, cancellationToken).ConfigureAwait(false);
                var stdout = await File.ReadAllTextAsync(stdoutPath, cancellationToken).ConfigureAwait(false);
                var stderr = await File.ReadAllTextAsync(stderrPath, cancellationToken).ConfigureAwait(false);
                File.Delete(stdoutPath);
                File.Delete(stderrPath);
                return new CliOutput(result.ExitCode, result.TimedOut, stdout, stderr);
            },
            corpusDataDirectory);
    }

    public async Task<SearchResolution> ResolveAsync(
        EvidenceEntry search, string? defaultCompartment = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(search);
        var key = search.NormalizedSearch(defaultCompartment);
        if (_resolved.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var resolution = await RunPagesAsync(search, search.Compartment ?? defaultCompartment, cancellationToken).ConfigureAwait(false);
        _resolved[key] = resolution;
        return resolution;
    }

    private async Task<SearchResolution> RunPagesAsync(EvidenceEntry search, string? compartment, CancellationToken cancellationToken)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;
        var limit = CountParameter().Match(search.Query!) is { Success: true } count && int.TryParse(count.Groups[1].ValueSpan, out var requested) && requested > 0
            ? requested
            : int.MaxValue;
        for (var page = 0; page < MaxPages; page++)
        {
            var arguments = new List<string> { "search", _corpusDataDirectory, search.ResourceType!, search.Query! };
            if (compartment is not null)
            {
                arguments.AddRange(["--compartment", compartment]);
            }

            arguments.AddRange(
            [
                "--elements", "id", "--max-bytes", "51200",
                "--count", Math.Min(PageSize, limit - offset).ToString(CultureInfo.InvariantCulture),
                "--offset", offset.ToString(CultureInfo.InvariantCulture),
            ]);

            CliOutput output;
            try
            {
                output = await _invoke(arguments, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or Win32Exception)
            {
                return SearchResolution.Unresolved($"could not run the resolver: {ex.Message}");
            }

            var parsed = ReadPage(output);
            if (parsed.Failure is not null)
            {
                return SearchResolution.Unresolved(parsed.Failure);
            }

            keys.UnionWith(parsed.Keys);
            offset += parsed.Keys.Count;
            if (offset >= Math.Min(parsed.Total, limit))
            {
                return SearchResolution.Resolved(keys.Order(StringComparer.Ordinal).ToList());
            }

            if (parsed.Keys.Count == 0)
            {
                return SearchResolution.Unresolved($"the resolver reported {parsed.Total} matches but returned none at offset {offset}");
            }
        }

        return SearchResolution.Unresolved($"more than {MaxPages * PageSize} matches; not paged to the end");
    }

    [GeneratedRegex(@"(?:^|&)_count=(\d+)(?:&|$)")]
    private static partial Regex CountParameter();

    private static Page ReadPage(CliOutput output)
    {
        if (output.TimedOut)
        {
            return Page.Failed($"the resolver timed out after {CallTimeout.TotalSeconds:0} seconds");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(output.Stdout.TrimStart('﻿'));
        }
        catch (JsonException)
        {
            var detail = output.Stderr.Split('\n', 2)[0].Trim();
            return Page.Failed($"the resolver exited with code {output.ExitCode} and printed no JSON{(detail.Length > 0 ? ": " + detail : "")}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (FirstError(root) is { } error)
            {
                return Page.Failed($"the resolver reported an error: {error}");
            }

            if (output.ExitCode != 0)
            {
                return Page.Failed($"the resolver exited with code {output.ExitCode}");
            }

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("facts", out var facts) || facts.ValueKind != JsonValueKind.Object
                || !facts.TryGetProperty("total", out var total) || !total.TryGetInt32(out var totalMatches)
                || !facts.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array)
            {
                return Page.Failed("the resolver's response has no facts.total and facts.resources");
            }

            var keys = new List<string>();
            foreach (var resource in resources.EnumerateArray())
            {
                if (resource.ValueKind != JsonValueKind.Object
                    || !resource.TryGetProperty("resourceType", out var type) || type.ValueKind != JsonValueKind.String
                    || !resource.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
                {
                    return Page.Failed("the resolver returned a resource without resourceType and id");
                }

                keys.Add($"{type.GetString()}/{id.GetString()}");
            }

            return new Page(keys, totalMatches, null);
        }
    }

    private static string? FirstError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("issues", out var issues) || issues.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var issue in issues.EnumerateArray())
        {
            if (issue.ValueKind == JsonValueKind.Object
                && issue.TryGetProperty("severity", out var severity) && severity.GetString() == "error")
            {
                var code = issue.TryGetProperty("code", out var c) ? c.GetString() : null;
                var diagnostics = issue.TryGetProperty("diagnostics", out var d) ? d.GetString() : null;
                return $"{code}: {diagnostics}";
            }
        }

        return null;
    }

    private sealed record Page(IReadOnlyList<string> Keys, int Total, string? Failure)
    {
        public static Page Failed(string reason) => new([], 0, reason);
    }
}
