using FhirAgentBench.Answers;
using FhirAgentBench.Authoring;
using FhirAgentBench.Cli;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Tests;

public sealed class AdmitCommandTests : IDisposable
{
    private static readonly GoldAdmitter Admitter = new(
        OracleRegistry.CreateDefault(), new OracleContext(Fixture.Index, Fixture.Now, Fixture.MrnSystem));

    private readonly string _directory = Directory.CreateTempSubdirectory("fab-admit-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string LockPath => Path.Combine(_directory, "gold.lock.json");

    private static Question Unanswerable(string id, int tier = 7) => new()
    {
        Id = id,
        Tier = tier,
        Template = "t",
        Persona = "nursing",
        Text = "?",
        Answer = new AnswerSpec { Type = AnswerType.Identifier },
        Oracle = new OracleCall("unanswerable", OracleParameters.Empty),
        SourcePath = id + ".yaml",
    };

    private (int Exit, string Out, string Err) Run(IReadOnlyList<Question> questions, bool updateLock = false)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = AdmitCommand.Execute(Admitter, questions, LockPath, updateLock, show: false, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    private string HashOf(string id) => Admitter.Admit(Unanswerable(id), null).GoldHash;

    [Fact]
    public void EmptyLock_WithoutFlag_DoesNotWriteLock()
    {
        var result = Run([Unanswerable("a")]);

        Assert.Equal(0, result.Exit);
        Assert.False(File.Exists(LockPath));
    }

    [Fact]
    public void UpdateLock_LocksAdmittedQuestions()
    {
        var result = Run([Unanswerable("a")], updateLock: true);

        Assert.Equal(0, result.Exit);
        Assert.Equal(HashOf("a"), GoldLock.Load(LockPath)["a"]);
    }

    [Fact]
    public void NewQuestion_WithNonEmptyLock_IsReportedAndNotLocked()
    {
        GoldLock.Save(LockPath, new Dictionary<string, string> { ["a"] = HashOf("a") });

        var result = Run([Unanswerable("a"), Unanswerable("b")]);

        Assert.NotEqual(0, result.Exit);
        Assert.Contains("NEW  b", result.Out);
        Assert.Contains("b: new question", result.Err);
        Assert.False(GoldLock.Load(LockPath).ContainsKey("b"));
    }

    [Fact]
    public void Drift_FailsUnlessRelocked()
    {
        GoldLock.Save(LockPath, new Dictionary<string, string> { ["a"] = "stale" });

        var drift = Run([Unanswerable("a")]);
        Assert.NotEqual(0, drift.Exit);
        Assert.Contains("gold drift", drift.Err);
        Assert.Equal("stale", GoldLock.Load(LockPath)["a"]);

        var relock = Run([Unanswerable("a")], updateLock: true);
        Assert.Equal(0, relock.Exit);
        Assert.Equal(HashOf("a"), GoldLock.Load(LockPath)["a"]);
    }

    [Fact]
    public void FailedQuestion_NeverWritesAHash_EvenWithUpdateLock()
    {
        var original = new Dictionary<string, string> { ["a"] = "stale", ["bad"] = "keep" };
        GoldLock.Save(LockPath, original);

        var result = Run([Unanswerable("a"), Unanswerable("bad", tier: 1)], updateLock: true);

        Assert.NotEqual(0, result.Exit);
        Assert.Contains("bad:", result.Err);
        Assert.Equal(original, GoldLock.Load(LockPath));
    }

    [Fact]
    public void UpdateLock_KeepsEntriesForQuestionsOutsideTheSet()
    {
        GoldLock.Save(LockPath, new Dictionary<string, string> { ["other"] = "keep" });

        Run([Unanswerable("a")], updateLock: true);

        var locked = GoldLock.Load(LockPath);
        Assert.Equal("keep", locked["other"]);
        Assert.Equal(HashOf("a"), locked["a"]);
    }
}
