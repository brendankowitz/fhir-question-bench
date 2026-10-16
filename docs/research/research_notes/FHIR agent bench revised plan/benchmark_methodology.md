# Benchmark Methodology for Comparing Agent Profiles (150-question FHIR bench, deterministic grading)

Context assumed: a fixed set of 150 questions, deterministic grader, multiple agent "profiles" (tools/MCP servers, skills, prompts, models), k repeated runs per question per profile. All profiles answer the same questions, so every comparison is **paired**.

Notation used below (from Miller 2024): n = number of questions, K = repeats per question, s_i = mean score of question i over its K repeats, sigma_i^2 = within-question (run-to-run) variance, Var(x) = between-question variance of the true per-question success probability, omega^2 = Var(x_A) + Var(x_B) - 2Cov(x_A, x_B) = variance of the per-question true difference between profiles A and B. z_{alpha/2}+z_beta = 1.96+0.84 = 2.80 for alpha=0.05 two-sided and 80% power, so (z_{alpha/2}+z_beta)^2 = 7.85.

## Statistical power: what differences can 150 questions x k repeats detect?

### Takeaway
With 150 questions, a single run per profile can only reliably detect differences of about **10-14 percentage points** (pp). Running K=3-5 repeats and analyzing paired per-question differences brings the minimum detectable effect (MDE) down to about **7-10pp**. A **5pp** difference is out of reach at n=150 unless the two profiles are highly correlated and K is large (K=10 or more), and even then it is marginal. Repeats only shrink the within-question noise term. Once K is about 5, more questions give more power than more repeats.

### Cited Findings
- Standard error of the mean via the CLT: SE = sqrt( (1/(n-1)) * sum_i (s_i - s_bar)^2 / n ). For Bernoulli scores this is SE = sqrt( p(1-p)/n ), and CI95 = s_bar +/- 1.96*SE. The paper recommends the analytic CLT SE over the bootstrap unless the sampling scheme or estimator is complex. — [Miller 2024, Adding Error Bars to Evals](https://arxiv.org/html/2411.00640v1)
- Variance decomposition: Var(mu_hat) = (Var(x) + E[sigma_i^2]) / n. Averaging K resamples per question gives Var(mu_hat) = (Var(x) + E[sigma_i^2]/K) / n. Resampling therefore shrinks **only** the within-question term. For uniformly distributed question difficulty, K=2 cuts variance by 1/3, K=4 by 1/2 and K=6 by 5/9, and the limit as K grows is 2/3. — [Miller 2024](https://arxiv.org/html/2411.00640v1)
- Paired difference: SE_{A-B,paired} = sqrt( Var(s_A - s_B) / n ) = sqrt( SE_A^2 + SE_B^2 - 2*SE_A*SE_B*Corr(s_A, s_B) ). With correlation 0.5, pairing cuts estimator variance by 1/3 (from 1/6 to 1/9). Miller recommends always computing question-level paired differences and **reporting the correlation between models**. — [Miller 2024](https://arxiv.org/html/2411.00640v1)
- Clustered SE: SE_clustered = ( SE_CLT^2 + (1/n^2) * sum_c sum_i sum_{j!=i} (s_ic - s_bar)(s_jc - s_bar) )^{1/2}. The paired-and-clustered version is SE = (1/n) * ( sum_c sum_i sum_j (d_ic - d_bar)(d_jc - d_bar) )^{1/2}. Clustered SEs came out up to about 3x the naive SEs (DROP: 1.34 vs 0.44). Use them when questions share a source, such as several questions about the same patient or the same bundle. — [Miller 2024](https://arxiv.org/html/2411.00640v1)
- Sample size: n = (z_{alpha/2}+z_beta)^2 * (omega^2 + sigma_A^2/K_A + sigma_B^2/K_B) / delta^2. Minimum detectable effect: delta = (z_{alpha/2}+z_beta) * sqrt( (omega^2 + sigma_A^2/K_A + sigma_B^2/K_B) / n ). In Miller's worked examples, detecting 3pp with omega^2=1/9 needs about 969 questions. With n=198, sigma^2=1/6 and Corr=0.5, raising K from 1 to 10 cuts the MDE from 13.2% to 7.5%. — [Miller 2024](https://arxiv.org/html/2411.00640v1)
- Further recommendations from Miller: do not lower temperature to reduce variance (unless that is the deployed setting), run a power analysis before running the eval, and report n (and the number of clusters) with SEs in parentheses under each score. — [Miller 2024](https://arxiv.org/html/2411.00640v1)
- McNemar sample size (Connor 1987, Biometrics 43(1):207-211) for paired binary outcomes: n_pairs = ( z_{alpha/2}*sqrt(p_d) + z_beta*sqrt(p_d - delta^2) )^2 / delta^2, where p_d is the discordant proportion (questions where exactly one profile is right) and delta = p_10 - p_01 is the accuracy difference. SAS treats Connor as an approximation, and its default is an exact binomial method. Power should be planned conservatively on the plausible p_d. — [MetricGate McNemar docs](https://metricgate.com/docs/sample-size-mcnemar-paired); [SAS PROC POWER](https://support.sas.com/documentation/cdl/en/statug/65328/HTML/default/statug_power_details55.htm)
- Kapoor et al. found that many reported agent accuracies exceeded the maximum of their own five reproduction runs, and that cost discourages repeated runs and error bars (a single SWE-Agent run on SWE-bench could cost more than $8,000). This is evidence that single-run leaderboard numbers are routinely within noise. — [AI Agents That Matter](https://arxiv.org/html/2407.01502)

### Inferences (worked numbers, computed by me from the formulas above; alpha=0.05 two-sided, power 0.80)
**A. Single run, McNemar / Connor, n=150 pairs.** MDE depends on the discordance rate p_d:

| p_d (share of questions where the profiles disagree) | MDE at n=150 |
|---|---|
| 0.10 | 7.2pp |
| 0.15 | 8.8pp |
| 0.20 | 10.2pp |
| 0.30 | 12.4pp |
| 0.40 | 14.4pp |

Pairs needed to detect the target difference:

| p_d | Pairs for 10pp | Pairs for 5pp |
|---|---|---|
| 0.20 | 155 | 626 |
| 0.30 | 234 | 940 |
| 0.10 | (not computed) | 312 |

So with 150 questions and one run, **10pp is detectable only if the profiles disagree on 20% of questions or fewer. 5pp is not detectable.**

**B. Repeated runs, Miller formula.** Illustrative model: overall accuracy about 0.6, total Bernoulli variance about 0.24, split evenly between Var(x)=0.12 (between-question difficulty) and E[sigma_i^2]=0.12 (run-to-run noise). rho is the correlation of true per-question success between the two profiles, so omega^2 = 2*0.12*(1-rho).

| rho (profile similarity) | K | MDE at n=150 | n for 10pp | n for 5pp |
|---|---|---|---|---|
| 0.5 | 1 | 13.7pp | 283 | 1131 |
| 0.5 | 3 | 10.2pp | 157 | 628 |
| 0.5 | 5 | 9.4pp | 132 | 528 |
| 0.5 | 10 | 8.7pp | 114 | 453 |
| 0.5 | infinite | 7.9pp | 95 | 377 |
| 0.8 | 1 | 12.3pp | 227 | 905 |
| 0.8 | 3 | 8.2pp | 101 | 402 |
| 0.8 | 5 | 7.1pp | 76 | 302 |
| 0.8 | 10 | 6.1pp | 57 | 227 |
| 0.8 | infinite | 5.0pp | 38 | 151 |

What the table shows:
- Going from K=1 to K=3 gives the biggest gain (about a 25-33% reduction in MDE). K=5 adds a little more, and K above 5 has strongly diminishing returns, because the between-question term omega^2 does not shrink with K. The rho=0.8 rows suit profiles that share a model and differ only in tools or prompt; rho=0.5 suits profiles that use different models.
- **Recommendation: K=3 as the default, K=5 for headline comparisons.** To resolve 5pp, add questions (roughly 300-600) rather than repeats.
- **Per-tier breakdowns are underpowered.** With 3 tiers of 50 questions, SEs are sqrt(3)≈1.7x larger, so the per-tier MDE is about 12-24pp. Report per-tier numbers descriptively with CIs, and avoid declaring per-tier wins without a pre-registered test.
- **Single-score SE at n=150:** 4.1pp at p=0.5, 3.7pp at p=0.7 and 2.4pp at p=0.9, so a 95% CI is about ±8pp at mid-range accuracy.
- **Multiple comparisons:** comparing many profiles inflates false positives. Pre-register the primary comparison (e.g. each profile vs baseline) and apply Holm-Bonferroni to the rest. This is standard practice rather than a sourced finding.
- **Implementation:**
  - Compute per-question mean over K for each profile.
  - Take the per-question difference d_i.
  - Report mean(d) ± 1.96*SD(d)/sqrt(n). Use the clustered version if questions share fixtures.
  - As a robustness check, use a paired bootstrap that resamples questions, not runs, with 10,000 resamples.
  - For K=1, use exact McNemar on the discordant counts.
- **Estimate p_d, rho and sigma_i^2 from a pilot** (e.g. 2 profiles x K=3), then rerun the power calculation. The numbers above are illustrative and depend on these values.

### Gaps
- I found no published empirical values of the within-question vs between-question variance split for tool-using agents on QA tasks. The 50/50 split above is an assumption, so measure it in the pilot.
- I could not retrieve the Inspect docs section on epoch reducers or clustered stderr (the pages I fetched point to `metrics.html`). Inspect reportedly supports epochs with reducers such as mean, mode, pass_at_k and at_least_k, plus clustered stderr, but I did not verify this.

## pass@k vs pass^k, consistency, cost-per-success, per-tier reporting

### Takeaway
Report three numbers from the same K runs:
- **pass@1**: mean accuracy over runs (the primary metric).
- **pass^k**: reliability, i.e. all k runs correct.
- **pass@k**: capability ceiling, i.e. any run correct.

Alongside them, report a **flip rate** and **cost/tokens per success**, and plot an **accuracy vs cost Pareto frontier**. For a QA bench that a user relies on, pass^k matters more than pass@k.

### Cited Findings
- tau-bench definitions, with n trials per task and c successes:
  - pass^k = E_task[ C(c,k) / C(n,k) ], the probability that all k i.i.d. trials succeed.
  - pass@k = 1 - E_task[ C(n-c,k) / C(n,k) ].
  - pass^1 = pass@1 = E[c/n].
  - pass@k suits settings where one success is enough (code with unit tests). pass^k measures reliability, which suits customer-facing agents.
  - The main results use at least 3 trials per task.
  — [tau-bench, Yao et al. 2024](https://arxiv.org/html/2406.12045)
- In tau-bench, gpt-4o function-calling scores pass^1 = 61.2% in retail and 35.2% in airline, and **pass^8 falls below 25% in retail**. — [tau-bench](https://arxiv.org/html/2406.12045)
- tau-bench grades by comparing the final database state with an annotated goal state, not the conversation text. — [tau-bench abstract](https://arxiv.org/abs/2406.12045)
- Anthropic's agent-eval guidance:
  - At a 75% per-trial rate, pass^3 ≈ 0.75^3 ≈ 42%.
  - As k grows, pass@k approaches 100% while pass^k approaches 0%.
  - Use pass^k for agents where consistency matters.
  - Grade the outcome, not the exact path, and use partial credit for multi-part tasks.
  - Start each trial from a clean environment, because shared state causes correlated failures.
  — [Anthropic, Demystifying evals for AI agents](https://www.anthropic.com/engineering/demystifying-evals-for-ai-agents)
- Cost-aware evaluation:
  - Simple baselines match complex agents on HumanEval at a fraction of the cost. Warming (GPT-4) scored 93.2% for $2.45, while LATS (GPT-4) scored 88.0% for $134.50, about 55x the cost for lower accuracy.
  - Costs at similar accuracy differ by nearly two orders of magnitude.
  - Recommendations: plot accuracy vs cost on a Pareto frontier. For downstream (application) evaluation, report **actual dollar cost and token counts**, so costs can be recomputed when prices change, rather than proxies like parameter count.
  - Retries can raise accuracy "without any scientifically meaningful" design gain.
  — [Kapoor et al., AI Agents That Matter](https://arxiv.org/html/2407.01502)
- On NovelQA, RAG and long-context approaches had roughly equal accuracy, but RAG cost more than 20x less. Cost differences between configurations can dominate accuracy differences. — [AI Agents That Matter](https://arxiv.org/html/2407.01502)

### Inferences
- **Metrics to compute per profile, from the same K runs:**

| Metric | Definition |
|---|---|
| pass@1 | Mean over questions of c/K |
| pass^K | Mean of C(c,K)/C(K,K), i.e. the share of questions answered correctly in all K runs |
| pass@K | Share of questions answered correctly in at least one run |
| Flip rate | Share of questions with 0 < c < K (inconsistent) |
| Cost per success | Total $ (or tokens) / total correct answers |
| Efficiency | Median and p90 of tool calls, turns, wall-clock and tokens per question |

- **Illustrative pass^k decay** (my arithmetic, for a homogeneous per-question p):

| Per-question p | pass^3 | pass^5 |
|---|---|---|
| 0.9 | 0.73 | 0.59 |
| 0.6 | (not computed) | 0.08 |

  Real pass^k is higher than this, because per-question p varies and many questions sit at p≈1.
- **Tool and MCP profiles may change consistency more than accuracy.** For example, a FHIR search tool can make answers deterministic. Report pass^k and flip rate per profile, because this is often where tool or skill profiles differ.
- **Per-tier tables:** show n per tier, pass@1 with 95% CI, pass^K and cost per success. Also include a question-by-profile heatmap (questions as rows, profiles as columns, cell = c/K) for diagnosis.
- **Reproducibility:** pin model versions and MCP server versions, and log every trajectory.

### Gaps
- I found no source giving a standard definition of "flip rate" for agents. The definition above (share of questions with 0 < c < K) is my proposal.

## Benchmark quality checklists (ABC, SWE-bench Verified, tau-bench, Kapoor)

### Takeaway
The main validity risks for this bench are:
- **Task validity:** questions that are ambiguous or unsolvable from the corpus, environment leakage, and stale state.
- **Outcome validity:** graders that accept wrong answers (substring matching, "list everything" answers, empty or "N/A" answers) or reject correct ones (equivalent forms).
- **Shortcuts:** a trivial agent scoring well.

Run an oracle solver and a trivial or null agent as baselines, have every question independently validated, and inspect pilot outliers.

### Cited Findings
- Agentic Benchmark Checklist (ABC), Zhu, Jin, Kapoor, Longpre, Liang, Kang, Steinhardt et al., July 2025. Benchmark flaws can cause **up to 100% relative** under- or over-estimation of agent performance. Applying ABC to CVE-Bench reduced overestimation by 33%. — [ABC, arXiv 2507.02825](https://arxiv.org/abs/2507.02825)
- ABC task-validity items:
  - T.1: state tool and package versions.
  - T.2: keep API tools available and manage rate limits.
  - T.3: detect API interruptions.
  - T.4: clear legacy data and state between tasks.
  - T.5: fully isolate agents from ground truth.
  - T.6: freeze the environment so it is reproducible.
  - T.7: verify ground-truth annotations.
  - T.8: verify each task is solvable.
  - T.9: provide an automatic **oracle solver**.
  - T.10: **inspect outliers in pilot runs.** Consistent failure on easy tasks suggests impossible tasks, and success only on hard tasks suggests shortcuts.
  — [ABC](https://arxiv.org/html/2507.02825)
- ABC outcome-validity items:
  - String match: account for semantically equivalent expressions and for redundant words.
  - Substring match: handle negation ("not"), prevent success by listing every possible answer, and prevent success by guessing.
  - LLM-as-judge: pilot-test the judge's accuracy and self-consistency.
  - State matching: include all valid end states, check irrelevant state too, and make trivial changes insufficient to pass.
  - Answer matching: **explicitly specify parser and format assumptions** (O.h.1), and design tasks so answers can't be guessed (O.h.2).
  — [ABC](https://arxiv.org/html/2507.02825)
- ABC reporting items:
  - Open-source the data and harness.
  - Prevent contamination and plan updates.
  - State the capabilities and construct validity.
  - Document limitations quantitatively.
  - **Report statistical significance** (R.10).
  - Compare against baselines, including **trivial agents** and human experts (R.12-13).
  — [ABC](https://arxiv.org/html/2507.02825)
- Concrete flaws ABC found in other benchmarks:
  - tau-bench: 38% of airline and 6% of retail tasks are unsolvable, yet "do nothing" passes them, so a trivial agent scores 38% and beats a GPT-4o agent. Substring matching on verbatim DB text means dumping the database passes, and listing all answers inflates scores by 40%.
  - WebArena: substring matching accepts extraneous content (+5.2% overestimate), and its LLM judge accepts empty replies on "N/A" tasks (+1.4-5.2%).
  - KernelBench: correctness overestimated by about 31pp because the fuzzer varies only values, not shapes.
  - OSWorld: 13 of 46 Chrome tasks are broken by website drift, underestimating one agent by 28pp.
  - SWE-Lancer: agents could overwrite the test files.
  — [ABC](https://arxiv.org/html/2507.02825)
- SWE-bench Verified:
  - OpenAI worked with 93 Python developers who screened 1,699 random SWE-bench samples. Each sample was labeled 3 times, with severity rated 0-3 for an underspecified issue and for tests that reject valid solutions. Labels 2-3 meant discard, and 500 samples were kept.
  - Secondary sources report about 68% filtered out. 1699→500 implies about 70.6%, a discrepancy I could not resolve because the OpenAI page returned 403.
  - A later audit (TDD-Bench Verified) kept 449 of the 500 after further filtering, showing that some flawed items survived triple annotation.
  — [OpenAI SWE-bench Verified (via search summary)](https://openai.com/index/introducing-swe-bench-verified/); [TDD-Bench Verified](https://arxiv.org/pdf/2412.02883)
- ABC reports that 24% of SWE-bench Verified's top-50 leaderboard positions are incorrect because weak unit tests let wrong patches pass. — [ABC](https://arxiv.org/html/2507.02825)
- Anthropic's task-quality guidance:
  - A good task is one where **two domain experts independently reach the same pass/fail verdict**.
  - Build a **reference solution** that passes all graders.
  - **A 0% pass rate across many trials usually signals a broken task.**
  - Read transcripts so you know whether a failure is the agent's fault or the grader's.
  - Build balanced sets that cover both "should" and "should not" cases.
  — [Anthropic, Demystifying evals](https://www.anthropic.com/engineering/demystifying-evals-for-ai-agents)
- Kapoor et al. on shortcuts: the WebArena STeP agent reached 35.8% largely by hard-coding policies for specific tasks. Holdout sets were appropriate for only a minority of the 17 benchmarks they surveyed, and benchmarks should have holdouts matched to the generality being claimed. They also found harness bugs that marked incorrect tasks as correct. — [AI Agents That Matter](https://arxiv.org/html/2407.01502)
- GAIA design principles:
  - Each question has one unambiguous, concise factoid answer.
  - Each question was checked by two additional annotators, and items they disagreed on were repaired or removed.
  - Answers do not appear in plain text on the web, and multiple choice was avoided.
  — [GAIA, arXiv 2311.12983](https://arxiv.org/html/2311.12983)

### Inferences (checklist to adopt for the FHIR bench)
1. **Oracle solver:** for each question, compute the answer programmatically from the FHIR corpus (e.g. a query script) and assert that it equals the gold answer. This doubles as the generator for new corpus seeds.
2. **Independent validation:** a second person, or a second derivation method, re-derives each answer. Discard or repair items where they disagree.
3. **Trivial baselines:** run a no-tools agent, an "answer empty / unknown" agent and a "dump everything" agent. Each should score about 0%, or the floor you expect from chance or general knowledge.
4. **Pilot outlier review (ABC T.10):** flag questions that every profile fails (likely broken) and questions that only weak profiles pass (likely shortcut or grader bug). Read their transcripts.
5. **Isolation:** reset FHIR server state per run, keep gold answers out of the agent's filesystem and tools, and pin server and tool versions.
6. **Harness fault detection:** detect API errors and timeouts and record them as infrastructure failures, not agent failures. Re-run them, or report them separately.

### Gaps
- I could not fetch the OpenAI SWE-bench Verified post directly (HTTP 403). Its details come from search-result summaries, and the 68.3% vs 70.6% filtering figure is unresolved.

## Deterministic grading of free-form answers

### Takeaway
Require a **structured final answer**, such as a JSON object validated against a per-question schema that declares the answer type (number, string, list or set, date, boolean, reference/ID, or "insufficient data"). Grade it with type-specific normalizers and tolerances:
- Set equality for lists.
- Absolute or relative tolerance for numbers.
- Canonical forms for codes and dates.
- An explicit abstention option.

Use LLM judges only where the answer cannot be canonicalized, and only after validating the judge against human labels.

### Cited Findings
- GAIA uses **quasi exact match** with type-specific normalization. Answers are a number, a few words, or a comma-separated list. The format rules are: numbers without commas, units or percent signs unless asked for; strings without articles or abbreviations; list elements normalized according to their type. — [GAIA](https://arxiv.org/html/2311.12983)
- ABC O.h.1: explicitly specify parser and format assumptions. O.a.1/O.a.2: handle semantically equivalent expressions and redundant words. O.b.2/O.b.3: prevent success by listing all candidates or by guessing. — [ABC](https://arxiv.org/html/2507.02825)
- Anthropic: prefer deterministic graders where possible, use LLM graders where necessary, and use humans to calibrate them. LLM judges need calibration against humans, need an "Unknown" escape option, and should use a separate judge per dimension. — [Anthropic, Demystifying evals](https://www.anthropic.com/engineering/demystifying-evals-for-ai-agents)
- ABC O.c.1: pilot-test any LLM judge's accuracy and self-consistency. WebArena's unvalidated judge accepted empty answers on N/A tasks. — [ABC](https://arxiv.org/html/2507.02825)
- tau-bench counted empty responses as successes on unsolvable tasks, which is the failure mode to avoid when scoring abstention. — [ABC](https://arxiv.org/abs/2507.02825)

### Inferences (grading rules to adopt)
- **Answer envelope:** `{"answer": <typed value>, "status": "answered" | "insufficient_data"}`, plus optional `evidence` (resource IDs), which is logged but not graded.
- **Type-specific rules:**

| Answer type | Rule |
|---|---|
| Integers and counts | Exact match |
| Floats (lab values, ages) | Declared tolerance per question, e.g. abs ≤ 0.01 or rel ≤ 0.5%, after unit normalization (UCUM) |
| Dates | Normalize to ISO 8601 at the question's declared precision |
| Codes | Compare system+code tuples, not display strings |
| Resource references | Normalize `Patient/123` vs `123` |
| Sets | Set equality after normalization. Also log precision and recall for diagnosis, but score with strict equality, which blocks "list everything" gaming (ABC O.b.2) |
| Ordered lists | Sequence equality only where the question asks for an order |
| Booleans and enums | Exact match against the allowed values |

- **Abstention:**
  - Include some questions that are genuinely unanswerable from the data, where the gold answer is `insufficient_data`.
  - Score abstention as correct only on those questions. On answerable questions, abstention counts as incorrect, but report it separately from wrong answers ("abstain rate" vs "error rate").
  - Make sure "always abstain" scores no higher than the unanswerable fraction, ideally 5-10% of questions.
- **Grader unit tests:**
  - For each question, store the gold answer plus a few known-equivalent variants that must pass and a few near-misses that must fail. Run them in CI.
  - Also run the grader over the oracle's output (should score 100%) and over the trivial agents' output (should score about 0%).
- **LLM judge (only if needed):**
  - Acceptable for free-text explanation quality or clinical-narrative questions.
  - Requirements: pin the judge model, use temperature 0 or majority-of-3, give the judge an "unknown" option, and measure agreement with about 50-100 human-labeled items (report Cohen's kappa).
  - Never use the same model family as the profiles under test without noting it. This is a general best practice, not from a specific source above.

### Gaps
- I found no FHIR-specific grading standard. UCUM unit normalization and code-system equivalence are my domain-informed recommendations, not sourced.

## Contamination: held-out splits, regenerated corpora, canaries

### Takeaway
Keep a **private held-out split**, as GAIA does with private answers for 300 of its 466 questions. Generate the corpus synthetically so questions and answers can be **regenerated with new seeds**. Add a canary string to published files. Canaries only detect contamination after the fact, so regeneration is the stronger defense. Also guard against **dev-set overfitting** by profile authors (skill and prompt tuning on the test set), which is a shortcut risk at least as large as pretraining contamination.

### Cited Findings
- GAIA released 466 questions, kept the answers to 300 private for the leaderboard, and published a 166-question annotated dev set. — [GAIA](https://arxiv.org/html/2311.12983)
- ABC R.3-R.4: prevent data contamination and plan for benchmark updates. — [ABC](https://arxiv.org/html/2507.02825)
- Kapoor et al. define holdout requirements by the level of generality being claimed. Agents tuned on the test distribution, like STeP's hard-coded WebArena policies, overstate generality, and 7 of the 17 surveyed benchmarks had no holdout and no plans for one. — [AI Agents That Matter](https://arxiv.org/html/2407.01502)
- The BIG-bench canary GUID was meant to let data builders filter benchmark content out of training corpora, and the BIG-bench paper asks for it in all documents about the benchmark. A non-public pre-RLHF GPT-4 base model reportedly reproduced it, which shows canaries do not prevent ingestion. They are detection-only and can be ignored or stripped. — [BIG-Bench canary contamination in GPT-4 (LessWrong/AF)](https://alignmentforum.org/posts/kSmHMoaLKGcGgyWzs/big-bench-canary-contamination-in-gpt-4); [Pretraining on the Test Set Is All You Need](https://arxiv.org/pdf/2309.08632)

### Inferences
- **Splits:**
  - Dev: about 30-50 questions, public, used to tune profiles, skills and prompts.
  - Test: the 150 questions, run only for reported comparisons.
  - Private holdout: questions generated from a fresh corpus seed and never published. Rotate it periodically.
  - If test scores drift well above holdout scores for a profile, treat that as evidence that the profile is overfitting to the test set.
- **Seeded regeneration:** if the corpus is synthetic (e.g. Synthea-like patients), the oracle solver re-derives every gold answer for any seed. Questions become templates and the answers come from the seed. Report results on at least 2 seeds to show robustness.
- Put a unique canary GUID in published question and answer files and in the README, and ask in the README that it not be used for training.

### Gaps
- I did not find a primary source quantifying how much regenerated-seed evaluation changes agent scores. This would be worth measuring in the pilot.

## Difficulty calibration: tiers that create spread

### Takeaway
Design tiers by **mechanism**, meaning the number of steps, resources and joins. GAIA's tiers are an example: Level 1 needs at most 5 steps and at most 1 tool, Level 2 needs about 5-10 steps and several tools, and Level 3 has arbitrarily long chains. Then **calibrate empirically with pilot runs**. Aim for the expected profile range to sit roughly in the 20-80% band overall and avoid tiers at 0% or 100%, because items everyone passes or everyone fails carry no comparative signal. Drop or repair noisy items.

### Cited Findings
- GAIA difficulty levels are loose guidelines: Level 1 uses no tools or one tool and at most 5 steps, Level 2 uses about 5-10 steps across different tools, and Level 3 has arbitrarily long action sequences. — [GAIA](https://arxiv.org/html/2311.12983)
- Anthropic distinguishes **capability evals**, which start at a low pass rate, from **regression evals**, which should sit near 100%. Watch for **saturation**, meaning all solvable tasks pass and the eval stops showing improvement, and add harder tasks when it happens. A 0% pass rate across many trials usually means the task is broken. — [Anthropic, Demystifying evals](https://www.anthropic.com/engineering/demystifying-evals-for-ai-agents)
- ABC T.10: use pilot runs to find outliers. Easy tasks that everyone fails suggest the task is impossible, and hard tasks that only some pass suggest shortcuts. — [ABC](https://arxiv.org/html/2507.02825)
- Ai2's "Signal and Noise" (NeurIPS 2025):
  - Defines **signal** as how well a benchmark separates stronger from weaker models, and **noise** as its sensitivity to random variation.
  - Benchmarks with a higher signal-to-noise ratio are more reliable for decisions.
  - Interventions that help: **filtering out noisy subtasks** and using metrics with better SNR (e.g. continuous instead of binary).
  — [Signal and Noise, arXiv 2508.13144](https://arxiv.org/html/2508.13144v1); [Ai2 blog](https://allenai.org/blog/signal-noise)
- tau-bench used at least 40 gpt-4-turbo trials per retail task to measure task difficulty, an example of empirical per-task difficulty estimation. — [tau-bench](https://arxiv.org/html/2406.12045)

### Inferences (calibration procedure)
1. **Draft tiers by mechanism**, for example:
   - T1: a single resource lookup.
   - T2: multi-resource joins and filters (e.g. Observation + Encounter + date window).
   - T3: aggregation or temporal reasoning across many resources, terminology mapping, or pagination traps.
   - T4 (optional): requires an MCP or FHIR-specific capability, such as `_include`/`_revinclude`, chained search or terminology `$expand`.
2. **Pilot:**
   - Run about 2 contrasting profiles (weak, e.g. small model with no tools; strong, e.g. best model with full tools), K=3, on an over-generated pool of about 200-250 candidates.
   - Estimate each item's empirical difficulty p_i and its discordance between profiles.
3. **Select items:**
   - Drop items that every profile passes in every run (no signal) and items that every profile fails, unless the oracle confirms they are valid and you intend them as headroom.
   - Keep some items above the current strongest profile, so the bench doesn't saturate when models improve.
   - Prefer items with high discrimination (the strong profile passes, the weak one fails) and low run-to-run variance. This is SNR filtering in the spirit of Ai2.
4. **Targets:** best profile overall at about 60-75%, weakest at about 15-30%. Per tier, the strong profile at roughly 85-95% on T1, 50-70% on T2 and 20-40% on T3. This keeps the mean away from the 0% and 100% ceilings, where the binomial variance p(1-p) is small but so is the signal.
5. **Balance tiers** at about 50 each, or weight them toward the tier where profiles actually differ. Stratify any subsampling by tier, and keep tier labels fixed before the final runs, so tiers are not redefined after seeing results.
6. **Add partial-credit diagnostics** (e.g. set precision and recall) as secondary continuous metrics. They have higher SNR than binary correctness (per Ai2), while the primary metric stays binary.

### Gaps
- I did not retrieve item response theory (IRT) based benchmark-selection methods (e.g. tinyBenchmarks or fluid benchmarking) in this session. They would provide formal item-discrimination estimates and are worth a follow-up.
- The target accuracy bands above are my judgment, derived from variance and saturation reasoning, not from a cited source.
