using System.CommandLine;
using FhirAgentBench.Answers;
using FhirAgentBench.Authoring;
using FhirAgentBench.Corpus;
using FhirAgentBench.Evidence;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Cli;

internal static class AdmitCommand
{
    public static Command Create()
    {
        var corpusDir = new Option<string>("--corpus") { Description = "Folder containing corpus.yaml.", Required = true };
        var set = new Option<string>("--set") { Description = "Question-set file listing question ids.", Required = true };
        var questionsDir = new Option<string>("--questions-dir") { Description = "Folder of question YAML files.", DefaultValueFactory = _ => "questions" };
        var updateLock = new Option<bool>("--update-lock") { Description = "Accept drifted gold and lock new questions in questions/gold.lock.json. Writes nothing unless every question is admitted." };
        var show = new Option<bool>("--show") { Description = "Print each computed gold answer (authoring only; never paste gold into a run)." };
        var checkSearches = new Option<bool>("--check-searches") { Description = "Also resolve each question's evidence_searches with the evidence resolver and compare them to the oracle's evidence. A miss fails admit; gold and the lock are never changed." };
        var evidenceResolver = EvidenceResolverSetup.CreateOption();
        var command = new Command("admit", "Recompute gold for a question set and check it against the answer spec and gold lock.")
        {
            corpusDir, set, questionsDir, updateLock, show, checkSearches, evidenceResolver,
        };
        command.SetAction(async (parse, cancellationToken) =>
        {
            var corpus = CorpusDefinition.Load(parse.GetValue(corpusDir)!);
            var questions = QuestionLoader.LoadSet(parse.GetValue(questionsDir)!, parse.GetValue(set)!);
            var lockPath = Path.Combine(parse.GetValue(questionsDir)!, GoldLock.FileName);
            var context = new OracleContext(CorpusIndex.Load(corpus.DataDirectory), corpus.Now, corpus.MrnSystem);
            var oracles = OracleRegistry.CreateDefault();
            var admitter = new GoldAdmitter(oracles, context);
            var exit = Execute(admitter, questions, lockPath, parse.GetValue(updateLock), parse.GetValue(show), Console.Out, Console.Error);
            if (!parse.GetValue(checkSearches))
            {
                return exit;
            }

            var resolverPath = EvidenceResolverSetup.ConfiguredPath(parse.GetValue(evidenceResolver));
            if (resolverPath is null)
            {
                Console.Error.WriteLine($"--check-searches needs a resolver: pass --evidence-resolver or set {EvidenceResolverSetup.PathVariable}.");
                return 2;
            }

            var work = Directory.CreateTempSubdirectory("fab-check-searches-").FullName;
            try
            {
                var resolver = EvidenceResolverSetup.Create(resolverPath, corpus.DataDirectory, work)!;
                var searchExit = await CheckSearchesAsync(new SearchChecker(resolver, context), oracles, context, questions, Console.Out, cancellationToken);
                return exit != 0 ? exit : searchExit;
            }
            finally
            {
                EvidenceResolverSetup.TryDelete(work);
            }
        });
        return command;
    }

    internal static int Execute(
        GoldAdmitter admitter, IReadOnlyList<Question> questions, string lockPath, bool updateLock, bool show, TextWriter output, TextWriter error)
    {
        var locked = GoldLock.Load(lockPath);
        var hasLock = locked.Count > 0;
        var results = new List<AdmissionResult>();
        var failed = 0;
        var needsReview = false;

        foreach (var question in questions)
        {
            var isNew = hasLock && !locked.ContainsKey(question.Id);
            var result = admitter.Admit(question, updateLock ? null : locked.GetValueOrDefault(question.Id));
            results.Add(result);

            var problems = result.Problems.ToList();
            var drifted = problems.Any(p => p.StartsWith("gold drift", StringComparison.Ordinal));
            var isUnlocked = isNew && !updateLock;
            if (isUnlocked)
            {
                problems.Add("new question is not in the gold lock");
            }

            needsReview |= drifted || isUnlocked;
            if (problems.Count > 0)
            {
                failed++;
                output.WriteLine($"{(result.IsAdmitted && isUnlocked ? "NEW " : "FAIL")} {question.Id}");
                foreach (var problem in problems)
                {
                    error.WriteLine($"{question.Id}: {problem}");
                }
            }
            else
            {
                output.WriteLine($"OK   {question.Id}");
            }

            if (show)
            {
                output.WriteLine($"     {AnswerJson.Serialize(result.Gold)}");
            }
        }

        if (needsReview)
        {
            error.WriteLine("Review the gold, then re-run with --update-lock to accept drift and lock new questions.");
        }

        if (updateLock)
        {
            if (failed > 0)
            {
                error.WriteLine($"{failed} question(s) failed admission; {lockPath} was not changed.");
            }
            else
            {
                foreach (var result in results.Where(r => r.GoldHash.Length > 0))
                {
                    if (locked.TryGetValue(result.QuestionId, out var previous))
                    {
                        if (previous != result.GoldHash)
                        {
                            output.WriteLine($"UPDATED {result.QuestionId}: {Short(previous)} -> {Short(result.GoldHash)}");
                        }
                    }
                    else
                    {
                        output.WriteLine($"NEW {result.QuestionId}: {Short(result.GoldHash)}");
                    }

                    locked[result.QuestionId] = result.GoldHash;
                }

                GoldLock.Save(lockPath, locked);
                output.WriteLine($"Updated {lockPath}.");
            }
        }
        else if (!hasLock)
        {
            error.WriteLine($"Gold lock '{lockPath}' is empty or missing, so nothing is checked for drift. Run with --update-lock to bootstrap it.");
            return 1;
        }

        return failed == 0 ? 0 : 1;
    }

    internal static async Task<int> CheckSearchesAsync(
        SearchChecker checker, OracleRegistry oracles, OracleContext context, IReadOnlyList<Question> questions, TextWriter output,
        CancellationToken cancellationToken)
    {
        var failed = 0;
        foreach (var question in questions.Where(q => q.EvidenceSearches.Count > 0))
        {
            var gold = oracles.Compute(context, question);
            var result = (await checker.CheckAsync(question, gold, cancellationToken))!;
            output.WriteLine($"{(result.Fails ? "FAIL" : "OK  ")} {question.Id}: {result.Label}{Detail(result)}");
            failed += result.Fails ? 1 : 0;
        }

        return failed == 0 ? 0 : 1;
    }

    private static string Detail(SearchCheckResult result) => result switch
    {
        { Reason: { } reason } => $" ({reason})",
        { Missing.Count: > 0 } => $" (missing {result.Missing.Count}: {Sample(result.Missing)}; extra {result.Extra.Count})",
        { Extra.Count: > 0 } => $" (extra {result.Extra.Count}: {Sample(result.Extra)})",
        _ => "",
    };

    private static string Sample(IReadOnlyList<string> keys) =>
        string.Join(", ", keys.Take(5)) + (keys.Count > 5 ? ", ..." : "");

    private static string Short(string hash) => hash.Length <= 12 ? hash : hash[..12];
}
