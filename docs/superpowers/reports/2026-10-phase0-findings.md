# Phase 0 findings: first paid run (2026-10-16)

## Setup

| | |
|---|---|
| Corpus | `synthea-sample-v1` (pinned, now = 2026-08-17) |
| Question set | `question-sets/phase0.txt` (10 questions, T1–T4, T6, T7), K = 1 |
| Harness | Copilot CLI 1.0.98, `claude-sonnet-5.5`, credit cap 30 per attempt |
| P1 | shell only (`view`, `rg`, `glob`, `powershell`, `apply_patch`) |
| P2 | P1 + `cli-on-fhir` MCP, ignixa-fhir PR #504 at `0462637d`, built from source (Release). 16 tools: folder, search, read, patient-context and terminology; `terminology_load_package` off |
| Baselines | B0 oracle 100%, B1 abstain 20% on phase0 (6% on v1) |

## Results

| Profile | Correct | Credits (total) | Mean time per question |
|---|---|---|---|
| P1 shell | 10/10 | 45.3 | 22 s |
| P2 shell + cli-on-fhir | 10/10 | 62.5 | 25 s |

Paired results: both profiles were correct on all 10 questions, so there were no discordant pairs and McNemar p = 1.0. **The phase0 set does not discriminate between the profiles.** Sonnet 5.5 with a shell answers every question by grepping the patient's Bundle and parsing it in PowerShell. This is expected for 10 mostly T1–T3 questions on a corpus where one file holds one patient. The v1 set and the Phase 1 corpus exist to fix this.

Per question, P2 cost more on 9 of 10 questions (by 0.3 to 4.6 credits each). It cost less only on the T6 cohort question (5.1 against 5.5 credits, and 20 s against 56 s). That cohort question is the only one where the tools clearly helped.

## How the agent used cli-on-fhir (P2)

- Tools used: `patient_list` (9 of 10), `search_fhir_resources` (every question; with `compartment`, `_sort=-date` and `elements`), `context_find`, `folder_inspect` and `read_resources`. No terminology tool was called. `t4-ever-statin-001` solved the drug class with a `context_find` and its own RxNorm list.
- Recurrent waste: `patient_list` returns about 26 KB on this corpus, so Copilot spills it to a temp file and the agent greps it. Each question that starts this way pays one wasted turn, about 1–2 credits. **Feedback for PR #504:** add a name/birth-date/identifier filter or a compact mode to `patient_list`. A tool description that points to `search_fhir_resources` `Patient?name=` would also help.
- `search_fhir_resources` worked first time on every call, including compartment searches and `_sort`.

## Harness checks

- No folder-trust stall. `claude-sonnet-5.5` was accepted. `answer.json` was written and graded on all 22 attempts (2 smoke runs plus 20). No harness errors.
- The `fhir-*` tools appear in the transcripts and logs, so P2 really differs from P1.
- File writes use `create` and `apply_patch`, plus PowerShell `Set-Content`.
- Out-of-bounds audit: across all transcripts, the agents touched only the corpus, their workspace and Copilot's own temp spill files. No access to `questions/`, the gold lock or earlier runs was found.
- Not exercised: credit-cap exhaustion (the highest attempt used 10.3 of 30 credits) and Ctrl+C cleanup.

## Environment notes

- Copilot had to be upgraded with `winget upgrade --force`, because winget refused a "modified" portable package.
- `COPILOT_GITHUB_TOKEN` was taken from `gh auth token`. The agent's shell can run `gh` itself (the parked sandbox item).
- Building PR #504 needs .NET SDK 10.0.401, but this machine has 10.0.113. The scratch build relaxed `global.json` and set `-p:DisableGitVersionTask=true` (GitVersion fails on a detached worktree).
- A cold, uncached `inspect --build-index` over 119,981 resources took about 36 s, and each process caches its index afterwards. That is practical for Phase 0.

## Next

1. Run P1 and P2 on `question-sets/v1.txt`. The estimate at the observed mean cost is about 450 credits for P1 and 630 for P2. The cap allows up to 6,000.
2. Pass the `patient_list` finding to PR #504.
3. Add a P2 variant with `--tx-server` terminology so the MAP questions can use `terminology_translate`.
