using System.ComponentModel;
using System.Text;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Execution;

public sealed record PatientMap(string Text, int Bytes, TimeSpan Duration);

public sealed class WarmContextException(string message) : Exception(message);

/// <summary>Runs the profile's warm_context command once per patient and caches the map for the rest of the run.</summary>
public sealed class WarmContextRunner(Profile profile, string corpusDataDirectory, OracleContext context)
{
    private readonly WarmContext _warm = profile.WarmContext
        ?? throw new ArgumentException($"Profile '{profile.Id}' has no warm_context.", nameof(profile));

    private readonly Dictionary<string, PatientMap> _cache = new(StringComparer.Ordinal);

    /// <summary>Returns the map and whether it came from the cache; null for a question without a patient. Throws <see cref="WarmContextException"/> on failure.</summary>
    public async Task<(PatientMap Map, bool FromCache)?> GetAsync(
        Question question, string attemptDirectory, CancellationToken cancellationToken = default)
    {
        if (question.Patient is not { } patient)
        {
            return null;
        }

        var patientKey = context.PatientKey(patient);
        var patientId = patientKey[(patientKey.IndexOf('/') + 1)..];
        if (_cache.TryGetValue(patientId, out var cached))
        {
            return (cached, true);
        }

        var map = await RunAsync(patientId, patient.Mrn, attemptDirectory, cancellationToken).ConfigureAwait(false);
        _cache[patientId] = map;
        return (map, false);
    }

    private async Task<PatientMap> RunAsync(string patientId, string mrn, string attemptDirectory, CancellationToken cancellationToken)
    {
        var command = McpConfig.Expand(_warm.Command, corpusDataDirectory, profile);
        var arguments = _warm.Args
            .Select(a => McpConfig.Expand(a, corpusDataDirectory, profile)
                .Replace("{patient_id}", patientId, StringComparison.Ordinal)
                .Replace("{mrn}", mrn, StringComparison.Ordinal))
            .ToList();
        var stdoutPath = Path.Combine(attemptDirectory, "warm-context.stdout.txt");
        var stderrPath = Path.Combine(attemptDirectory, "warm-context.stderr.txt");
        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync(
                    command, arguments, CopilotEnvironment.SystemOnly(), attemptDirectory, stdoutPath, stderrPath,
                    TimeSpan.FromSeconds(_warm.TimeoutSeconds), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new WarmContextException($"warm_context command '{command}' could not run: {ex.Message}");
        }

        if (result.TimedOut)
        {
            throw new WarmContextException($"warm_context command timed out after {_warm.TimeoutSeconds} seconds for patient {patientId}; see warm-context.stderr.txt");
        }

        if (result.ExitCode != 0)
        {
            throw new WarmContextException($"warm_context command exited with code {result.ExitCode} for patient {patientId}; see warm-context.stderr.txt");
        }

        var bytes = (int)Math.Min(new FileInfo(stdoutPath).Length, int.MaxValue);
        if (bytes > _warm.MaxBytes)
        {
            throw new WarmContextException($"warm_context output is {bytes} bytes for patient {patientId}, over max_bytes {_warm.MaxBytes}; see warm-context.stdout.txt");
        }

        var text = (await File.ReadAllTextAsync(stdoutPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false)).Trim();
        return text.Length > 0
            ? new PatientMap(text, bytes, result.Duration)
            : throw new WarmContextException($"warm_context command printed nothing for patient {patientId}");
    }
}
