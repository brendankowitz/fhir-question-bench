using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Execution;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Tests;

/// <summary>Per-question "now" and assumption, typed set items, and gold read from a file by the static-answer oracle.</summary>
public sealed class QuestionContextTests : IDisposable
{
    private static readonly CorpusDefinition Corpus = new("fixture", Fixture.Now, Fixture.MrnSystem, "u", "h",
        Path.GetDirectoryName(Fixture.CorpusDirectory)!);

    private readonly string _directory = Directory.CreateTempSubdirectory("fab-context-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string WriteQuestion(string id, string body)
    {
        var path = Path.Combine(_directory, "questions", id + ".yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"id: {id}\ntier: 2\ntemplate: t\npersona: physician\nquestion: Q?\n{body}");
        return path;
    }

    private static Question Question(DateTimeOffset? now = null, string assumption = "") => new()
    {
        Id = "q",
        Tier = 2,
        Template = "t",
        Persona = "physician",
        Text = "Q?",
        Answer = new AnswerSpec { Type = AnswerType.Integer },
        Oracle = new OracleCall("clock", OracleParameters.Empty),
        Now = now,
        Assumption = assumption,
        SourcePath = "q.yaml",
    };

    [Fact]
    public void Loader_ReadsNowAssumptionAndTypedSetItems()
    {
        var path = WriteQuestion("q1", """
            now: "2133-12-31T23:59:00-05:00"
            assumption: "  Search for 'Daily Weight' to find weight.  "
            answer: { type: set, nullable: true, null_accepts_abstain: true, items: { type: decimal, tolerance: 0.01, relative_tolerance: 0.001 } }
            oracle: { name: unanswerable }
            """);

        var question = QuestionLoader.Load(path);

        Assert.Equal(new DateTimeOffset(2133, 12, 31, 23, 59, 0, TimeSpan.FromHours(-5)), question.Now);
        Assert.Equal("Search for 'Daily Weight' to find weight.", question.Assumption);
        Assert.True(question.Answer.NullAcceptsAbstain);
        Assert.Equal(AnswerType.Decimal, question.Answer.Items!.Type);
        Assert.Equal(0.001m, question.Answer.Items.RelativeTolerance);
    }

    [Fact]
    public void Loader_WithoutNowOrAssumption_DefaultsToCorpusNowAndEmpty()
    {
        var question = QuestionLoader.Load(WriteQuestion("q2", "answer: { type: integer }\noracle: { name: unanswerable }\n"));

        Assert.Null(question.Now);
        Assert.Equal("", question.Assumption);
    }

    [Theory]
    [InlineData("now: \"yesterday\"\nanswer: { type: integer }", "now 'yesterday'")]
    [InlineData("answer: { type: integer, items: { type: text } }", "only a set answer may have")]
    [InlineData("answer: { type: set, items: { type: set } }", "items must be")]
    [InlineData("answer: { type: set, items: { type: text, nullable: true } }", "items must be")]
    [InlineData("answer: { type: decimal, null_accepts_abstain: true }", "needs nullable: true")]
    [InlineData("answer: { type: decimal, relative_tolerance: -0.1 }", "must not be negative")]
    public void Loader_RejectsInvalidContextAndSpecs(string body, string message)
    {
        var path = WriteQuestion("q3", body + "\noracle: { name: unanswerable }\n");

        var ex = Assert.Throws<InvalidDataException>(() => QuestionLoader.Load(path));

        Assert.Contains(message, ex.Message);
        Assert.Contains("q3.yaml", ex.Message);
    }

    [Fact]
    public void Render_UsesQuestionNowAndAppendsAssumption()
    {
        var question = Question(new DateTimeOffset(2133, 12, 31, 23, 59, 0, TimeSpan.FromHours(-5)), "Search for 'Daily Weight' to find weight.");

        var prompt = PromptRenderer.Render("Treat now as {{now}}.{{assumption}}\n{{question}}", question, Corpus, "ws");

        Assert.Equal("Treat now as 2133-12-31T23:59:00-05:00. Search for 'Daily Weight' to find weight.\nQ?", prompt);
    }

    [Fact]
    public void Render_WithoutAssumption_IsUnchanged()
    {
        var prompt = PromptRenderer.Render("Treat now as {{now}}.{{assumption}}\nnext", Question(), Corpus, "ws");

        Assert.Equal("Treat now as 2026-01-15T00:00:00+00:00.\nnext", prompt);
    }

    [Fact]
    public void Registry_GivesOraclesTheQuestionsNow()
    {
        var registry = new OracleRegistry([new ClockOracle()]);
        var context = new OracleContext(Fixture.Index, Fixture.Now, Fixture.MrnSystem);
        var questionNow = new DateTimeOffset(2133, 12, 31, 23, 59, 0, TimeSpan.FromHours(-5));

        Assert.Equal(questionNow.ToString("O"), registry.Compute(context, Question(questionNow)).Value!.Value.GetString());
        Assert.Equal(Fixture.Now.ToString("O"), registry.Compute(context, Question()).Value!.Value.GetString());
    }

    [Fact]
    public void StaticAnswer_ReadsGoldAndEvidenceRelativeToTheQuestion()
    {
        File.WriteAllText(Path.Combine(_directory, "gold.json"), """
            { "q4": {"status":"answered","answer":"2133-12-31 02:00:00","evidence":["Observation/o1"]},
              "other": {"status":"answered","answer":null,"evidence":[]} }
            """);
        var question = QuestionLoader.Load(WriteQuestion("q4",
            "answer: { type: datetime, nullable: true }\noracle: { name: static-answer, parameters: { gold_file: ../gold.json } }\n"));
        var context = new OracleContext(Fixture.Index, Fixture.Now, Fixture.MrnSystem);

        var gold = OracleRegistry.CreateDefault().Compute(context, question);

        Assert.Equal("2133-12-31 02:00:00", gold.Value!.Value.GetString());
        Assert.Equal(["Observation/o1"], gold.Evidence);
    }

    [Fact]
    public void StaticAnswer_MissingQuestionOrFile_NamesIt()
    {
        File.WriteAllText(Path.Combine(_directory, "gold.json"), "{}");
        var context = new OracleContext(Fixture.Index, Fixture.Now, Fixture.MrnSystem);
        var missingId = QuestionLoader.Load(WriteQuestion("q5",
            "answer: { type: integer }\noracle: { name: static-answer, parameters: { gold_file: ../gold.json } }\n"));
        var missingFile = QuestionLoader.Load(WriteQuestion("q6",
            "answer: { type: integer }\noracle: { name: static-answer, parameters: { gold_file: ../absent.json } }\n"));

        Assert.Contains("'q5'", Assert.Throws<InvalidDataException>(() => OracleRegistry.CreateDefault().Compute(context, missingId)).Message);
        Assert.Contains("absent.json", Assert.Throws<FileNotFoundException>(() => OracleRegistry.CreateDefault().Compute(context, missingFile)).Message);
    }

    private sealed class ClockOracle : IOracle
    {
        public string Name => "clock";

        public Answer Compute(OracleContext context, Question question) => Answer.Of(context.Now.ToString("O"), []);
    }
}
