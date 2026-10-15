from __future__ import annotations

import copy
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time

import pytest

from fhir_question_bench.corpus import load_corpus
from fhir_question_bench.demo import make_demo_response
from fhir_question_bench.oracle import DATA_ROOT, derive_gold, load_questions


ROOT = Path(__file__).parents[1]


@pytest.fixture
def setup_run(tmp_path):
    config = {
        "copilot_command": ["copilot-offline-placeholder.exe"],
        "copilot_version": "1.0.97",
        "model": "fixed-model",
        "reasoning_effort": "high",
        "wall_timeout_seconds": 10,
        "max_ai_credits": 30,
        "max_trace_bytes": 8192,
        "max_answer_bytes": 16384,
        "auth_env": ["COPILOT_GITHUB_TOKEN"],
    }
    path = tmp_path / "config.json"
    path.write_text(json.dumps(config))
    fixture = tmp_path / "fake with spaces"
    fixture.mkdir()
    q = load_questions(DATA_ROOT / "questions.jsonl")[0]
    corpus = load_corpus(DATA_ROOT / "agent" / "corpus-manifest.json")
    # Deliberately oracle/self-check fixture data, never supplied to real agents.
    response = make_demo_response(q, derive_gold(q, corpus, DATA_ROOT))
    (fixture / "fixture-response.json").write_text(json.dumps(response))
    return config, path, fixture, tmp_path


def invoke(setup, *args, config=None, output="run", mode=None, tool_mode=None, data_root=None,
           timeout=120):
    settings, path, fixture, root = setup
    path.write_text(json.dumps(settings if config is None else config))
    command = [sys.executable, "-B", "-m", "fhir_question_bench"]
    if data_root:
        command += ["--data-root", str(data_root)]
    command += ["run", "--config", str(path), "--output", str(root / output)]
    if mode:
        fake = fixture / f"{mode}.py"
        shutil.copyfile(ROOT / "tests" / "fake_copilot.py", fake)
        command += ["--fake-process", str(fake)]
    if tool_mode:
        tool = fixture / f"{tool_mode}.py"
        shutil.copyfile(ROOT / "tests" / "fake_copilot.py", tool)
        command += ["--fake-tool-process", str(tool)]
    env = dict(os.environ, PYTHONPATH=str(ROOT / "src"),
               RUNNER_TEST_SECRET="never-in-child", COPILOT_GITHUB_TOKEN="test-do-not-forward")
    result = subprocess.run(command + list(args), env=env, capture_output=True, text=True,
                            timeout=timeout, check=False)
    report = root / output / "report.json"
    return result, json.loads(report.read_text()) if report.exists() else None


def one_question(config, arms=None):
    config = copy.deepcopy(config)
    config["question_ids"] = ["direct-001"]
    config["arms"] = arms or ["filesystem"]
    return config


def test_default_dry_run_prepares_sixty_without_launching(setup_run):
    config, _, fixture, root = setup_run
    config["copilot_command"] = [sys.executable, str(fixture / "must-not-run.py")]
    result, report = invoke(setup_run)
    assert result.returncode == 0, result.stderr
    assert report["mode"] == "dry-run"
    assert report["measured_agent_performance"] is False
    assert report["counts"] == {"planned": 60, "completed": 0, "failed": 0, "blocked": 0,
                                 "not_run": 60, "strict_success": 0}
    assert report["budget"]["total_credit_soft_cap"] == 1800
    assert report["budget"]["pricing_usd"] is None
    assert len(list((root / "run" / "episodes").glob("*/*/workspace/question.json"))) == 60
    assert not (root / "run" / "episodes" / "direct-001" / "filesystem" / "stdout.jsonl").exists()
    assert len({row["config_home"] for row in report["episodes"]}) == 60
    assert not list((root / "run" / "episodes").rglob("*gold*"))
    again, _ = invoke(setup_run)
    assert again.returncode == 2 and "non-empty" in again.stderr


def test_prepared_argv_and_fresh_mcp_config_all_arms(setup_run):
    config = one_question(setup_run[0], ["filesystem", "cli", "mcp"])
    config["tool"] = {"command": [r"C:\tool folder\cli-on-fhir.exe"],
                      "expected_revision": "abc123", "mcp_tools": ["search", "read"]}
    result, report = invoke(setup_run, config=config)
    assert result.returncode == 0, result.stderr
    for row in report["episodes"]:
        argv = row["copilot_argv"]
        assert argv[:1] == ["copilot-offline-placeholder.exe"]
        assert argv[argv.index("--model") + 1] == "fixed-model"
        assert argv[argv.index("--max-ai-credits") + 1] == "30"
        for flag in ("--no-custom-instructions", "--no-ask-user", "--no-auto-update",
                     "--no-remote", "--no-remote-export", "--disable-builtin-mcps"):
            assert flag in argv
        assert "--secret-env-vars" in argv
        assert "--allow-all" not in argv and "--allow-all-paths" not in argv
        assert "--allow-all-urls" not in argv
        home = Path(row["config_home"])
        assert sorted(p.name for p in home.iterdir()) == ["config.json"]
        assert json.loads((home / "config.json").read_text())["mcpServers"] == {}
        prompt = argv[argv.index("-p") + 1]
        assert "response-contract.json" in prompt and "scope_template" in prompt
        assert "gold" not in prompt.lower()
        if row["arm"] == "filesystem":
            assert "cli-on-fhir" not in prompt
            assert "--additional-mcp-config" not in argv
        elif row["arm"] == "cli":
            assert json.dumps([r"C:\tool folder\cli-on-fhir.exe"]) in prompt
            assert "--additional-mcp-config" not in argv
        else:
            extra = argv[argv.index("--additional-mcp-config") + 1]
            mcp = json.loads(Path(extra[1:]).read_text())["mcpServers"]["fhir"]
            assert mcp["command"] == r"C:\tool folder\cli-on-fhir.exe"
            assert mcp["args"] == ["mcp", str(Path(row["workspace"]) / "corpus")]
            assert mcp["tools"] == ["search", "read"]
    assert report["snapshot"]["tool"]["revision_status"] == "unverified-declaration"
    permissions = report["snapshot"]["permissions"]
    assert permissions["all_paths"] is permissions["all_urls"] is False
    assert permissions["personal_config"] is False and permissions["builtin_mcps"] is False
    assert set(permissions["effective_argv_sha256"]) == {"filesystem", "cli", "mcp"}


@pytest.mark.parametrize("patch", [
    {"model": "auto"}, {"max_ai_credits": 29}, {"wall_timeout_seconds": 0},
    {"copilot_command": "copilot -p bad"}, {"auth_env": ["RUNNER_TEST_SECRET"]},
    {"boundary_verified": True}, {"question_ids": ["unknown"]},
    {"arms": ["filesystem", "filesystem"]}, {"max_trace_bytes": True},
])
def test_invalid_config_fails_before_preparation(setup_run, patch):
    config = {**setup_run[0], **patch}
    result, report = invoke(setup_run, config=config)
    assert result.returncode == 2 and report is None


def test_live_is_blocked_even_with_budget_auth_and_fake_boundary_claim(setup_run):
    config = one_question(setup_run[0])
    result, report = invoke(setup_run, "--execute", "--approve-total-credits", "30", config=config)
    assert result.returncode == 1, result.stderr
    assert report["counts"]["blocked"] == 1
    assert report["episodes"][0]["status"] == "blocked_containment"
    assert report["episodes"][0]["exit_code"] is None
    assert report["episodes"][0]["usage"] is None
    assert "test-do-not-forward" not in json.dumps(report)
    no_budget, _ = invoke(setup_run, "--execute", config=config, output="no-budget")
    assert no_budget.returncode == 2
    mismatch, _ = invoke(setup_run, "--execute", "--approve-total-credits", "60",
                         config=config, output="wrong-budget")
    assert mismatch.returncode == 2


def test_fake_real_subprocess_success_with_usage_and_observed_argv(setup_run):
    config = one_question(setup_run[0])
    result, report = invoke(setup_run, config=config, mode="success")
    assert result.returncode == 0, result.stderr
    assert report["mode"] == "offline-fake-process"
    assert report["measured_agent_performance"] is False
    assert report["counts"] == {"planned": 1, "completed": 1, "failed": 0, "blocked": 0,
                                 "not_run": 0, "strict_success": 1}
    row = report["episodes"][0]
    assert row["status"] == "completed" and row["exit_code"] == 0
    assert row["usage"]["input_tokens"] == 12
    assert row["tool_calls"] == 1
    assert row["pricing_usd"] is None
    observed = json.loads((Path(row["directory"]) / "observed.json").read_text())
    assert observed["argv"] == row["copilot_argv"][1:]
    assert row["elapsed_seconds"] > 0
    assert "not model performance" in (setup_run[3] / "run" / "report.md").read_text()


@pytest.mark.parametrize(("mode", "expected"), [
    ("nonzero", "process_failed"),
    ("missing", "missing_answer"),
    ("injected", "missing_answer"),
    ("malformed", "invalid_response"),
    ("oversize", "answer_overflow"),
    ("overflow", "trace_overflow"),
    ("timeout", "timeout"),
    ("mutation", "data_mutation"),
    ("unsupported", "answer_failed"),
    ("unterminated", "missing_terminal_event"),
    ("link", "unsafe_answer"),
])
def test_fake_failures_cannot_be_successful(setup_run, mode, expected):
    config = one_question(setup_run[0])
    result, report = invoke(setup_run, config=config, mode=mode)
    assert result.returncode == 1, result.stderr
    row = report["episodes"][0]
    assert row["status"] == expected, row
    assert report["counts"]["planned"] == report["counts"]["failed"] == 1
    assert report["counts"]["strict_success"] == 0
    assert row["strict_success"] is False
    if mode == "overflow":
        assert row["trace_truncated"] is True
        assert sum((Path(row["directory"]) / name).stat().st_size
                   for name in ("stdout.jsonl", "stderr.txt")) <= 8192


def test_timeout_kills_descendant_not_just_parent(setup_run):
    config = one_question(setup_run[0])
    result, report = invoke(setup_run, config=config, mode="descendant")
    assert result.returncode == 1
    row = report["episodes"][0]
    assert row["status"] == "timeout"
    directory = Path(row["directory"])
    assert (directory / "child-pid.txt").exists()
    (directory / "release-child").write_text("release")
    time.sleep(1)
    assert not (directory / "escaped.txt").exists()
    assert report["containment"]["verified"] is False


def test_missing_tools_preserve_denominators_and_no_fallback(setup_run):
    config = one_question(setup_run[0], ["filesystem", "cli", "mcp"])
    result, report = invoke(setup_run, config=config, mode="success")
    assert result.returncode == 1, result.stderr
    assert report["counts"]["planned"] == 3
    assert report["counts"]["completed"] == 1
    assert report["counts"]["blocked"] == 2
    assert [r["status"] for r in report["episodes"]] == [
        "completed", "blocked_tool", "blocked_tool"]


def test_explicit_fake_tool_handshake_is_not_real_tool_verification(setup_run):
    config = one_question(setup_run[0], ["cli", "mcp"])
    config["tool"] = {"command": ["not-installed-fhir.exe"],
                      "expected_revision": "abc", "mcp_tools": ["search", "read"]}
    result, report = invoke(setup_run, config=config, mode="success", tool_mode="tool")
    assert result.returncode == 0, result.stderr
    assert report["counts"]["completed"] == 2
    assert report["preflight"]["mcp"]["tools"] == ["read", "search"]
    assert report["preflight"]["mcp"]["mode"] == "offline-fake-process"
    assert report["snapshot"]["tool"]["revision_status"] == "unverified-declaration"
    failed, blocked = invoke(setup_run, config=config, mode="success", tool_mode="badtools",
                             output="badtools")
    assert failed.returncode == 1
    assert blocked["counts"]["blocked"] == 2
    assert all(row["status"] == "blocked_tool" for row in blocked["episodes"])


def test_changed_config_snapshot_invalidates_current_comparison(setup_run):
    config = one_question(setup_run[0])
    _, first = invoke(setup_run, config=config)
    config["model"] = "different-fixed-model"
    _, second = invoke(setup_run, config=config, output="second")
    assert first["snapshot_id"] != second["snapshot_id"]
    snapshot = first["snapshot"]
    assert len(snapshot["gold_sha256"]["direct-001"]) == 64
    assert snapshot["actual_model_version"] is None
    assert snapshot["exposure"] == "adaptive-engineering"
    assert first["result_reuse_policy"] == "exact-snapshot-only; no historical pooling"
    assert snapshot["source_sha256"]["grading.py"]
    assert snapshot["permissions"] and snapshot["prompt_sha256"]


def test_prompt_metacharacters_stay_one_argv_argument(setup_run):
    config = one_question(setup_run[0], ["cli"])
    config["tool"] = {"command": [r"C:\FHIR tools\engine.exe", "x & echo injected > hacked"],
                      "expected_revision": "declared", "mcp_tools": ["read", "search"]}
    result, report = invoke(setup_run, config=config, mode="success", tool_mode="tool")
    assert result.returncode == 0, result.stderr
    row = report["episodes"][0]
    observed = json.loads((Path(row["directory"]) / "observed.json").read_text())
    assert observed["argv"] == row["copilot_argv"][1:]
    assert not list(setup_run[3].rglob("hacked"))


def test_integrity_failure_stops_remaining_planned_slots(setup_run):
    config = one_question(setup_run[0], ["filesystem", "cli", "mcp"])
    result, report = invoke(setup_run, config=config, mode="mutation")
    assert result.returncode == 1
    assert [r["status"] for r in report["episodes"]] == [
        "data_mutation", "blocked_integrity", "blocked_integrity"]
    assert report["counts"]["failed"] == 1
    assert report["counts"]["blocked"] == 2


def test_missing_auth_is_clear_without_starting_configured_command(setup_run, monkeypatch):
    for name in ("COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN"):
        monkeypatch.delenv(name, raising=False)
    from fhir_question_bench.runner import run_benchmark

    config, path, _, root = setup_run
    config = one_question(config)
    path.write_text(json.dumps(config))
    report = run_benchmark(path, root / "missing-auth", DATA_ROOT, execute=True,
                           approve_total_credits=30)
    row = report["episodes"][0]
    assert row["status"] == "blocked_auth"
    assert any("missing auth" in error for error in row["errors"])
    assert row["exit_code"] is None


def test_windows_command_wrappers_are_never_shell_concatenated(setup_run):
    config = one_question(setup_run[0])
    config["copilot_command"] = [r"C:\Program Files\copilot.cmd", "arg & echo bad"]
    result, report = invoke(setup_run, config=config)
    assert result.returncode == 0
    assert report["episodes"][0]["copilot_argv"][:2] == config["copilot_command"]
    assert report["snapshot"]["copilot"]["launch_support"] == "requires-executable-or-interpreter-argv"


def test_missing_trace_events_are_unknown_tool_count_not_zero(setup_run):
    config = one_question(setup_run[0])
    result, report = invoke(setup_run, config=config, mode="noevents")
    assert result.returncode == 1
    assert report["episodes"][0]["status"] == "missing_terminal_event"
    assert report["episodes"][0]["tool_calls"] is None


def test_response_symlink_is_rejected_without_following(setup_run):
    _, _, fixture, _ = setup_run
    probe = fixture / "can-symlink"
    try:
        probe.symlink_to(fixture / "fixture-response.json")
    except OSError:
        pytest.skip("host does not permit test symlinks")
    probe.unlink()
    result, report = invoke(setup_run, config=one_question(setup_run[0]), mode="symlink")
    assert result.returncode == 1
    assert report["episodes"][0]["status"] == "unsafe_answer"


def test_mcp_protocol_error_blocks_without_fallback(setup_run):
    config = one_question(setup_run[0], ["mcp"])
    config["tool"] = {"command": ["absent.exe"], "expected_revision": "declared",
                      "mcp_tools": ["read", "search"]}
    result, report = invoke(setup_run, config=config, mode="success", tool_mode="badprotocol")
    assert result.returncode == 1
    assert report["counts"]["blocked"] == 1
    assert report["episodes"][0]["status"] == "blocked_tool"
    assert report["preflight"]["mcp"]["failure"] == "protocol_error"


def test_runner_question_excludes_private_task_and_review_labels(setup_run):
    result, report = invoke(setup_run, config=one_question(setup_run[0]))
    assert result.returncode == 0
    work = Path(report["episodes"][0]["workspace"])
    public = json.loads((work / "question.json").read_text())
    assert set(public) == {"id", "cutoff", "corpus_id", "protocol_id", "selection_policy",
                           "answer_type", "prompt", "parameters"}
    assert public["parameters"] == {"patient": "Patient/p-ada"}
    assert (work / "response-contract.json").exists()
    assert (work / "selection-policies.json").exists()


def test_added_source_is_an_integrity_failure(setup_run):
    result, report = invoke(setup_run, config=one_question(setup_run[0]), mode="addedsource")
    assert result.returncode == 1
    assert report["episodes"][0]["status"] == "data_mutation"


@pytest.mark.parametrize(("mode", "expected"), [
    ("badusage", "invalid_usage"), ("sessionerror", "session_failed"),
])
def test_invalid_usage_and_session_error_fail_closed(setup_run, mode, expected):
    result, report = invoke(setup_run, config=one_question(setup_run[0]), mode=mode)
    assert result.returncode == 1
    assert report["episodes"][0]["status"] == expected


@pytest.mark.parametrize("patch", [
    {"arms": [[]]}, {"reasoning_effort": []}, {"tool": {
        "command": ["fhir"], "expected_revision": "declared", "mcp_tools": [[]]}},
])
def test_malformed_config_types_are_explicit_errors_not_tracebacks(setup_run, patch):
    result, _ = invoke(setup_run, config={**setup_run[0], **patch})
    assert result.returncode == 2
    assert "error:" in result.stderr and "Traceback" not in result.stderr


@pytest.mark.skipif(os.name != "nt", reason="Windows suspended-process assignment failure")
def test_failed_job_assignment_reaps_suspended_child(tmp_path, monkeypatch):
    from fhir_question_bench import processes
    from fhir_question_bench.runner import _environment

    children = []

    def fail_assignment(self, process):
        children.append(process)
        raise OSError("injected job assignment failure")

    monkeypatch.setattr(processes._WindowsJob, "assign_and_resume", fail_assignment)
    try:
        with pytest.raises(OSError, match="injected job assignment failure"):
            processes.capture_process([sys.executable, "-I", "-c", "raise SystemExit(0)"],
                                      tmp_path, _environment(tmp_path), 5, 4096)
        assert children[0].poll() is not None
    finally:
        for child in children:
            if child.poll() is None:
                child.kill()
                child.wait(timeout=5)


def test_interpreter_hosted_tool_content_invalidates_snapshot(setup_run):
    config = one_question(setup_run[0], ["cli"])
    script = setup_run[2] / "specialist.py"
    script.write_text("print('version one')")
    config["tool"] = {"command": [sys.executable, str(script)],
                      "expected_revision": "declared", "mcp_tools": ["read"]}
    first, report1 = invoke(setup_run, config=config)
    script.write_text("print('version two')")
    second, report2 = invoke(setup_run, config=config, output="changed-tool")
    assert first.returncode == second.returncode == 0
    assert report1["snapshot_id"] != report2["snapshot_id"]


def test_fake_tool_injection_requires_tool_configuration(setup_run):
    result, _ = invoke(setup_run, config=one_question(setup_run[0]),
                       mode="success", tool_mode="tool")
    assert result.returncode == 2
    assert "requires configured tool" in result.stderr


def test_separate_private_data_root_reaches_grader(setup_run):
    config = one_question(setup_run[0])
    root = setup_run[3] / "custom-data"
    shutil.copytree(DATA_ROOT, root)
    protocol_path = root / "agent" / "protocols" / "protocols.json"
    protocols = json.loads(protocol_path.read_text())
    protocol = next(p for p in protocols["protocols"] if p["id"] == "p-direct-001")
    protocol["rule_id"] = "custom-date-v1"
    protocol_path.write_text(json.dumps(protocols))
    manifest_path = root / "dataset-manifest.json"
    manifest = json.loads(manifest_path.read_text())
    for item in manifest["files"]:
        item["sha256"] = hashlib.sha256((root / item["path"]).read_bytes()).hexdigest()
    manifest_path.write_text(json.dumps(manifest))
    question = load_questions(root / "questions.jsonl")[0]
    corpus = load_corpus(root / "agent" / "corpus-manifest.json")
    response = make_demo_response(question, derive_gold(question, corpus, root))
    assert response["claims"][0]["rule_id"] == "custom-date-v1"
    (setup_run[2] / "fixture-response.json").write_text(json.dumps(response))
    result, report = invoke(setup_run, config=config, mode="success", data_root=root)
    assert result.returncode == 0, result.stderr
    assert report["episodes"][0]["strict_success"] is True


def test_all_twenty_through_sixty_fake_subprocesses_are_only_engineering_evidence(setup_run):
    config = copy.deepcopy(setup_run[0])
    config["max_answer_bytes"] = 262144
    config["tool"] = {"command": ["offline-specialist-placeholder.exe"],
                      "expected_revision": "not-a-verified-build", "mcp_tools": ["read", "search"]}
    questions = load_questions(DATA_ROOT / "questions.jsonl")
    corpus = load_corpus(DATA_ROOT / "agent" / "corpus-manifest.json")
    responses = {q.id: make_demo_response(q, derive_gold(q, corpus, DATA_ROOT)) for q in questions}
    (setup_run[2] / "fixture-response.json").write_text(json.dumps(responses))
    result, report = invoke(setup_run, config=config, mode="success", tool_mode="tool", timeout=600)
    assert result.returncode == 0, result.stderr
    assert report["counts"] == {"planned": 60, "completed": 60, "failed": 0, "blocked": 0,
                                 "not_run": 0, "strict_success": 60}
    assert report["mode"] == "offline-fake-process" and not report["measured_agent_performance"]
    assert report["containment"]["verified"] is False
    assert report["snapshot"]["actual_model_version"] is None
    for arm in ("filesystem", "cli", "mcp"):
        assert report["by_arm"][arm] == {
            "planned": 20, "status_counts": {"completed": 20}, "strict_success": 20}

@pytest.mark.parametrize(("mode", "expected"), [
    ("idlethen-tool", "invalid_lifecycle"),
    ("idlethen-session", "invalid_lifecycle"),
    ("session-mismatch", "invalid_lifecycle"),
    ("idlethen-activity", "invalid_lifecycle"),
])
def test_terminal_idle_must_be_final_and_session_associated(setup_run, mode, expected):
    result, report = invoke(setup_run, config=one_question(setup_run[0]), mode=mode)
    assert result.returncode == 1, result.stderr
    assert report["episodes"][0]["status"] == expected
    assert report["counts"]["failed"] == 1


@pytest.mark.parametrize(("mode", "expected"), [
    ("deep-answer", "invalid_response"),
    ("deep-usage", "invalid_usage"),
    ("deep-trace", "invalid_trace"),
])
def test_deep_json_is_a_bounded_episode_failure_and_report_persists(setup_run, mode, expected):
    config = one_question(setup_run[0])
    config["max_answer_bytes"] = 20000
    config["max_trace_bytes"] = 20000
    result, report = invoke(setup_run, config=config, mode=mode)
    assert result.returncode == 1, result.stderr
    assert report["episodes"][0]["status"] == expected
    counts = report["counts"]
    assert counts["planned"] == counts["completed"] + counts["failed"] + counts["blocked"] + counts["not_run"]
    assert counts["failed"] == 1
    assert (setup_run[3] / "run" / "report.md").is_file()


@pytest.mark.parametrize("override", [
    "--allow-all", "--allow-all-paths", "--allow-all-urls", "--allow-all-tools",
    "--additional-mcp-config=@personal.json", "--model=other", "--reasoning-effort high",
    "--max-ai-credits", "--usage-output-file=other.json", "-p injected prompt",
    "--resume=old-session", "--config", "--mcp-config personal.json",
    "--allow-all-p", "--model other", "--",
])
def test_configured_copilot_command_cannot_override_runner_owned_options(setup_run, override):
    config = one_question(setup_run[0])
    config["copilot_command"] = ["copilot", *override.split()]
    result, report = invoke(setup_run, config=config)
    assert result.returncode == 2, result.stderr
    assert report is None
    assert "runner-owned" in result.stderr


@pytest.mark.skipif(os.name != "nt", reason="Windows Job Object descendant completion")
def test_normal_parent_exit_waits_for_job_descendants(tmp_path):
    from fhir_question_bench.processes import capture_process

    code = (
        "import subprocess,sys; from pathlib import Path; "
        "child=subprocess.Popen([sys.executable,'-c','import time; time.sleep(30)']); "
        "Path('child-pid.txt').write_text(str(child.pid))"
    )
    result = capture_process([sys.executable, "-I", "-c", code], tmp_path,
                             dict(os.environ), 5, 4096)
    assert result.failure is None
    pid = int((tmp_path / "child-pid.txt").read_text())
    import ctypes
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    handle = kernel.OpenProcess(0x00100000, False, pid)
    if handle:
        try:
            assert kernel.WaitForSingleObject(handle, 0) == 0
        finally:
            kernel.CloseHandle(handle)


def test_preflight_mutation_is_checked_before_episode_dispatch(setup_run, monkeypatch):
    from fhir_question_bench import runner
    from fhir_question_bench.processes import ProcessResult

    config, config_path, fixture, root = setup_run
    config = one_question(config)
    config_path.write_text(json.dumps(config))
    data_root = root / "protected-data"
    shutil.copytree(DATA_ROOT, data_root)
    fake = fixture / "success.py"
    shutil.copyfile(ROOT / "tests" / "fake_copilot.py", fake)
    calls = []

    def mutate_during_preflight(argv, cwd, env, timeout, max_bytes, on_line=None):
        calls.append(argv)
        assert argv[-1] == "--version"
        (data_root / "agent" / "corpus" / "resources.ndjson").write_bytes(b"changed source")
        return ProcessResult(0, 0.01, b"offline-fixture-1\n", b"", None, False, ())

    monkeypatch.setattr(runner, "capture_process", mutate_during_preflight)
    report = runner.run_benchmark(config_path, root / "preflight-source-change", data_root,
                                  fake_process=fake)
    assert len(calls) == 1
    assert report["episodes"][0]["status"] == "blocked_integrity"
    assert report["counts"]["blocked"] == 1
    assert report["counts"]["planned"] == report["counts"]["failed"] + report["counts"]["blocked"] + report["counts"]["completed"] + report["counts"]["not_run"]


def test_specialist_startup_exception_does_not_block_filesystem_arm(setup_run, monkeypatch):
    from fhir_question_bench import runner
    from fhir_question_bench.processes import ProcessResult

    config, config_path, fixture, root = setup_run
    config = one_question(config, ["filesystem", "cli", "mcp"])
    config["tool"] = {"command": ["missing-specialist.exe"], "expected_revision": "declared",
                      "mcp_tools": ["search", "read"]}
    config_path.write_text(json.dumps(config))
    fake = fixture / "success.py"
    fake_tool = fixture / "tool.py"
    shutil.copyfile(ROOT / "tests" / "fake_copilot.py", fake)
    shutil.copyfile(ROOT / "tests" / "fake_copilot.py", fake_tool)
    original = runner.capture_process

    def raise_for_mcp(argv, cwd, env, timeout, max_bytes, on_line=None):
        if "mcp" in argv:
            raise OSError("specialist startup failed")
        return original(argv, cwd, env, timeout, max_bytes, on_line)

    monkeypatch.setattr(runner, "capture_process", raise_for_mcp)
    report = runner.run_benchmark(config_path, root / "specialist-startup-error", DATA_ROOT,
                                  fake_process=fake,
                                  fake_tool_process=fake_tool)
    assert [row["status"] for row in report["episodes"]] == ["completed", "blocked_tool", "blocked_tool"]
    assert "specialist startup failed" in report["preflight"]["mcp_error"]


def test_preflight_workspace_mutation_is_not_rebaselined(setup_run, monkeypatch):
    from fhir_question_bench import runner
    from fhir_question_bench.processes import ProcessResult

    config, config_path, fixture, root = setup_run
    config = one_question(config)
    config_path.write_text(json.dumps(config))
    fake = fixture / "success.py"
    shutil.copyfile(ROOT / "tests" / "fake_copilot.py", fake)
    output = root / "preflight-workspace-change"
    workspace = output / "episodes" / "direct-001" / "filesystem" / "workspace"

    def mutate_during_preflight(argv, cwd, env, timeout, max_bytes, on_line=None):
        (workspace / "corpus" / "extra.ndjson").write_text("{}")
        return ProcessResult(0, 0.01, b"offline-fixture-1\n", b"", None, False, ())

    monkeypatch.setattr(runner, "capture_process", mutate_during_preflight)
    report = runner.run_benchmark(config_path, output, DATA_ROOT, fake_process=fake)
    assert report["episodes"][0]["status"] == "blocked_integrity"
    assert report["counts"]["blocked"] == 1


def test_original_terminology_change_during_episode_fails_collection(setup_run, monkeypatch):
    from fhir_question_bench import runner

    config, config_path, fixture, root = setup_run
    config = one_question(config)
    config_path.write_text(json.dumps(config))
    fake = fixture / "success.py"
    shutil.copyfile(ROOT / "tests" / "fake_copilot.py", fake)
    data_root = root / "terminology-data"
    shutil.copytree(DATA_ROOT, data_root)
    original = runner.capture_process
    calls = 0

    def mutate_during_episode(argv, cwd, env, timeout, max_bytes, on_line=None):
        nonlocal calls
        calls += 1
        if calls == 1:
            return original(argv, cwd, env, timeout, max_bytes, on_line)
        (data_root / "agent" / "terminology" / "CodeSystem.json").write_bytes(b"changed terminology")
        return original(argv, cwd, env, timeout, max_bytes, on_line)

    monkeypatch.setattr(runner, "capture_process", mutate_during_episode)
    report = runner.run_benchmark(config_path, root / "terminology-change", data_root,
                                  fake_process=fake)
    assert calls == 2
    assert report["episodes"][0]["status"] == "data_mutation"
    assert report["counts"]["failed"] == 1


def test_runner_selects_only_the_original_initial_twenty_when_data_expands(setup_run, monkeypatch):
    from dataclasses import replace
    from fhir_question_bench import runner

    questions = load_questions(DATA_ROOT / "questions.jsonl")
    expanded = [*questions, replace(questions[0], id="qa-extra-unreviewed")]
    monkeypatch.setattr(runner, "load_questions", lambda _path: expanded)
    config, config_path, _, root = setup_run
    config = copy.deepcopy(config)
    config["arms"] = ["filesystem"]
    config.pop("question_ids", None)
    config_path.write_text(json.dumps(config))

    report = runner.run_benchmark(config_path, root / "initial20-only", DATA_ROOT)
    assert report["counts"]["planned"] == 20
    assert {row["question_id"] for row in report["episodes"]} == {question.id for question in questions[:20]}


def test_initial20_hash_pin_rejects_changed_selected_question(setup_run, monkeypatch):
    from dataclasses import replace
    from fhir_question_bench import runner

    original = load_questions(DATA_ROOT / "questions.jsonl")
    changed = [replace(original[0], prompt=original[0].prompt + " modified")]
    monkeypatch.setattr(runner, "load_questions", lambda _path: changed + original[1:])
    config, config_path, _, root = setup_run
    config_path.write_text(json.dumps(one_question(config)))
    with pytest.raises(ValueError, match="pinned initial20 question hash mismatch: direct-001"):
        runner.run_benchmark(config_path, root / "changed-initial-question", DATA_ROOT)


def test_initial20_runner_rejects_any_new_unreviewed_question_id(setup_run):
    config = one_question(setup_run[0])
    config["question_ids"] = ["qa-101"]
    result, report = invoke(setup_run, config=config)
    assert result.returncode == 2
    assert report is None
    assert "pinned initial20 IDs" in result.stderr


def test_explicit_trailing_telemetry_is_allowed_after_terminal_idle(setup_run):
    result, report = invoke(setup_run, config=one_question(setup_run[0]), mode="telemetry-after-idle")
    assert result.returncode == 0, result.stderr
    assert report["episodes"][0]["status"] == "completed"


@pytest.mark.skipif(os.name != "nt", reason="Windows Job Object cleanup reporting")
def test_unconfirmed_windows_cleanup_blocks_collection_and_advancement(setup_run, monkeypatch):
    from fhir_question_bench import processes, runner

    config, config_path, fixture, root = setup_run
    questions = load_questions(DATA_ROOT / "questions.jsonl")[:2]
    corpus = load_corpus(DATA_ROOT / "agent" / "corpus-manifest.json")
    responses = {question.id: make_demo_response(question, derive_gold(question, corpus, DATA_ROOT))
                 for question in questions}
    (fixture / "fixture-response.json").write_text(json.dumps(responses))
    fake = fixture / "success.py"
    shutil.copyfile(ROOT / "tests" / "fake_copilot.py", fake)
    config = copy.deepcopy(config)
    config["question_ids"] = [question.id for question in questions]
    config["arms"] = ["filesystem"]
    config_path.write_text(json.dumps(config))
    cleanup_calls = 0

    def fail_episode_cleanup(_self, _timeout):
        nonlocal cleanup_calls
        cleanup_calls += 1
        return cleanup_calls == 1

    monkeypatch.setattr(processes._WindowsJob, "terminate_and_wait", fail_episode_cleanup)
    report = runner.run_benchmark(config_path, root / "cleanup-failed", DATA_ROOT,
                                  fake_process=fake)
    assert [row["status"] for row in report["episodes"]] == ["cleanup_failed", "blocked_cleanup"]
    assert "did not exit" in report["episodes"][0]["errors"][0]
    assert "grade" not in report["episodes"][0]
    assert report["counts"]["failed"] == report["counts"]["blocked"] == 1
