using System.CommandLine;
using FhirAgentBench.Answers;
using FhirAgentBench.Authoring;
using FhirAgentBench.Corpus;
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
        var command = new Command("admit", "Recompute gold for a question set and check it against the answer spec and gold lock.")
        {
            corpusDir, set, questionsDir, updateLock, show,
        };
        command.SetAction(parse =>
        {
            var corpus = CorpusDefinition.Load(parse.GetValue(corpusDir)!);
            var questions = QuestionLoader.LoadSet(parse.GetValue(questionsDir)!, parse.GetValue(set)!);
            var lockPath = Path.Combine(parse.GetValue(questionsDir)!, GoldLock.FileName);
            var admitter = new GoldAdmitter(OracleRegistry.CreateDefault(), new OracleContext(CorpusIndex.Load(corpus.DataDirectory), corpus.Now, corpus.MrnSystem));
            return Execute(admitter, questions, lockPath, parse.GetValue(updateLock), parse.GetValue(show), Console.Out, Console.Error);
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

    private static string Short(string hash) => hash.Length <= 12 ? hash : hash[..12];
}
