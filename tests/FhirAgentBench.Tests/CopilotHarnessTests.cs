using System.Text.Json;
using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Execution;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Tests;

public sealed class CopilotHarnessTests : IDisposable
{
    private const string Token = "ghp_SECRET_TOKEN_VALUE_12345";

    private readonly string _directory = Directory.CreateTempSubdirectory("fab-copilot-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static Profile Shell(params (string Name, McpServer Server)[] servers) => new()
    {
        Id = "p",
        Harness = Profile.CopilotCli,
        Model = "claude-sonnet-5.5",
        BuiltinTools = ["view", "rg", "glob", "powershell", "read_powershell", "stop_powershell", "apply_patch"],
        McpServers = servers.ToDictionary(s => s.Name, s => s.Server),
        PromptTemplatePath = "prompt.md",
        MaxAiCredits = 25,
        SourcePath = "p.yaml",
    };

    private AttemptRequest Request() => new(
        new Question
        {
            Id = "q", Tier = 1, Template = "t", Persona = "nursing", Text = "?",
            Answer = new AnswerSpec { Type = AnswerType.Integer },
            Oracle = new OracleCall("unanswerable", OracleParameters.Empty), SourcePath = "q.yaml",
        },
        1, _directory, Path.Combine(_directory, "workspace"), "the prompt");

    private AttemptRequest RequestWithWorkspace()
    {
        var request = Request();
        Directory.CreateDirectory(request.WorkspaceDirectory);
        return request;
    }

    private string WriteScript(string name, string windowsBody, string unixBody)
    {
        var path = Path.Combine(_directory, OperatingSystem.IsWindows() ? name + ".cmd" : name + ".sh");
        File.WriteAllText(path, OperatingSystem.IsWindows() ? "@echo off\r\n" + windowsBody : "#!/bin/sh\n" + unixBody);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private static Dictionary<string, string> FullEnvironment() =>
        Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string)(e.Value ?? string.Empty));

    private CopilotCliHarness HarnessFor(string executable, params (string Name, McpServer Server)[] servers) =>
        new(Shell(servers) with { Executable = executable }, @"C:\corpus\data", Token);

    [Fact]
    public void Arguments_AreLockedDown()
    {
        var args = CopilotArguments.Build(Shell(), Request(), @"C:\corpus\data", mcpConfigPath: null);

        Assert.Equal(new[] { "-C", Path.Combine(_directory, "workspace"), "-p", "the prompt", "--model", "claude-sonnet-5.5" }, args.Take(6));
        Assert.Contains("--no-custom-instructions", args);
        Assert.Contains("--disable-builtin-mcps", args);
        Assert.Equal(@"C:\corpus\data", args[args.ToList().IndexOf("--add-dir") + 1]);
        Assert.Equal("25", args[args.ToList().IndexOf("--max-ai-credits") + 1]);
        Assert.Equal("COPILOT_GITHUB_TOKEN", args[args.ToList().IndexOf("--secret-env-vars") + 1]);
        Assert.DoesNotContain(args, a => a is "--allow-all" or "--yolo" or "--allow-all-paths" or "--allow-all-urls");
        Assert.DoesNotContain("--additional-mcp-config", args);
    }

    [Fact]
    public void Arguments_IncludePrefixedMcpToolsAndConfig()
    {
        var profile = Shell(("fhir", new McpServer("cli-on-fhir", ["mcp", "{corpus}"], ["search_fhir_resources", "read_resources"])));

        var args = CopilotArguments.Build(profile, Request(), "/data", Path.Combine(_directory, "mcp.json")).ToList();

        Assert.Contains("fhir-search_fhir_resources", args);
        Assert.Contains("fhir-read_resources", args);
        Assert.Equal("@" + Path.Combine(_directory, "mcp.json"), args[args.IndexOf("--additional-mcp-config") + 1]);
        Assert.DoesNotContain(args, a => a is "--allow-all" or "--yolo" or "--allow-all-paths" or "--allow-all-urls");
    }

    [Fact]
    public void McpConfig_ExpandsCorpusAndEnvironment()
    {
        Environment.SetEnvironmentVariable("FAB_TEST_TOOL", @"C:\tools\cli-on-fhir.exe");
        var profile = Shell(("fhir", new McpServer("${FAB_TEST_TOOL}", ["mcp", "{corpus}"], ["search_fhir_resources"])));
        var path = Path.Combine(_directory, "mcp.json");

        McpConfig.Write(profile, @"C:\corpus\data", path);

        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var server = json.RootElement.GetProperty("mcpServers").GetProperty("fhir");
        Assert.Equal(@"C:\tools\cli-on-fhir.exe", server.GetProperty("command").GetString());
        Assert.Equal(@"C:\corpus\data", server.GetProperty("args")[1].GetString());
    }

    [Fact]
    public void McpConfig_UnsetVariable_NamesTheVariable()
    {
        var profile = Shell(("fhir", new McpServer("${FAB_DEFINITELY_UNSET}", [], ["x"])));

        var ex = Assert.Throws<InvalidOperationException>(() => McpConfig.Write(profile, "/data", Path.Combine(_directory, "mcp.json")));

        Assert.Contains("FAB_DEFINITELY_UNSET", ex.Message);
    }

    [Fact]
    public async Task HarnessFactory_UnsetVariable_FailsBeforeAnyAttempt()
    {
        var profile = Shell(("fhir", new McpServer("${FAB_DEFINITELY_UNSET}", [], ["x"])));
        var corpus = new CorpusDefinition("c", DateTimeOffset.UnixEpoch, "mrn", "url", "sha", _directory);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => HarnessFactory.CreateAsync(
            profile, corpus, OracleRegistry.CreateDefault(), new OracleContext(Fixture.Index, Fixture.Now, Fixture.MrnSystem)));

        Assert.Contains("FAB_DEFINITELY_UNSET", ex.Message);
    }

    [Fact]
    public async Task ProcessRunner_CapturesExitCodeAndOutput()
    {
        var stdout = Path.Combine(_directory, "out.txt");

        var result = await ProcessRunner.RunAsync("dotnet", ["--version"], FullEnvironment(), _directory, stdout, Path.Combine(_directory, "err.txt"), TimeSpan.FromMinutes(1));

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Matches(@"\d+\.\d+", File.ReadAllText(stdout));
    }

    [Fact]
    public async Task ProcessRunner_TimeoutKillsProcessTree()
    {
        var (file, args) = OperatingSystem.IsWindows()
            ? ("cmd", new[] { "/c", "ping -n 30 127.0.0.1 > nul" })
            : ("sh", new[] { "-c", "sleep 30" });

        var result = await ProcessRunner.RunAsync(file, args, FullEnvironment(), _directory,
            Path.Combine(_directory, "out.txt"), Path.Combine(_directory, "err.txt"), TimeSpan.FromSeconds(2));

        Assert.True(result.TimedOut);
        Assert.True(result.Duration < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task ProcessRunner_Timeout_LeavesNoGrandchildRunning()
    {
        var grandchild = WriteScript("grandchild", "ping -n 6 127.0.0.1 > nul\r\necho late> late.txt\r\n", "sleep 5\necho late > late.txt\n");
        var parent = WriteScript("parent", $"cmd /c \"{grandchild}\"\r\n", $"\"{grandchild}\"\n");

        var result = await ProcessRunner.RunAsync(parent, [], FullEnvironment(), _directory,
            Path.Combine(_directory, "out.txt"), Path.Combine(_directory, "err.txt"), TimeSpan.FromSeconds(1));
        await Task.Delay(TimeSpan.FromSeconds(8));

        Assert.True(result.TimedOut);
        Assert.False(File.Exists(Path.Combine(_directory, "late.txt")));
    }

    [Fact]
    public async Task ProcessRunner_NormalExit_WithBackgroundGrandchild_ReturnsAndKillsIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var grandchild = WriteScript("grandchild", "ping -n 8 127.0.0.1 > nul\r\necho late> late.txt\r\n", "");
        var parent = WriteScript("parent", $"start \"\" /b cmd /c \"{grandchild}\"\r\nexit /b 0\r\n", "");

        var result = await ProcessRunner.RunAsync(parent, [], FullEnvironment(), _directory,
            Path.Combine(_directory, "out.txt"), Path.Combine(_directory, "err.txt"), TimeSpan.FromMinutes(1));
        await Task.Delay(TimeSpan.FromSeconds(10));

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Duration < TimeSpan.FromSeconds(10));
        Assert.False(File.Exists(Path.Combine(_directory, "late.txt")));
    }

    [Fact]
    public async Task ProcessRunner_Timeout_KillsOrphanedGrandchild()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var grandchild = WriteScript("grandchild", "ping -n 6 127.0.0.1 > nul\r\necho late> late.txt\r\n", "");
        var middle = WriteScript("middle", $"start \"\" /b cmd /c \"{grandchild}\"\r\nexit /b 0\r\n", "");
        var root = WriteScript("root", $"start \"\" /b cmd /c \"{middle}\"\r\nping -n 30 127.0.0.1 > nul\r\n", "");

        var result = await ProcessRunner.RunAsync(root, [], FullEnvironment(), _directory,
            Path.Combine(_directory, "out.txt"), Path.Combine(_directory, "err.txt"), TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromSeconds(8));

        Assert.True(result.TimedOut);
        Assert.False(File.Exists(Path.Combine(_directory, "late.txt")));
    }

    [Fact]
    public async Task ProcessRunner_UsesExactlyTheGivenEnvironment()
    {
        var fake = WriteScript(
            "env",
            "if defined FAB_DUMMY_SECRET echo leaked> leak.txt\r\nif defined FAB_GIVEN echo given> given.txt\r\n",
            "[ -z \"$FAB_DUMMY_SECRET\" ] || echo leaked > leak.txt\n[ -z \"$FAB_GIVEN\" ] || echo given > given.txt\n");
        var environment = FullEnvironment().Where(e => e.Key != "FAB_DUMMY_SECRET").ToDictionary(e => e.Key, e => e.Value);
        environment["FAB_GIVEN"] = "1";
        Environment.SetEnvironmentVariable("FAB_DUMMY_SECRET", "s3cret");
        try
        {
            await ProcessRunner.RunAsync(fake, [], environment, _directory,
                Path.Combine(_directory, "out.txt"), Path.Combine(_directory, "err.txt"), TimeSpan.FromMinutes(1));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAB_DUMMY_SECRET", null);
        }

        Assert.True(File.Exists(Path.Combine(_directory, "given.txt")));
        Assert.False(File.Exists(Path.Combine(_directory, "leak.txt")));
    }

    [Fact]
    public async Task Harness_ChildSeesOnlyAllowlistedEnvironment()
    {
        var fake = WriteScript(
            "fake-env",
            "if defined GH_TOKEN echo x> gh.txt\r\nif defined FAB_DUMMY_SECRET echo x> secret.txt\r\nif defined PATH echo x> path.txt\r\nif defined COPILOT_GITHUB_TOKEN echo x> token.txt\r\nif defined FAB_REFERENCED echo x> referenced.txt\r\n",
            "[ -z \"$GH_TOKEN\" ] || echo x > gh.txt\n[ -z \"$FAB_DUMMY_SECRET\" ] || echo x > secret.txt\n[ -z \"$PATH\" ] || echo x > path.txt\n[ -z \"$COPILOT_GITHUB_TOKEN\" ] || echo x > token.txt\n[ -z \"$FAB_REFERENCED\" ] || echo x > referenced.txt\n");
        var server = ("fhir", new McpServer("${FAB_REFERENCED}", ["mcp"], ["x"]));
        var request = RequestWithWorkspace();
        Environment.SetEnvironmentVariable("GH_TOKEN", "gh-leak");
        Environment.SetEnvironmentVariable("FAB_DUMMY_SECRET", "s3cret");
        Environment.SetEnvironmentVariable("FAB_REFERENCED", "tool");
        try
        {
            await HarnessFor(fake, server).RunAsync(request);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", null);
            Environment.SetEnvironmentVariable("FAB_DUMMY_SECRET", null);
            Environment.SetEnvironmentVariable("FAB_REFERENCED", null);
        }

        Assert.False(File.Exists(Path.Combine(request.WorkspaceDirectory, "gh.txt")));
        Assert.False(File.Exists(Path.Combine(request.WorkspaceDirectory, "secret.txt")));
        Assert.True(File.Exists(Path.Combine(request.WorkspaceDirectory, "path.txt")));
        Assert.True(File.Exists(Path.Combine(request.WorkspaceDirectory, "token.txt")));
        Assert.True(File.Exists(Path.Combine(request.WorkspaceDirectory, "referenced.txt")));
    }

    [Theory]
    [InlineData("COPILOT_GITHUB_TOKEN")]
    [InlineData("GH_TOKEN")]
    [InlineData("GITHUB_TOKEN")]
    public void McpConfig_RefusesCredentialVariables(string name)
    {
        var profile = Shell(("fhir", new McpServer("tool", ["--key", "${" + name + "}"], ["x"])));

        var ex = Assert.Throws<InvalidDataException>(() => McpConfig.Write(profile, "/data", Path.Combine(_directory, "mcp.json")));

        Assert.Contains(name, ex.Message);
        Assert.Contains("'p'", ex.Message);
    }

    [Fact]
    public async Task ResolveToken_WithoutEnvironmentOrGh_SaysWhatToDo()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => HarnessFactory.ResolveTokenAsync(_ => null, Path.Combine(_directory, "no-such-gh")));

        Assert.Contains("COPILOT_GITHUB_TOKEN", ex.Message);
        Assert.Contains("gh auth login", ex.Message);
    }

    [Fact]
    public async Task ResolveToken_PrefersEnvironment()
    {
        var token = await HarnessFactory.ResolveTokenAsync(name => name == "GH_TOKEN" ? "abc" : null, "no-such-gh");

        Assert.Equal("abc", token);
    }

    [Theory]
    [InlineData("copilot.cmd")]
    [InlineData(@"C:\tools\COPILOT.BAT")]
    public async Task HarnessFactory_RejectsShellScriptExecutable(string executable)
    {
        var profile = Shell() with { Executable = executable };
        var corpus = new CorpusDefinition("c", DateTimeOffset.UnixEpoch, "mrn", "url", "sha", _directory);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => HarnessFactory.CreateAsync(
            profile, corpus, OracleRegistry.CreateDefault(), new OracleContext(Fixture.Index, Fixture.Now, Fixture.MrnSystem)));

        Assert.Contains("copilot.exe", ex.Message);
    }

    [Fact]
    public async Task ProcessRunner_Cancellation_Propagates()
    {
        var (file, args) = OperatingSystem.IsWindows()
            ? ("cmd", new[] { "/c", "ping -n 30 127.0.0.1 > nul" })
            : ("sh", new[] { "-c", "sleep 30" });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunAsync(file, args, FullEnvironment(), _directory,
            Path.Combine(_directory, "out.txt"), Path.Combine(_directory, "err.txt"), TimeSpan.FromMinutes(5), cts.Token));
    }

    [Fact]
    public async Task Harness_UnstartableExecutable_IsFailedNotException()
    {
        var outcome = await HarnessFor(Path.Combine(_directory, "does-not-exist.exe")).RunAsync(RequestWithWorkspace());

        Assert.Equal(AttemptEnd.Failed, outcome.End);
        Assert.Contains("could not start", outcome.Detail);
    }

    [Fact]
    public async Task Harness_NonZeroExit_IsFailed()
    {
        var fake = WriteScript("fake-fail", "exit /b 3\r\n", "exit 3\n");

        var outcome = await HarnessFor(fake).RunAsync(RequestWithWorkspace());

        Assert.Equal(AttemptEnd.Failed, outcome.End);
        Assert.Contains("code 3", outcome.Detail);
    }

    [Fact]
    public async Task Harness_NeverWritesTheTokenToDisk()
    {
        var fake = WriteScript(
            "fake-ok",
            "if \"%COPILOT_GITHUB_TOKEN%\"==\"\" exit /b 9\r\necho args: %*\r\necho {\"type\":\"result\"}\r\necho {}> answer.json\r\n",
            "[ -n \"$COPILOT_GITHUB_TOKEN\" ] || exit 9\necho \"args: $@\"\necho '{}' > answer.json\n");
        var server = ("fhir", new McpServer("cli-on-fhir", ["mcp", "{corpus}"], ["search_fhir_resources"]));
        var request = RequestWithWorkspace();

        var outcome = await HarnessFor(fake, server).RunAsync(request);

        Assert.Equal(AttemptEnd.Completed, outcome.End);
        Assert.Contains("--secret-env-vars", File.ReadAllText(Path.Combine(_directory, "transcript.jsonl")));
        Assert.True(File.Exists(Path.Combine(_directory, "mcp.json")));
        var leaks = Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains(Token, StringComparison.Ordinal))
            .ToList();
        Assert.Empty(leaks);
    }
}
