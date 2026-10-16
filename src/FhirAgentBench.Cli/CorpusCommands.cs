using System.CommandLine;
using FhirAgentBench.Authoring;
using FhirAgentBench.Corpus;
using FhirAgentBench.Oracles;

namespace FhirAgentBench.Cli;

internal static class CorpusCommands
{
    public static Command Create()
    {
        var corpus = new Command("corpus", "Fetch, verify and explore a pinned corpus.");
        corpus.Subcommands.Add(Fetch());
        corpus.Subcommands.Add(Verify());
        corpus.Subcommands.Add(Stats());
        corpus.Subcommands.Add(Patients());
        corpus.Subcommands.Add(Codes());
        return corpus;
    }

    private static Argument<string> CorpusArgument() => new("corpus-dir") { Description = "Folder containing corpus.yaml." };

    private static Command Fetch()
    {
        var dir = CorpusArgument();
        var command = new Command("fetch", "Download the corpus source, check its sha256 and extract it read-only.") { dir };
        command.SetAction(async (parse, cancellationToken) =>
        {
            using var http = new HttpClient();
            Console.WriteLine(await CorpusFetcher.FetchAsync(CorpusDefinition.Load(parse.GetValue(dir)!), http, cancellationToken));
            return 0;
        });
        return command;
    }

    private static Command Verify()
    {
        var dir = CorpusArgument();
        var command = new Command("verify", "Check every data file against data.manifest.json.") { dir };
        command.SetAction(parse =>
        {
            var corpus = CorpusDefinition.Load(parse.GetValue(dir)!);
            var problems = CorpusManifest.Load(corpus.ManifestPath).Verify(corpus.DataDirectory);
            foreach (var problem in problems)
            {
                Console.Error.WriteLine(problem);
            }

            Console.WriteLine(problems.Count == 0 ? "Corpus matches its manifest." : $"{problems.Count} problem(s).");
            return problems.Count == 0 ? 0 : 1;
        });
        return command;
    }

    private static Command Stats()
    {
        var dir = CorpusArgument();
        var command = new Command("stats", "Resource counts by type and the latest Observation time.") { dir };
        command.SetAction(parse =>
        {
            var corpus = CorpusDefinition.Load(parse.GetValue(dir)!);
            var index = CorpusIndex.Load(corpus.DataDirectory);
            Console.WriteLine($"{index.Count} resources");
            foreach (var type in new[] { "Patient", "Encounter", "Condition", "Observation", "MedicationRequest", "Medication", "AllergyIntolerance", "Immunization", "Procedure" })
            {
                Console.WriteLine($"  {type}: {index.OfType(type).Count}");
            }

            Console.WriteLine($"Latest Observation: {CorpusQueries.LatestObservationTime(index):O}");
            return 0;
        });
        return command;
    }

    private static Command Patients()
    {
        var dir = CorpusArgument();
        var loinc = new Option<string>("--loinc") { Description = "LOINC code to count per patient.", Required = true };
        var top = new Option<int>("--top") { Description = "Rows to print.", DefaultValueFactory = _ => 20 };
        var command = new Command("patients", "List patients with valid results for a LOINC code, as of the corpus 'now'.") { dir, loinc, top };
        command.SetAction(parse =>
        {
            var corpus = CorpusDefinition.Load(parse.GetValue(dir)!);
            var context = new OracleContext(CorpusIndex.Load(corpus.DataDirectory), corpus.Now, corpus.MrnSystem);
            Console.WriteLine("MRN\tName\tBirthDate\tCount\tLatest");
            foreach (var row in CorpusQueries.PatientsWithObservation(context, parse.GetValue(loinc)!).Take(parse.GetValue(top)))
            {
                Console.WriteLine($"{row.Mrn}\t{row.Name}\t{row.BirthDate}\t{row.Count}\t{row.LatestDate}");
            }

            return 0;
        });
        return command;
    }

    private static Command Codes()
    {
        var dir = CorpusArgument();
        var type = new Option<string>("--type") { Description = "Resource type, e.g. Condition or MedicationRequest.", Required = true };
        var display = new Option<string>("--display-contains") { Description = "Case-insensitive display filter.", Required = true };
        var command = new Command("codes", "Count codes whose display contains a phrase, to build oracle code lists.") { dir, type, display };
        command.SetAction(parse =>
        {
            var corpus = CorpusDefinition.Load(parse.GetValue(dir)!);
            var index = CorpusIndex.Load(corpus.DataDirectory);
            Console.WriteLine("Count\tSystem\tCode\tDisplay");
            foreach (var row in CorpusQueries.CodeCounts(index, parse.GetValue(type)!, parse.GetValue(display)!))
            {
                Console.WriteLine($"{row.Count}\t{row.System}\t{row.Code}\t{row.Display}");
            }

            return 0;
        });
        return command;
    }
}
