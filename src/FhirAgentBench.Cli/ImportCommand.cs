using System.CommandLine;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FhirAgentBench.Corpus;
using FhirAgentBench.Import;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Cli;

internal static class ImportCommand
{
    public static Command Create()
    {
        var import = new Command("import", "Import an external question suite.");
        import.Subcommands.Add(FhirAgentBench());
        return import;
    }

    private static Command FhirAgentBench()
    {
        var config = new Option<string>("--config")
        {
            Description = "Suite import config (pinned source, corpus, output, default selection).",
            DefaultValueFactory = _ => Path.Combine("suites", "fhir-agentbench", "import.yaml"),
        };
        var split = new Option<string?>("--split") { Description = "Upstream split: train, valid or test (default from the config)." };
        var sample = new Option<int?>("--sample") { Description = "Also write a set of N questions stratified by main_table_name (default from the config)." };
        var seed = new Option<int?>("--seed") { Description = "Sampling seed (default from the config)." };
        var csv = new Option<string?>("--csv") { Description = "Use this local copy of the upstream CSV instead of downloading it; its sha256 is still checked." };
        var command = new Command(
            "fhir-agentbench",
            "Download FHIR-AgentBench's questions at the pinned commit, check the sha256, and write question YAMLs, gold and question sets for the static-answer oracle.")
        {
            config, split, sample, seed, csv,
        };
        command.SetAction(async (parse, cancellationToken) =>
        {
            var settings = ImportConfig.Load(parse.GetValue(config)!);
            Directory.CreateDirectory(settings.OutputDirectory);
            var text = await ReadSourceAsync(settings, parse.GetValue(csv), cancellationToken);
            var rows = Csv.ReadRecords(text).Select(UpstreamQuestion.From).ToList();

            var corpus = CorpusDefinition.Load(settings.CorpusDirectory);
            RunCommand.VerifiedManifest(corpus);
            Console.WriteLine($"Loading corpus '{corpus.Id}'...");
            var patients = PatientsBySubjectId(CorpusIndex.Load(corpus.DataDirectory), corpus.MrnSystem);

            var result = FhirAgentBenchImporter.Convert(
                rows, parse.GetValue(split) ?? settings.Split, patients, parse.GetValue(sample) ?? settings.Sample, parse.GetValue(seed) ?? settings.Seed);
            foreach (var written in FhirAgentBenchImporter.Write(result, settings.OutputDirectory, settings.Provenance))
            {
                Console.WriteLine($"Wrote {written}");
            }

            Console.WriteLine();
            Console.Write(result.Report());
            return 0;
        });
        return command;
    }

    private static async Task<string> ReadSourceAsync(ImportConfig settings, string? localCopy, CancellationToken cancellationToken)
    {
        var cache = Path.Combine(settings.OutputDirectory, "upstream.csv");
        byte[] bytes;
        if (localCopy is not null)
        {
            bytes = await File.ReadAllBytesAsync(localCopy, cancellationToken);
        }
        else if (File.Exists(cache) && Sha256(await File.ReadAllBytesAsync(cache, cancellationToken)) == settings.Sha256)
        {
            bytes = await File.ReadAllBytesAsync(cache, cancellationToken);
        }
        else
        {
            Console.WriteLine($"Downloading {settings.SourceUrl}...");
            using var http = new HttpClient();
            bytes = await http.GetByteArrayAsync(settings.SourceUrl, cancellationToken);
        }

        var actual = Sha256(bytes);
        if (!string.Equals(actual, settings.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Upstream CSV sha256 {actual} does not match the config ({settings.Sha256}). The source changed or the download is corrupt.");
        }

        await File.WriteAllBytesAsync(cache, bytes, cancellationToken);
        return Encoding.UTF8.GetString(bytes);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static Dictionary<string, CorpusPatient> PatientsBySubjectId(CorpusIndex index, string mrnSystem)
    {
        var patients = new Dictionary<string, CorpusPatient>(StringComparer.Ordinal);
        foreach (var patient in index.OfType("Patient"))
        {
            var mrn = Identifier(patient, mrnSystem);
            if (mrn is null)
            {
                continue;
            }

            var name = patient.TryGetProperty("name", out var names) && names.GetArrayLength() > 0 ? DisplayName(names[0]) : null;
            var birthDate = patient.TryGetProperty("birthDate", out var birth) ? birth.GetString() : null;
            if (name is null || birthDate is null)
            {
                throw new InvalidDataException($"Patient/{patient.GetProperty("id").GetString()} (MRN {mrn}) has no name or birth date for the prompt.");
            }

            patients[mrn] = new CorpusPatient(patient.GetProperty("id").GetString()!, new PatientContext(mrn, name, birthDate));
        }

        return patients;
    }

    private static string? Identifier(JsonElement patient, string system) =>
        patient.TryGetProperty("identifier", out var identifiers)
            ? identifiers.EnumerateArray()
                .Where(i => i.TryGetProperty("system", out var s) && s.GetString() == system && i.TryGetProperty("value", out _))
                .Select(i => i.GetProperty("value").GetString())
                .FirstOrDefault()
            : null;

    private static string? DisplayName(JsonElement name)
    {
        var given = name.TryGetProperty("given", out var g) ? g.EnumerateArray().Select(x => x.GetString()) : [];
        var family = name.TryGetProperty("family", out var f) ? f.GetString() : null;
        var parts = given.Append(family).Where(part => !string.IsNullOrWhiteSpace(part)).ToList();
        return parts.Count == 0 ? null : string.Join(' ', parts);
    }
}
