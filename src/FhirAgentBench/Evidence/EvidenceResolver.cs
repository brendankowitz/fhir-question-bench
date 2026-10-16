using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FhirAgentBench.Answers;
using FhirAgentBench.Execution;

namespace FhirAgentBench.Evidence;

/// <summary>The resource keys a cited search returned, or why they could not be found; <see cref="Notes"/> records parameters removed before running it.</summary>
public sealed record SearchResolution(IReadOnlyList<string> Keys, string? UnresolvedReason, IReadOnlyList<string> Notes)
{
    public bool IsResolved => UnresolvedReason is null;

    public static SearchResolution Resolved(IReadOnlyList<string> keys, IReadOnlyList<string> notes) => new(keys, null, notes);

    public static SearchResolution Unresolved(string reason, IReadOnlyList<string>? notes = null) => new([], reason, notes ?? []);
}

public sealed record CliOutput(int ExitCode, bool TimedOut, string Stdout, string Stderr = "");

public delegate Task<CliOutput> CliInvoker(IReadOnlyList<string> arguments, CancellationToken cancellationToken);

/// <summary>
/// Turns a FHIR search an agent cited into the resource keys it returned, by running the pinned cli-on-fhir over the corpus.
/// A search that cannot be run or read is unresolved with the reason; a key set is never guessed.
/// </summary>
public sealed partial class EvidenceResolver
{
    public const string PositionalSeparator = "--";

    private const int PageSize = 50;

    private const int MaxPages = 100;

    private static readonly TimeSpan ColdCallTimeout = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan WarmCallTimeout = TimeSpan.FromMinutes(2);

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

    /// <summary>
    /// Runs <paramref name="executable"/> as a child process with a system-only environment; its index cache lives in
    /// <paramref name="cacheDirectory"/>, never in the corpus. The first call builds the index and gets a longer timeout.
    /// </summary>
    public static EvidenceResolver ForExecutable(string executable, string corpusDataDirectory, string cacheDirectory, string scratchDirectory)
    {
        if (OperatingSystem.IsWindows() && !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The evidence resolver '{executable}' must be an .exe on Windows: a .cmd or .bat shim is re-parsed by cmd.exe, which would interpret & | % ^ in a cited search.",
                nameof(executable));
        }

        Directory.CreateDirectory(scratchDirectory);
        Directory.CreateDirectory(cacheDirectory);
        var environment = CopilotEnvironment.SystemOnly();
        var calls = 0;
        return new EvidenceResolver(
            async (arguments, cancellationToken) =>
            {
                var call = Interlocked.Increment(ref calls);
                var stdoutPath = Path.Combine(scratchDirectory, $"call-{call}.stdout.json");
                var stderrPath = Path.Combine(scratchDirectory, $"call-{call}.stderr.txt");
                var separator = arguments.ToList().IndexOf(PositionalSeparator);
                string[] full = [.. arguments.Take(separator), "--cache-dir", cacheDirectory, .. arguments.Skip(separator)];
                var result = await ProcessRunner.RunAsync(
                    executable, full, environment, scratchDirectory, stdoutPath, stderrPath,
                    call == 1 ? ColdCallTimeout : WarmCallTimeout, cancellationToken).ConfigureAwait(false);
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

        var (query, notes) = Sanitize(search.Query!);
        var resolution = await RunPagesAsync(search.ResourceType!, query, search.Compartment ?? defaultCompartment, notes, cancellationToken)
            .ConfigureAwait(false);
        _resolved[key] = resolution;
        return resolution;
    }

    /// <summary>
    /// Removes parameters that would change how many matches are paged or pull in other resources (<c>_offset</c>,
    /// <c>_count=0</c>, <c>_include</c>, <c>_revinclude</c>); each removal is returned as a note.
    /// </summary>
    internal static (string Query, IReadOnlyList<string> Notes) Sanitize(string query)
    {
        var kept = new List<string>();
        var notes = new List<string>();
        foreach (var parameter in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = parameter.Split('=', 2)[0];
            var removed = name == "_offset"
                || parameter == "_count=0"
                || name == "_include" || name.StartsWith("_include:", StringComparison.Ordinal)
                || name == "_revinclude" || name.StartsWith("_revinclude:", StringComparison.Ordinal);
            if (removed)
            {
                notes.Add($"removed {parameter}");
            }
            else
            {
                kept.Add(parameter);
            }
        }

        return (string.Join('&', kept), notes);
    }

    private async Task<SearchResolution> RunPagesAsync(
        string resourceType, string query, string? compartment, IReadOnlyList<string> notes, CancellationToken cancellationToken)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;
        var total = 0;
        var limit = CountParameter().Match(query) is { Success: true } count && int.TryParse(count.Groups[1].ValueSpan, out var requested) && requested > 0
            ? requested
            : int.MaxValue;
        for (var page = 0; page < MaxPages; page++)
        {
            List<string> arguments = ["search"];
            if (compartment is not null)
            {
                arguments.AddRange(["--compartment", compartment]);
            }

            arguments.AddRange(
            [
                "--elements", "id", "--max-bytes", "51200",
                "--count", Math.Min(PageSize, limit - offset).ToString(CultureInfo.InvariantCulture),
                "--offset", offset.ToString(CultureInfo.InvariantCulture),
                PositionalSeparator, _corpusDataDirectory, resourceType, query,
            ]);

            CliOutput output;
            try
            {
                output = await _invoke(arguments, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return SearchResolution.Unresolved($"could not run the resolver: {ex.GetType().Name}: {ex.Message}", notes);
            }

            var parsed = ReadPage(output);
            if (parsed.Failure is not null)
            {
                return SearchResolution.Unresolved(parsed.Failure, notes);
            }

            keys.UnionWith(parsed.Keys);
            offset += parsed.Keys.Count;
            total = parsed.Total;
            if (offset >= Math.Min(total, limit))
            {
                return SearchResolution.Resolved(keys.Order(StringComparer.Ordinal).ToList(), notes);
            }

            if (parsed.Keys.Count == 0)
            {
                return SearchResolution.Unresolved($"the resolver reported {total} matches but returned none at offset {offset}", notes);
            }
        }

        return SearchResolution.Unresolved($"stopped after {MaxPages} pages with {offset} of {total} matches read", notes);
    }

    [GeneratedRegex(@"(?:^|&)_count=(\d+)(?:&|$)")]
    private static partial Regex CountParameter();

    private static Page ReadPage(CliOutput output)
    {
        if (output.TimedOut)
        {
            return Page.Failed("the resolver timed out");
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
