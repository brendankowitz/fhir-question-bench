using System.CommandLine;
using FhirAgentBench.Cli;

var root = new RootCommand("FHIR agent bench: measure how agent harnesses and models answer clinical questions over FHIR data.");
root.Subcommands.Add(CorpusCommands.Create());
root.Subcommands.Add(AdmitCommand.Create());
root.Subcommands.Add(RunCommand.Create());
root.Subcommands.Add(ReportCommand.Create());
root.Subcommands.Add(ImportCommand.Create());

try
{
    return await root.Parse(args).InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false });
}
catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or HttpRequestException
    or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or UnauthorizedAccessException)
{
    await Console.Error.WriteLineAsync($"error: {ex.Message}");
    return 1;
}
