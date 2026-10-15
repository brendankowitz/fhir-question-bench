using System.Security.Cryptography;
using System.Text.Json;

namespace FhirAgentBench.Execution;

public sealed record RunSnapshot(
    string RunId,
    string ProfileId,
    string ProfileSha256,
    string CorpusId,
    string CorpusManifestSha256,
    IReadOnlyList<string> QuestionIds,
    string QuestionsSha256,
    string? GoldLockSha256,
    int Repeats,
    string? HarnessVersion,
    string? CliOnFhirCommit,
    DateTimeOffset StartedUtc)
{
    private const string FileName = "run.json";

    public static string HashFiles(IEnumerable<string> paths)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in paths)
        {
            hash.AppendData(File.ReadAllBytes(path));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public Task WriteAsync(string runDirectory, CancellationToken cancellationToken = default) =>
        File.WriteAllTextAsync(Path.Combine(runDirectory, FileName), JsonSerializer.Serialize(this, JsonOptions.Default), cancellationToken);

    public static async Task<RunSnapshot> ReadAsync(string runDirectory, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(runDirectory, FileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"'{runDirectory}' is not a run folder: {FileName} is missing.", path);
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<RunSnapshot>(json, JsonOptions.Default)
            ?? throw new InvalidDataException($"'{path}' is empty.");
    }
}
