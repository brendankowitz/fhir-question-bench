using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Execution;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Tests;

public sealed class FileModelTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("fab-files-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Write(string relativePath, string content)
    {
        var path = Path.Combine(_directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string WriteQuestion(string id, string answerYaml) => Write(
        $"questions/{id}.yaml",
        $"id: {id}\ntier: 1\ntemplate: t\npersona: nursing\nquestion: Q?\nanswer: {answerYaml}\noracle: {{ name: unanswerable }}\n");

    [Fact]
    public void Question_LoadsAllFields()
    {
        var path = Write("questions/t1-last-a1c-001.yaml", """
            id: t1-last-a1c-001
            tier: 1
            template: latest-lab
            tags: [REF]
            tool_groups: [patient-context, search]
            persona: primary-care
            patient: { mrn: MRN-ADA, name: Ada Lovelace, birth_date: "1960-03-01" }
            question: When was her last A1c, and what was it?
            answer:
              type: composite
              fields:
                value: { type: quantity, unit: "%", tolerance: 0.05 }
                date: { type: date }
            oracle:
              name: latest-observation
              parameters: { loinc: 4548-4 }
            verification: { status: unverified }
            """);

        var question = QuestionLoader.Load(path);

        Assert.Equal(1, question.Tier);
        Assert.Equal("MRN-ADA", question.Patient!.Mrn);
        Assert.Equal(AnswerType.Composite, question.Answer.Type);
        Assert.Equal(0.05m, question.Answer.Fields["value"].Tolerance);
        Assert.Equal("4548-4", question.Oracle.Parameters.GetString("loinc"));
        Assert.Equal(new[] { "patient-context", "search" }, question.ToolGroups);
    }

    [Fact]
    public void Question_IdMustMatchFileName()
    {
        var path = Write("questions/other.yaml", """
            id: t1-last-a1c-001
            tier: 1
            template: latest-lab
            persona: primary-care
            question: Q?
            answer: { type: integer }
            oracle: { name: unanswerable }
            """);

        var ex = Assert.Throws<InvalidDataException>(() => QuestionLoader.Load(path));
        Assert.Contains("must match the file name", ex.Message);
    }

    [Fact]
    public void Question_UnknownAnswerType_IsRejectedWithFileName()
    {
        var path = Write("questions/bad.yaml", """
            id: bad
            tier: 1
            template: x
            persona: nursing
            question: Q?
            answer: { type: integr }
            oracle: { name: unanswerable }
            """);

        var ex = Assert.Throws<InvalidDataException>(() => QuestionLoader.Load(path));
        Assert.Contains("bad.yaml", ex.Message);
        Assert.Contains("integr", ex.Message);
    }

    [Fact]
    public void Question_CompositeWithoutFields_IsRejected()
    {
        var path = WriteQuestion("empty-composite", "{ type: composite }");

        var ex = Assert.Throws<InvalidDataException>(() => QuestionLoader.Load(path));
        Assert.Contains("empty-composite.yaml", ex.Message);
        Assert.Contains("at least one field", ex.Message);
    }

    [Fact]
    public void Question_NegativeTolerance_IsRejected()
    {
        var path = WriteQuestion("neg-tolerance", "{ type: decimal, tolerance: -0.1 }");

        var ex = Assert.Throws<InvalidDataException>(() => QuestionLoader.Load(path));
        Assert.Contains("neg-tolerance.yaml", ex.Message);
        Assert.Contains("tolerance", ex.Message);
    }

    [Fact]
    public void Question_QuantityWithoutUnit_IsRejected()
    {
        var path = WriteQuestion("no-unit", "{ type: quantity }");

        var ex = Assert.Throws<InvalidDataException>(() => QuestionLoader.Load(path));
        Assert.Contains("no-unit.yaml", ex.Message);
        Assert.Contains("unit", ex.Message);
    }

    [Fact]
    public void QuestionSet_SkipsBlankLinesAndComments()
    {
        Write("questions/q1.yaml", "id: q1\ntier: 7\ntemplate: t\npersona: nursing\nquestion: Q?\nanswer: { type: boolean }\noracle: { name: unanswerable }\n");
        var set = Write("question-sets/s.txt", "# phase 0\n\nq1\n");

        var questions = QuestionLoader.LoadSet(Path.Combine(_directory, "questions"), set);

        Assert.Equal("q1", Assert.Single(questions).Id);
    }

    [Fact]
    public void OracleParameters_ReadsListsAndNumbers()
    {
        var path = Write("questions/q2.yaml", """
            id: q2
            tier: 6
            template: cohort
            persona: researcher
            question: How many?
            answer: { type: integer }
            oracle:
              name: cohort-lab-above-or-missing
              parameters: { snomed: [44054006, 73211009], threshold: 9, loinc: 4548-4 }
            """);

        var parameters = QuestionLoader.Load(path).Oracle.Parameters;

        Assert.Equal(new[] { "44054006", "73211009" }, parameters.GetStrings("snomed"));
        Assert.Equal(9m, parameters.GetDecimal("threshold"));
        Assert.Throws<InvalidDataException>(() => parameters.GetString("missing"));
    }

    [Fact]
    public void Corpus_LoadsNowAndSource()
    {
        Write("corpora/c1/corpus.yaml", """
            id: c1
            now: "2026-08-18T00:00:00Z"
            mrn_system: http://hospital.smarthealthit.org
            source: { url: "https://example.org/c.zip", sha256: abc }
            """);

        var corpus = CorpusDefinition.Load(Path.Combine(_directory, "corpora", "c1"));

        Assert.Equal(new DateTimeOffset(2026, 8, 18, 0, 0, 0, TimeSpan.Zero), corpus.Now);
        Assert.EndsWith(Path.Combine("c1", "data"), corpus.DataDirectory);
    }

    [Fact]
    public void Profile_ResolvesPromptRelativeToProfileFile()
    {
        Write("prompts/default.md", "prompt");
        var path = Write("profiles/p2.yaml", """
            id: p2
            harness: copilot-cli
            model: claude-sonnet-5.5
            builtin_tools: [view, rg]
            mcp_servers:
              fhir: { command: "${FAB_CLI_ON_FHIR}", args: [mcp, "{corpus}"], tools: [search_fhir_resources] }
            prompt_template: ../prompts/default.md
            max_ai_credits: 30
            timeout_minutes: 20
            """);

        var profile = ProfileLoader.Load(path);

        Assert.Equal(Profile.CopilotCli, profile.Harness);
        Assert.Equal("copilot", profile.Executable);
        Assert.True(File.Exists(profile.PromptTemplatePath));
        Assert.Equal(new[] { "search_fhir_resources" }, profile.McpServers["fhir"].Tools);
    }

    [Fact]
    public void Profile_UnknownHarness_IsRejected()
    {
        Write("prompts/default.md", "prompt");
        var path = Write("profiles/x.yaml", "id: x\nharness: claude-code\nprompt_template: ../prompts/default.md\n");

        var ex = Assert.Throws<InvalidDataException>(() => ProfileLoader.Load(path));
        Assert.Contains("claude-code", ex.Message);
    }
}
