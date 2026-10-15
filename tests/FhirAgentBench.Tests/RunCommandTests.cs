using System.CommandLine;
using FhirAgentBench.Cli;
using FhirAgentBench.Execution;

namespace FhirAgentBench.Tests;

public sealed class RunCommandTests
{
    private static Profile ProfileFor(string harness) => new()
    {
        Id = "p", Harness = harness, PromptTemplatePath = "p.md", SourcePath = "p.yaml",
    };

    [Theory]
    [InlineData(Profile.CopilotCli, true)]
    [InlineData(Profile.Oracle, false)]
    [InlineData(Profile.Abstain, false)]
    public void RequiresConfirmation_OnlyForPaidHarness(string harness, bool expected) =>
        Assert.Equal(expected, RunCommand.RequiresConfirmation(ProfileFor(harness)));

    [Fact]
    public async Task ZeroRepeats_ExitsWithUsageCode()
    {
        var root = new RootCommand { RunCommand.Create() };

        var exit = await root.Parse(["run", "--profile", "x", "--corpus", "x", "--set", "x", "--repeats", "0"]).InvokeAsync();

        Assert.Equal(2, exit);
    }

    [Fact]
    public void CreateRunDirectory_RefusesExistingDirectory()
    {
        var runs = Directory.CreateTempSubdirectory("fab-runs-").FullName;
        RunCommand.CreateRunDirectory(runs, "r1");

        var ex = Assert.Throws<InvalidOperationException>(() => RunCommand.CreateRunDirectory(runs, "r1"));

        Assert.Contains(Path.Combine(runs, "r1"), ex.Message);
    }

    [Fact]
    public void NewRunId_IsUniqueAndEndsWithSuffix()
    {
        var first = RunCommand.NewRunId("b0");
        var second = RunCommand.NewRunId("b0");

        Assert.NotEqual(first, second);
        Assert.Contains("-b0-", first);
    }
}
