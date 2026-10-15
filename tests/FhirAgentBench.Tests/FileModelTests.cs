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

    private string WriteRaw(string id, string questionText, string extra = "") => Write(
        $"questions/{id}.yaml",
        $"id: {id}\ntier: 1\ntemplate: t\npersona: nursing\n{extra}question: {questionText}\nanswer: {{ type: boolean }}\noracle: {{ name: unanswerable }}\n");

    [Fact]
    public void Question_EmptyTagsAndToolGroups_LoadAsEmpty()
    {
        var path = WriteRaw("e1", "Q?", "tags:\ntool_groups:\n");

        var question = QuestionLoader.Load(path);

        Assert.Empty(question.Tags);
        Assert.Empty(question.ToolGroups);
    }

    [Fact]
    public void Profile_EmptyCollections_LoadAsEmpty()
    {
        Write("prompts/default.md", "prompt");
        var path = Write("profiles/e.yaml", "id: e\nharness: oracle\nbuiltin_tools:\nmcp_servers:\nprompt_template: ../prompts/default.md\n");
        var withServer = Write("profiles/e2.yaml", "id: e2\nharness: oracle\nmcp_servers:\n  fhir:\n    command: x\n    args:\n    tools:\nprompt_template: ../prompts/default.md\n");

        var profile = ProfileLoader.Load(path);

        Assert.Empty(profile.McpServers);
        Assert.Empty(profile.BuiltinTools);
        var server = ProfileLoader.Load(withServer).McpServers["fhir"];
        Assert.Empty(server.Args);
        Assert.Empty(server.Tools);
    }

    [Fact]
    public void QuestionSet_MissingQuestionFile_NamesSetAndId()
    {
        var set = Write("question-sets/m.txt", "ghost\n");

        var ex = Assert.Throws<InvalidDataException>(() => QuestionLoader.LoadSet(Path.Combine(_directory, "questions"), set));
        Assert.Contains("m.txt", ex.Message);
        Assert.Contains("'ghost'", ex.Message);
    }

    [Fact]
    public void QuestionSet_DuplicateId_IsRejected()
    {
        WriteQuestion("d1", "{ type: boolean }");
        var set = Write("question-sets/d.txt", "d1\nd1\n");

        var ex = Assert.Throws<InvalidDataException>(() => QuestionLoader.LoadSet(Path.Combine(_directory, "questions"), set));
        Assert.Contains("more than once", ex.Message);
    }

    [Fact]
    public void Corpus_BadNow_NamesFileAndField()
    {
        Write("corpora/c2/corpus.yaml", "id: c2\nnow: not-a-date\nmrn_system: s\nsource: { url: u, sha256: h }\n");

        var ex = Assert.Throws<InvalidDataException>(() => CorpusDefinition.Load(Path.Combine(_directory, "corpora", "c2")));
        Assert.Contains("corpus.yaml", ex.Message);
        Assert.Contains("now", ex.Message);
    }

    [Fact]
    public void OracleParameters_BadNumber_NamesParameterAndValue()
    {
        var path = Write("questions/n1.yaml", "id: n1\ntier: 1\ntemplate: t\npersona: nursing\nquestion: Q?\nanswer: { type: integer }\noracle: { name: x, parameters: { threshold: abc, days: 1.5 } }\n");
        var parameters = QuestionLoader.Load(path).Oracle.Parameters;

        var decimalEx = Assert.Throws<InvalidDataException>(() => parameters.GetDecimal("threshold"));
        Assert.Contains("threshold", decimalEx.Message);
        Assert.Contains("abc", decimalEx.Message);
        var intEx = Assert.Throws<InvalidDataException>(() => parameters.GetInt("days"));
        Assert.Contains("days", intEx.Message);
    }

    [Fact]
    public void Question_BadBirthDate_IsRejected()
    {
        var path = WriteRaw("b1", "Q?", "patient: { mrn: M, name: N, birth_date: yesterday }\n");

        var ex = Assert.Throws<InvalidDataException>(() => QuestionLoader.Load(path));
        Assert.Contains("b1.yaml", ex.Message);
        Assert.Contains("birth_date", ex.Message);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("99")]
    [InlineData("integer, date")]
    public void Question_NumericOrListAnswerType_IsRejected(string type)
    {
        var path = WriteQuestion("numtype", "{ type: \"" + type + "\" }");

        var ex = Assert.Throws<InvalidDataException>(() => QuestionLoader.Load(path));
        Assert.Contains("unknown answer type", ex.Message);
    }

    [Theory]
    [InlineData("See http://example.org/x")]
    [InlineData("See https://example.org/x")]
    [InlineData("Check urn:oid:1.2.3 please")]
    [InlineData("Look at Patient/abc-123 now")]
    [InlineData("Is MedicationRequest/m1 active")]
    public void Question_TextWithIdentifiersOrUrls_IsRejected(string text)
    {
        var path = WriteRaw("leak", "\"" + text + "\"");

        var ex = Assert.Throws<InvalidDataException>(() => QuestionLoader.Load(path));
        Assert.Contains("leak.yaml", ex.Message);
    }

    [Fact]
    public void Question_TextWithOrdinarySlashes_IsAccepted()
    {
        var path = WriteRaw("ok", "Was blood pressure 120/80 and/or higher?");

        Assert.Equal("Was blood pressure 120/80 and/or higher?", QuestionLoader.Load(path).Text);
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
