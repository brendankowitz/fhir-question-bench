using System.Text;
using System.Text.Json;
using FhirAgentBench.Answers;
using FhirAgentBench.Authoring;
using FhirAgentBench.Cli;
using FhirAgentBench.Evidence;
using FhirAgentBench.Execution;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;
using FhirAgentBench.Reporting;

namespace FhirAgentBench.Tests;

public sealed class EvidenceTests : IDisposable
{
    private static readonly OracleContext Context = new(Fixture.Index, Fixture.Now, Fixture.MrnSystem);

    private readonly string _directory = Directory.CreateTempSubdirectory("fab-evidence-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("Observation/o1", EvidenceKind.Reference)]
    [InlineData("Patient/5693c080-f485-91e7-54b9-ad9a8c12af62", EvidenceKind.Reference)]
    [InlineData("Encounter?class=EMER&date=ge2024-08-17", EvidenceKind.Search)]
    [InlineData("Patient/ada/Observation?code=http://loinc.org|4548-4", EvidenceKind.Search)]
    [InlineData("Patient?", EvidenceKind.Invalid)]
    [InlineData("Encounter", EvidenceKind.Invalid)]
    [InlineData("Observation/o1/_history/2", EvidenceKind.Invalid)]
    [InlineData("Organization/ada/Observation?code=x", EvidenceKind.Invalid)]
    [InlineData("Observation?code=a b", EvidenceKind.Invalid)]
    [InlineData("Observation?-x", EvidenceKind.Invalid)]
    [InlineData("observation/o1", EvidenceKind.Invalid)]
    [InlineData("Foo/bar", EvidenceKind.Invalid)]
    [InlineData("Foo?a=b", EvidenceKind.Invalid)]
    [InlineData("Observation?@c:/secret.rsp", EvidenceKind.Invalid)]
    [InlineData("Observation?1=2", EvidenceKind.Invalid)]
    [InlineData("Observation?_count=1", EvidenceKind.Search)]
    [InlineData("Observation?code=a|b&x=$y&z=%26", EvidenceKind.Search)]
    [InlineData("the lab result", EvidenceKind.Invalid)]
    [InlineData("", EvidenceKind.Invalid)]
    public void Entry_IsClassified(string text, EvidenceKind expected)
    {
        Assert.Equal(expected, EvidenceEntry.Parse(text).Kind);
    }

    [Fact]
    public void Search_NormalisesParameterOrderAndCompartment()
    {
        var plain = EvidenceEntry.Parse("Encounter?date=ge2024&class=EMER");
        var compartment = EvidenceEntry.Parse("Patient/ada/Encounter?class=EMER&date=ge2024");

        Assert.Equal("Patient/ada/Encounter?class=EMER&date=ge2024", plain.NormalizedSearch("Patient/ada"));
        Assert.Equal(plain.NormalizedSearch("Patient/ada"), compartment.NormalizedSearch());
        Assert.NotEqual(plain.NormalizedSearch(), compartment.NormalizedSearch());
    }

    [Fact]
    public void AnswerJson_KeepsEveryEvidenceEntry_EvenNonStrings()
    {
        var answer = AnswerJson.Parse("""{"status":"answered","answer":1,"evidence":["Observation/o1","Encounter?class=EMER",7,null,"junk"]}""");

        Assert.Equal(5, answer.Evidence.Count);
        Assert.Equal(
            new[] { EvidenceKind.Reference, EvidenceKind.Search, EvidenceKind.Invalid, EvidenceKind.Invalid, EvidenceKind.Invalid },
            answer.ClassifiedEvidence().Select(e => e.Kind));
    }

    [Fact]
    public void AnswerJson_KeepsEvidenceThatIsNotAnArray()
    {
        var answer = AnswerJson.Parse("""{"status":"answered","answer":1,"evidence":"Observation/o1"}""");

        Assert.Equal(EvidenceKind.Invalid, Assert.Single(answer.ClassifiedEvidence()).Kind);
    }

    [Fact]
    public void Score_ComputesRecallAndPrecision()
    {
        var (recall, precision) = EvidenceAnalyzer.Score(["a", "b", "c", "d"], new HashSet<string> { "a", "b", "x" });

        Assert.Equal(0.5, recall);
        Assert.Equal(2 / 3.0, precision!.Value, precision: 12);
    }

    [Fact]
    public void Score_IsNullWhereTheDenominatorIsEmpty()
    {
        var noOracleEvidence = EvidenceAnalyzer.Score([], new HashSet<string> { "a" });
        var nothingCited = EvidenceAnalyzer.Score(["a"], new HashSet<string>());

        Assert.Null(noOracleEvidence.Recall);
        Assert.Null(noOracleEvidence.Precision);
        Assert.Equal(0.0, nothingCited.Recall);
        Assert.Null(nothingCited.Precision);
    }

    private static CliInvoker Pages(int total, int pageLimit, List<IReadOnlyList<string>> calls) =>
        (arguments, _) =>
        {
            calls.Add(arguments);
            var offset = int.Parse(arguments[arguments.ToList().IndexOf("--offset") + 1], System.Globalization.CultureInfo.InvariantCulture);
            var count = int.Parse(arguments[arguments.ToList().IndexOf("--count") + 1], System.Globalization.CultureInfo.InvariantCulture);
            var resources = Enumerable.Range(offset, Math.Max(0, Math.Min(Math.Min(pageLimit, count), total - offset)))
                .Select(i => new { resourceType = "Encounter", id = $"e{i}" });
            var json = JsonSerializer.Serialize(new { facts = new { total, offset, resources }, issues = Array.Empty<object>() });
            return Task.FromResult(new CliOutput(0, false, json));
        };

    [Fact]
    public async Task Resolver_PagesByOffsetUntilTheTotalIsReached()
    {
        var calls = new List<IReadOnlyList<string>>();
        var resolver = new EvidenceResolver(Pages(total: 7, pageLimit: 3, calls), "/data");

        var result = await resolver.ResolveAsync(EvidenceEntry.Parse("Encounter?class=EMER"), "Patient/p1");

        Assert.True(result.IsResolved);
        Assert.Equal(7, result.Keys.Count);
        Assert.Equal(3, calls.Count);
        var first = calls[0].ToList();
        Assert.Equal(["search"], first.Take(1));
        Assert.Equal(["--", "/data", "Encounter", "class=EMER"], first.Skip(first.IndexOf("--")));
        Assert.Equal("Patient/p1", first[first.IndexOf("--compartment") + 1]);
        Assert.Equal("id", first[first.IndexOf("--elements") + 1]);
        Assert.Equal("51200", first[first.IndexOf("--max-bytes") + 1]);
        Assert.Equal(["0", "3", "6"], calls.Select(c => c[c.ToList().IndexOf("--offset") + 1]));
    }

    [Fact]
    public async Task Resolver_StopsAtTheCountTheSearchAsked_NotAtTheTotal()
    {
        var calls = new List<IReadOnlyList<string>>();
        var resolver = new EvidenceResolver(Pages(total: 40, pageLimit: 50, calls), "/data");

        var result = await resolver.ResolveAsync(EvidenceEntry.Parse("Observation?code=x&_sort=-date&_count=1"));

        Assert.Single(result.Keys);
        var call = Assert.Single(calls).ToList();
        Assert.Equal("1", call[call.IndexOf("--count") + 1]);
    }

    [Fact]
    public async Task Resolver_AnswersARepeatedSearchFromMemory()
    {
        var calls = new List<IReadOnlyList<string>>();
        var resolver = new EvidenceResolver(Pages(2, 50, calls), "/data");

        await resolver.ResolveAsync(EvidenceEntry.Parse("Encounter?a=1&b=2"));
        await resolver.ResolveAsync(EvidenceEntry.Parse("Encounter?b=2&a=1"));

        Assert.Single(calls);
    }

    [Fact]
    public async Task Resolver_ZeroMatchesIsResolvedAndEmpty()
    {
        var resolver = new EvidenceResolver(Pages(0, 50, []), "/data");

        var result = await resolver.ResolveAsync(EvidenceEntry.Parse("Encounter?class=NOPE"));

        Assert.True(result.IsResolved);
        Assert.Empty(result.Keys);
    }

    public static TheoryData<string, int, bool, string> Failures => new()
    {
        { """{"facts":null,"issues":[{"severity":"error","code":"unknown-search-parameter","diagnostics":"bogus"}]}""", 1, false, "unknown-search-parameter" },
        { """{"facts":{"total":1,"resources":[{"resourceType":"Encounter","id":"e"}]},"issues":[{"severity":"error","code":"boom","diagnostics":"x"}]}""", 0, false, "boom" },
        { """{"facts":{"total":1,"resources":[]},"issues":[]}""", 0, false, "returned none" },
        { """{"facts":{"total":1,"resources":[{"resourceType":"Encounter","id":"e"}]},"issues":[]}""", 3, false, "code 3" },
        { "not json", 0, false, "no JSON" },
        { """{"issues":[]}""", 0, false, "no facts" },
        { "", -1, true, "timed out" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Resolver_ReportsUnresolvedWithReason(string stdout, int exitCode, bool timedOut, string reason)
    {
        var resolver = new EvidenceResolver((_, _) => Task.FromResult(new CliOutput(exitCode, timedOut, stdout)), "/data");

        var result = await resolver.ResolveAsync(EvidenceEntry.Parse("Encounter?class=EMER"));

        Assert.False(result.IsResolved);
        Assert.Empty(result.Keys);
        Assert.Contains(reason, result.UnresolvedReason);
    }

    [Fact]
    public async Task Resolver_NamesTheFirstStderrLine_WhenNoJsonWasPrinted()
    {
        var resolver = new EvidenceResolver((_, _) => Task.FromResult(new CliOutput(1, false, "", "Unrecognized command or argument '--max-bytes'.\nUsage")), "/data");

        var result = await resolver.ResolveAsync(EvidenceEntry.Parse("Encounter?class=EMER"));

        Assert.Contains("Unrecognized command or argument '--max-bytes'.", result.UnresolvedReason);
        Assert.DoesNotContain("Usage", result.UnresolvedReason);
    }

    [Fact]
    public async Task Resolver_ReportsUnresolved_WhenTheProcessCannotStart()
    {
        var resolver = new EvidenceResolver((_, _) => throw new InvalidOperationException("Could not start 'x'."), "/data");

        var result = await resolver.ResolveAsync(EvidenceEntry.Parse("Encounter?class=EMER"));

        Assert.Contains("Could not start", result.UnresolvedReason);
    }

    [Fact]
    public async Task Resolver_RunsAnExecutableAsAChildProcess_WithItsCacheOutsideTheCorpus()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var recorded = Path.Combine(_directory, "args.txt");
        var script = FakeExecutable("fake-cli", recorded, """{"facts":{"total":1,"resources":[{"resourceType":"Encounter","id":"e1"}]},"issues":[]}""");
        var cache = Path.Combine(_directory, "run", "cache");
        var resolver = EvidenceResolver.ForExecutable(script, Path.Combine(_directory, "corpus"), cache, Path.Combine(_directory, "run", "scratch"));

        var result = await resolver.ResolveAsync(EvidenceEntry.Parse("Encounter?class=EMER"));

        Assert.Equal(["Encounter/e1"], result.Keys);
        var args = File.ReadAllText(recorded);
        Assert.Contains(cache, args);
        Assert.True(args.IndexOf("--cache-dir", StringComparison.Ordinal) < args.IndexOf(" -- ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("shim.cmd")]
    [InlineData("shim.bat")]
    public void Resolver_RefusesCommandShimsOnWindows(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var ex = Assert.Throws<ArgumentException>(
            () => EvidenceResolver.ForExecutable(name, "/data", Path.Combine(_directory, "c"), Path.Combine(_directory, "s")));

        Assert.Contains(".exe", ex.Message);
    }

    [Fact]
    public void SystemOnlyEnvironment_LeavesOutOtherVariables()
    {
        Environment.SetEnvironmentVariable("FAB_TEST_SECRET", "x");
        try
        {
            var environment = CopilotEnvironment.SystemOnly();

            Assert.DoesNotContain("FAB_TEST_SECRET", environment.Keys);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAB_TEST_SECRET", null);
        }
    }

    [Fact]
    public async Task Resolver_ReportsUnresolved_WhenTheInvokerThrowsAnythingElse()
    {
        var resolver = new EvidenceResolver((_, _) => throw new UnauthorizedAccessException("denied"), "/data");

        var result = await resolver.ResolveAsync(EvidenceEntry.Parse("Encounter?class=EMER"));

        Assert.Contains("UnauthorizedAccessException", result.UnresolvedReason);
    }

    [Fact]
    public async Task Resolver_StopsAfterTheMaximumPages_WithAnAccurateReason()
    {
        var resolver = new EvidenceResolver(Pages(total: 100_000, pageLimit: 50, []), "/data");

        var result = await resolver.ResolveAsync(EvidenceEntry.Parse("Encounter?class=EMER"));

        Assert.False(result.IsResolved);
        Assert.Contains("stopped after 100 pages with 5000 of 100000 matches read", result.UnresolvedReason);
    }

    private string FakeExecutable(string name, string recordArgsPath, string stdout)
    {
        var payload = Path.Combine(_directory, name + ".json");
        File.WriteAllText(payload, stdout);
        var path = Path.Combine(_directory, OperatingSystem.IsWindows() ? name + ".cmd" : name + ".sh");
        File.WriteAllText(
            path,
            OperatingSystem.IsWindows()
                ? $"@echo off\r\necho %* > \"{recordArgsPath}\"\r\ntype \"{payload}\"\r\n"
                : $"#!/bin/sh\necho \"$@\" > \"{recordArgsPath}\"\ncat \"{payload}\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private static Question AdaQuestion(params string[] searches) => new()
    {
        Id = "q",
        Tier = 1,
        Template = "t",
        Persona = "nursing",
        Text = "?",
        Patient = new PatientContext("MRN-ADA", "Ada Lovelace", "1960-03-01"),
        Answer = new AnswerSpec { Type = AnswerType.Integer },
        Oracle = new OracleCall("unanswerable", OracleParameters.Empty),
        EvidenceSearches = searches,
        SourcePath = "q.yaml",
    };

    private static Answer Cited(params string[] evidence) => new(AnswerStatus.Answered, null, evidence);

    private static EvidenceResolver Fixed(Dictionary<string, string[]> keysBySearch) =>
        new((arguments, _) =>
        {
            var positional = arguments.SkipWhile(a => a != "--").Skip(1).ToList();
            var type = positional[1];
            var query = positional[2];
            if (!keysBySearch.TryGetValue($"{type}?{query}", out var ids))
            {
                return Task.FromResult(new CliOutput(1, false, """{"facts":null,"issues":[{"severity":"error","code":"unknown-search-parameter","diagnostics":"no"}]}"""));
            }

            var resources = ids.Select(id => new { resourceType = type, id });
            return Task.FromResult(new CliOutput(0, false, JsonSerializer.Serialize(new { facts = new { total = ids.Length, resources }, issues = Array.Empty<object>() })));
        }, "/data");

    [Fact]
    public async Task Analyzer_CountsKindsAndScoresTheResolvedKeys()
    {
        var resolver = Fixed(new() { ["Observation?code=a"] = ["o1", "o2"] });
        var analyzer = new EvidenceAnalyzer(resolver, Context);
        var gold = Cited("Observation/o1", "Observation/o2", "Observation/o3", "Observation/o4");

        var result = await analyzer.AnalyzeAsync(
            AdaQuestion(), gold, Cited("Observation?code=a", "Observation/o3", "Observation/zz", "Observation?bad=1", "nonsense"));

        Assert.Equal(2, result.References);
        Assert.Equal(2, result.Searches);
        Assert.Equal(1, result.Invalid);
        Assert.Equal(1, result.Unresolved);
        Assert.Contains("Observation?bad=1", Assert.Single(result.UnresolvedReasons));
        Assert.Equal(["Observation/o1", "Observation/o2", "Observation/o3", "Observation/zz"], result.ResolvedKeys);
        Assert.Null(result.Recall);
        Assert.Null(result.Precision);
    }

    [Fact]
    public async Task Analyzer_ScoresWhenEverySearchResolved()
    {
        var analyzer = new EvidenceAnalyzer(Fixed(new() { ["Observation?code=a"] = ["o1", "o2"] }), Context);

        var result = await analyzer.AnalyzeAsync(
            AdaQuestion(), Cited("Observation/o1", "Observation/o2", "Observation/o3", "Observation/o4"), Cited("Observation?code=a", "Observation/o3", "Observation/zz"));

        Assert.Equal(0, result.Unresolved);
        Assert.Equal(0.75, result.Recall);
        Assert.Equal(0.75, result.Precision);
    }

    [Fact]
    public async Task Analyzer_AnalysesAtMostTwentyFiveEntries_AndCountsTheRestAsIgnored()
    {
        var analyzer = new EvidenceAnalyzer(null, Context);
        var many = Enumerable.Range(0, 40).Select(i => $"Observation/o{i}").ToArray();

        var result = await analyzer.AnalyzeAsync(AdaQuestion(), Cited("Observation/o0"), Cited(many));

        Assert.Equal(EvidenceAnalyzer.MaxEntries, result.References);
        Assert.Equal(15, result.Ignored);
    }

    [Fact]
    public async Task Analyzer_StopsResolvingWhenTheAnswerBudgetIsUsedUp()
    {
        var slow = new EvidenceResolver(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromMinutes(5), token);
            return new CliOutput(0, false, "");
        }, "/data");
        var analyzer = new EvidenceAnalyzer(slow, Context, TimeSpan.FromMilliseconds(200));

        var result = await analyzer.AnalyzeAsync(AdaQuestion(), Cited("Observation/o1"), Cited("Observation?a=1", "Observation?b=2"));

        Assert.Equal(2, result.Unresolved);
        Assert.All(result.UnresolvedReasons, reason => Assert.Contains("time budget", reason));
        Assert.Null(result.Recall);
    }

    [Fact]
    public async Task Analyzer_RecordsParametersRemovedBeforeResolving()
    {
        var analyzer = new EvidenceAnalyzer(Fixed(new() { ["Observation?code=a"] = ["o1"] }), Context);

        var result = await analyzer.AnalyzeAsync(
            AdaQuestion(), Cited("Observation/o1"), Cited("Observation?code=a&_offset=20&_include=Observation:subject"));

        Assert.Equal(0, result.Unresolved);
        Assert.Contains(result.Notes, note => note.Contains("removed _offset=20"));
        Assert.Contains(result.Notes, note => note.Contains("removed _include=Observation:subject"));
    }

    [Theory]
    [InlineData("code=a&_offset=5", "code=a")]
    [InlineData("code=a&_count=0", "code=a")]
    [InlineData("code=a&_count=5", "code=a&_count=5")]
    [InlineData("_include:iterate=Observation:subject&_revinclude=Provenance:target&code=a", "code=a")]
    public void Sanitize_RemovesOnlyPagingAndIncludeParameters(string query, string expected)
    {
        Assert.Equal(expected, EvidenceResolver.Sanitize(query).Query);
    }

    [Fact]
    public async Task Analyzer_WithoutAResolver_LeavesSearchesUnresolved_NeverGuessing()
    {
        var analyzer = new EvidenceAnalyzer(null, Context);

        var result = await analyzer.AnalyzeAsync(AdaQuestion(), Cited("Observation/o1"), Cited("Observation?code=a"));

        Assert.Equal(1, result.Unresolved);
        Assert.Contains("no evidence resolver", Assert.Single(result.UnresolvedReasons));
        Assert.Empty(result.ResolvedKeys);
        Assert.Null(result.Recall);
        Assert.Null(result.Precision);
    }

    [Fact]
    public async Task Analyzer_MarksAnAcceptedSearch_AfterNormalisingOrderAndCompartment()
    {
        var resolver = Fixed(new() { ["Encounter?class=EMER&date=ge2024"] = ["e1"], ["Encounter?date=ge2024&class=EMER"] = ["e1"] });
        var analyzer = new EvidenceAnalyzer(resolver, Context);
        var question = AdaQuestion("Encounter?class=EMER&date=ge2024");

        var verbatim = await analyzer.AnalyzeAsync(question, Cited("Encounter/e1"), Cited("Encounter?date=ge2024&class=EMER"));
        var compartment = await analyzer.AnalyzeAsync(question, Cited("Encounter/e1"), Cited("Patient/ada/Encounter?class=EMER&date=ge2024"));
        var other = await analyzer.AnalyzeAsync(question, Cited("Encounter/e1"), Cited("Encounter?class=EMER"));

        Assert.Equal(1, verbatim.AcceptedSearches);
        Assert.Equal(1, compartment.AcceptedSearches);
        Assert.Equal(0, other.AcceptedSearches);
    }

    [Fact]
    public async Task Checker_ReportsMatchSupersetAndMiss()
    {
        var resolver = Fixed(new() { ["Observation?code=exact"] = ["o1", "o2"], ["Observation?code=wide"] = ["o1", "o2", "o3"], ["Observation?code=narrow"] = ["o1"] });
        var checker = new SearchChecker(resolver, Context);
        var gold = Cited("Observation/o1", "Observation/o2");

        var match = await checker.CheckAsync(AdaQuestion("Observation?code=exact"), gold);
        var superset = await checker.CheckAsync(AdaQuestion("Observation?code=wide"), gold);
        var miss = await checker.CheckAsync(AdaQuestion("Observation?code=narrow"), gold);
        var unresolved = await checker.CheckAsync(AdaQuestion("Observation?code=unknown"), gold);
        var undeclared = await checker.CheckAsync(AdaQuestion(), gold);

        Assert.Equal("searches-match", match!.Label);
        Assert.Equal("searches-superset", superset!.Label);
        Assert.Equal(["Observation/o3"], superset.Extra);
        Assert.Equal("searches-miss", miss!.Label);
        Assert.Equal(["Observation/o2"], miss.Missing);
        Assert.True(miss.Fails);
        Assert.False(superset.Fails);
        Assert.True(unresolved!.Fails);
        Assert.Null(undeclared);
    }

    [Fact]
    public async Task Checker_UnionsSeveralSearches()
    {
        var resolver = Fixed(new() { ["Observation?code=a"] = ["o1"], ["Observation?code=b"] = ["o2"] });

        var result = await new SearchChecker(resolver, Context).CheckAsync(
            AdaQuestion("Observation?code=a", "Observation?code=b"), Cited("Observation/o1", "Observation/o2"));

        Assert.Equal(SearchCheckOutcome.Match, result!.Outcome);
    }

    [Fact]
    public async Task AdmitCheck_FailsOnlyOnAMiss()
    {
        var resolver = Fixed(new() { ["Observation?code=a"] = ["o-ada-a1c-2"] });
        var checker = new SearchChecker(resolver, Context);
        var oracles = OracleRegistry.CreateDefault();
        var a1c = AdaQuestion("Observation?code=a") with
        {
            Tier = 1,
            Answer = new AnswerSpec { Type = AnswerType.Composite, Fields = new Dictionary<string, AnswerSpec> { ["date"] = new() { Type = AnswerType.Date } } },
            Oracle = new OracleCall("latest-observation", new OracleParameters(new Dictionary<string, object> { ["loinc"] = "4548-4" })),
        };
        var output = new StringWriter();

        var passing = await AdmitCommand.CheckSearchesAsync(checker, oracles, Context, [a1c], output, CancellationToken.None);
        var miss = await AdmitCommand.CheckSearchesAsync(
            new SearchChecker(Fixed(new() { ["Observation?code=a"] = ["other"] }), Context), oracles, Context, [a1c], output, CancellationToken.None);

        Assert.Equal(1, miss);
        Assert.Contains("searches-miss", output.ToString());
        Assert.Equal(0, passing);
    }

    [Fact]
    public void Report_ShowsEvidenceSectionWithDescriptiveStatistics()
    {
        EvidenceDiagnostics Diagnostics(int searches, double? recall, int unresolved, int accepted) =>
            new(1, searches, 0, unresolved, accepted, 0, [], recall, null, [], []);
        var grades = new[]
        {
            new GradeRecord("a", 1, 1, GradeStatus.Wrong, Diagnostics(1, 1.0, 0, 1)),
            new GradeRecord("b", 1, 1, GradeStatus.Correct, Diagnostics(0, 0.5, 0, 0)),
            new GradeRecord("c", 1, 1, GradeStatus.Correct, Diagnostics(2, null, 2, 0)),
            new GradeRecord("d", 1, 1, GradeStatus.FormatInvalid),
        };

        var summary = RunReport.Summarize("r", "p", grades);
        var markdown = RunReport.ToMarkdown(summary);

        Assert.Equal(0.75, summary.Evidence!.MeanRecall);
        Assert.Equal(2 / 3.0, summary.Evidence.SearchShare, precision: 12);
        Assert.Equal(2, summary.Evidence.Unresolved);
        Assert.Equal(1, summary.Evidence.RecallExcludedAttempts);
        Assert.Contains("1 attempts with unresolved searches excluded from recall", markdown);
        Assert.Contains("Mean recall 75.0% over 2 attempts", markdown);
        Assert.Contains("66.7% of 3 attempts cited searches (1 cited an accepted search)", markdown);
        Assert.Contains("2 searches unresolved", markdown);
        Assert.Equal(0.5, summary.Accuracy);
    }

    [Fact]
    public void Report_WithoutDiagnostics_HasNoEvidenceSection()
    {
        var markdown = RunReport.ToMarkdown(RunReport.Summarize("r", "p", [new GradeRecord("a", 1, 1, GradeStatus.Correct)]));

        Assert.DoesNotContain("Evidence", markdown);
    }

    [Fact]
    public void Grades_RoundTripThroughJson()
    {
        var grade = new GradeRecord("a", 1, 1, GradeStatus.Correct, new EvidenceDiagnostics(1, 2, 3, 4, 5, 6, ["Encounter/e1"], 0.5, null, ["x: y"], ["z"]));

        var json = JsonSerializer.Serialize(grade, JsonOptions.Default);
        var back = JsonSerializer.Deserialize<GradeRecord>(json, JsonOptions.Default)!;

        Assert.Equal(grade.Evidence!.ResolvedKeys, back.Evidence!.ResolvedKeys);
        Assert.Equal(0.5, back.Evidence.Recall);
        Assert.Null(back.Evidence.Precision);
    }

    private string WriteQuestion(string searchesYaml)
    {
        var path = Path.Combine(_directory, "q1.yaml");
        File.WriteAllText(path, $"id: q1\ntier: 1\ntemplate: t\npersona: nursing\nquestion: Q?\nanswer: {{ type: integer }}\noracle: {{ name: unanswerable }}\n{searchesYaml}", Encoding.UTF8);
        return path;
    }

    [Fact]
    public void Loader_ReadsEvidenceSearches()
    {
        var question = QuestionLoader.Load(WriteQuestion("evidence_searches: [\"Encounter?class=EMER&date=ge2024-08-17\", \"Patient?_has:Observation:patient:code=x\"]\n"));

        Assert.Equal(2, question.EvidenceSearches.Count);
        Assert.Equal("Encounter?class=EMER&date=ge2024-08-17", question.EvidenceSearches[0]);
    }

    [Fact]
    public void Loader_DefaultsToNoSearches()
    {
        Assert.Empty(QuestionLoader.Load(WriteQuestion("")).EvidenceSearches);
    }

    [Theory]
    [InlineData("Observation/o1")]
    [InlineData("Encounter")]
    [InlineData("Patient/ada/Encounter?class=EMER")]
    [InlineData("class=EMER")]
    public void Loader_RejectsEntriesThatAreNotSearches(string entry)
    {
        var ex = Assert.Throws<InvalidDataException>(() => QuestionLoader.Load(WriteQuestion($"evidence_searches: [\"{entry}\"]\n")));

        Assert.Contains("evidence_searches", ex.Message);
        Assert.Contains(entry, ex.Message);
    }

    [EnvFact]
    public async Task Integration_PassesSpecialCharactersInTheQueryUnchanged()
    {
        var resolver = EvidenceResolver.ForExecutable(
            Environment.GetEnvironmentVariable("FAB_EVIDENCE_RESOLVER")!, Fixture.CorpusDirectory, Path.Combine(_directory, "cache"), Path.Combine(_directory, "scratch"));

        var result = await resolver.ResolveAsync(EvidenceEntry.Parse("Observation?code=http://loinc.org|4548-4&status=final,amended&value-quantity=ge1$&_tag=a\"b%26c"));

        Assert.DoesNotContain("Unrecognized command", result.UnresolvedReason ?? "");
        Assert.DoesNotContain("printed no JSON", result.UnresolvedReason ?? "");
    }

    [EnvFact]
    public async Task Integration_DoesNotExpandAResponseFileNamedInTheQuery()
    {
        var response = Path.Combine(_directory, "args.rsp");
        File.WriteAllText(response, "--summary\ncount\n");
        var resolver = EvidenceResolver.ForExecutable(
            Environment.GetEnvironmentVariable("FAB_EVIDENCE_RESOLVER")!, Fixture.CorpusDirectory, Path.Combine(_directory, "cache"), Path.Combine(_directory, "scratch"));

        var result = await resolver.ResolveAsync(EvidenceEntry.Parse("Observation?_tag=@" + response.Replace('\\', '/')));

        Assert.DoesNotContain("Unrecognized command", result.UnresolvedReason ?? "");
        Assert.DoesNotContain("printed no JSON", result.UnresolvedReason ?? "");
    }

    [EnvFact]
    public async Task Integration_ResolvesASearchAgainstTheRealCorpus()
    {
        var resolverPath = Environment.GetEnvironmentVariable("FAB_EVIDENCE_RESOLVER")!;
        var resolver = EvidenceResolver.ForExecutable(
            resolverPath, Fixture.CorpusDirectory, Path.Combine(_directory, "cache"), Path.Combine(_directory, "scratch"));

        var result = await resolver.ResolveAsync(EvidenceEntry.Parse("Observation?code=http://loinc.org|4548-4"), "Patient/ada");

        Assert.True(result.IsResolved, result.UnresolvedReason);
        Assert.Contains("Observation/o-ada-a1c-2", result.Keys);
    }
}

internal sealed class EnvFactAttribute : FactAttribute
{
    public EnvFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FAB_EVIDENCE_RESOLVER")))
        {
            Skip = "Set FAB_EVIDENCE_RESOLVER to a cli-on-fhir executable to run this test.";
        }
    }
}
