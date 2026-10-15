"""Initial20 offline runner. Live submission is deliberately not implemented."""
from __future__ import annotations

from collections import Counter
import hashlib
import json
import math
import os
from pathlib import Path
import platform
import shutil
import stat
import subprocess
import sys
from typing import Any

from .contracts import AgentResponse
from .corpus import load_corpus
from .grading import grade_response
from .oracle import derive_gold, load_questions, verify_dataset_manifest
from .prepare import prepare_question
from .processes import capture_process


ARMS = ["filesystem", "cli", "mcp"]
AUTH_NAMES = ["COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN"]
INITIAL_20_QUESTION_SHA256 = {
    "direct-001": "508adb548e279927ed216dfb047abdf2fd294fe833291a57a721aa215948fcc4",
    "direct-002": "4cd8b5d77a25a91c3e72e28661065eb4cc25e0fc7dcf942a5b5cf3fcc80f63d8",
    "direct-003": "5736b66bf99e3fc11f65eb03e60fc5b383e59afe9b8143bb7a895c1cebfe3e5b",
    "direct-004": "2e2415512d443b76ea0b2b590af69c9441105f794a81933dc751fca8bc573fcc",
    "temporal-001": "3d4209cd8cc484118661d048368b153541c643655d0920ca56017c1f447d3534",
    "temporal-002": "e7e1c6c15cfc7b554a780afa1d5bcb5ea93effec8578c0becc2de0562a54ce9b",
    "temporal-003": "5268608cbb45cb0ba560330c7d9775a0b81450199d03c24ddb415ee697f39f1d",
    "temporal-004": "1119da2cfbfa625853e33a329e50591f487df7b1896b7850a581aaaa339326b5",
    "join-001": "32a3931570929e854220c7c08ada20bc404d91280334a57eb6cc91915010f477",
    "join-002": "43c8a07eb44fc0eb0ea14b47b9a96573f3dbe94901cf7db6d6b981b94808ff4c",
    "join-003": "a68976a7410c8590350273df630c3fcca4403eccc7e5413721a0335fe5e93a69",
    "join-004": "3dd80f44f0ab11e8e476aa0b54d0b6809c80d0a37eda29f02482674c6758abad",
    "term-001": "c739807ed58bc396f2774c27801fcfa6c0644a79b434f1b51072198d160930c5",
    "term-002": "a5044ed16ebcdd17cadfa9cb85587ac6f33d14a4ac891772297c3fe0bdcb49c9",
    "term-003": "fefa4af9e772e1499f4ca0deae95576193ee29f9e3522e531e67854e4283bf78",
    "term-004": "92ec204ec12dd0c14e53955007b5538377cc1b6de2edd0b90bb31e5e819a8e09",
    "cohort-001": "fcfb2b8dfe0a48d80160901d5e4628c30699e1998e122ee67287bfaae4b325ad",
    "cohort-002": "66a4d224c769eb075e79bcd6f8a660f5e2e41b3e5fa97ec93ab9cd68c42763ec",
    "cohort-003": "0437d720a75a5c4b955f5f0be4cd91d89212a6080f5d9295f75692898d16388c",
    "cohort-004": "a99d5a1ab89dea6e9d529e26956e7ab433be48c7482b1e6c3c380f07514f66b2",
}
INITIAL_20_QUESTION_IDS = tuple(INITIAL_20_QUESTION_SHA256)
BUILTIN_TOOLS = ["view", "rg", "glob", "powershell", "read_powershell", "stop_powershell", "apply_patch"]
PROMPT = (
    "Answer question.json using the local corpus, protocols/protocols.json and terminology assets. "
    "Read response-contract.json and selection-policies.json first. Use the exact public "
    "scope_template required/forbidden policy and bindings. Write the final strict response "
    "to answer.json in this working directory. Do not put the answer only in commentary. "
    "Generic file reading, search and shell scripting are permitted. Do not change source assets. "
)


def _hash(value: Any) -> str:
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":"),
                                     ensure_ascii=False).encode("utf-8")).hexdigest()


def _file_hash(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _write_json(path: Path, value: Any) -> None:
    path.write_text(json.dumps(value, indent=2, sort_keys=True, ensure_ascii=False) + "\n",
                    encoding="utf-8")


def _argv(value: Any, name: str) -> list[str]:
    if not isinstance(value, list) or not value or any(
        not isinstance(arg, str) or not arg.strip() or "\0" in arg for arg in value
    ):
        raise ValueError(f"{name} must be a nonempty argv array of strings")
    return value


_RUNNER_OWNED_OPTIONS = {
    "-p", "--prompt", "-C", "--cd", "--working-directory", "--model",
    "--reasoning-effort", "--max-ai-credits", "--usage-output-file",
    "--output-format", "--stream", "--resume", "--continue", "-c",
    "--agent", "--agents", "--config", "--mcp-config", "--additional-mcp-config",
    "--directory", "--",
    "--secret-env-vars", "--allow-all", "--allow-all-paths", "--allow-all-urls",
    "--allow-all-tools", "--allow-tool", "--deny-tool", "--available-tools",
    "--disable-builtin-mcps", "--no-custom-instructions", "--no-ask-user",
    "--no-auto-update", "--no-remote", "--no-remote-export", "--disallow-temp-dir",
    "--log-dir",
}


def _validate_copilot_prefix(command: list[str]) -> None:
    for argument in command[1:]:
        option = argument.split("=", 1)[0]
        abbreviated = option.startswith("--") and any(owned.startswith(option) for owned in _RUNNER_OWNED_OPTIONS
                                                           if owned.startswith("--"))
        if option in _RUNNER_OWNED_OPTIONS or abbreviated or (
                argument.startswith(("-p", "-C")) and argument not in {"-p", "-C"}):
            raise ValueError(f"copilot_command cannot override runner-owned option {argument!r}")


def read_config(path: Path) -> dict:
    config = json.loads(path.read_text(encoding="utf-8"))
    required = {"copilot_command", "copilot_version", "model",
                "wall_timeout_seconds", "max_ai_credits"}
    optional = {"reasoning_effort", "max_trace_bytes", "max_answer_bytes", "auth_env",
                "question_ids", "arms", "tool"}
    if not isinstance(config, dict) or required - config.keys() or config.keys() - required - optional:
        raise ValueError("runner config has missing or unknown fields")
    _argv(config["copilot_command"], "copilot_command")
    _validate_copilot_prefix(config["copilot_command"])
    for key in ("model", "copilot_version"):
        if not isinstance(config[key], str) or not config[key].strip() or config[key].strip().lower() == "auto":
            raise ValueError(f"{key} requires a fixed identifier, not auto")
    if "reasoning_effort" in config and (
        not isinstance(config["reasoning_effort"], str) or config["reasoning_effort"] not in {
            "none", "minimal", "low", "medium", "high", "xhigh", "max"
        }
    ):
        raise ValueError("invalid reasoning_effort")
    config.setdefault("max_trace_bytes", 1048576)
    config.setdefault("max_answer_bytes", 262144)
    for key, minimum in (("wall_timeout_seconds", 0.05), ("max_ai_credits", 30),
                         ("max_trace_bytes", 1), ("max_answer_bytes", 1)):
        value = config[key]
        if type(value) not in (int, float) or not math.isfinite(value) or value < minimum:
            raise ValueError(f"{key} must be finite and at least {minimum}")
        if key.endswith("_bytes") and type(value) is not int:
            raise ValueError(f"{key} must be an integer")
    config.setdefault("arms", ARMS.copy())
    if (not isinstance(config["arms"], list) or not config["arms"]
            or any(arm not in ARMS for arm in config["arms"])
            or len(set(config["arms"])) != len(config["arms"])):
        raise ValueError("arms must be unique filesystem/cli/mcp names")
    config.setdefault("auth_env", AUTH_NAMES.copy())
    if (not isinstance(config["auth_env"], list) or not config["auth_env"]
            or any(name not in AUTH_NAMES for name in config["auth_env"])):
        raise ValueError("auth_env must name only standard GitHub authentication variables")
    if "question_ids" in config:
        ids = config["question_ids"]
        if (not isinstance(ids, list) or not ids or any(not isinstance(q, str) for q in ids)
                or len(set(ids)) != len(ids)):
            raise ValueError("question_ids must be unique question identifiers")
    if "tool" in config:
        tool = config["tool"]
        if not isinstance(tool, dict) or set(tool) != {"command", "expected_revision", "mcp_tools"}:
            raise ValueError("tool requires command, expected_revision and mcp_tools")
        _argv(tool["command"], "tool.command")
        if not isinstance(tool["expected_revision"], str) or not tool["expected_revision"].strip():
            raise ValueError("tool expected_revision is required (a declaration, not verification)")
        names = tool["mcp_tools"]
        if (not isinstance(names, list) or not names or
                any(not isinstance(n, str) or not n.strip() or n == "*" for n in names)
                or len(set(names)) != len(names)):
            raise ValueError("tool.mcp_tools must pin explicit unique tool names")
    return config


def executable_identity(command: list[str]) -> dict:
    found = shutil.which(command[0])
    path = Path(found) if found else None
    code_suffixes = {".exe", ".dll", ".js", ".mjs", ".cjs", ".py", ".cmd", ".bat", ".ps1", ".sh"}
    files = {}
    errors = {}
    for index, argument in enumerate(command):
        if index == 0 and not path:
            continue
        argument = str(path) if index == 0 else argument
        candidate = Path(argument)
        if (index == 0 or candidate.suffix.lower() in code_suffixes) and candidate.is_file():
            try:
                files[str(candidate.resolve())] = _file_hash(candidate)
            except OSError as error:
                errors[str(candidate)] = str(error)
    return {
        "command": command,
        "resolved_executable": str(path.resolve()) if path else None,
        "executable_sha256": files.get(str(path.resolve())) if path else None,
        "argv_code_files_sha256": files,
        "hash_errors": errors,
        "available": bool(path),
        "launch_support": "requires-executable-or-interpreter-argv"
        if Path(command[0]).suffix.lower() in {".cmd", ".bat"} else "shell-false-argv",
    }


def _check_links(path: Path) -> None:
    for part in (path, *path.parents):
        try:
            info = part.lstat()
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ValueError(f"reparse path is forbidden: {part}")


def _protected_inventory(root: Path, ignored_root_files: set[str] | None = None) -> dict[str, str]:
    """Freeze complete directory membership and file bytes, rejecting links."""
    ignored = ignored_root_files or set()
    _check_links(root)
    result = {}
    for path in sorted(root.rglob("*")):
        relative = path.relative_to(root).as_posix()
        if relative in ignored:
            continue
        _check_links(path)
        if path.is_dir():
            result[relative + "/"] = "directory"
        elif path.is_file():
            if path.stat().st_nlink != 1:
                raise ValueError(f"hardlinked protected asset is forbidden: {path}")
            result[relative] = _file_hash(path)
        else:
            raise ValueError(f"unsupported protected workspace entry: {path}")
    return result


def _effective_permissions(config: dict) -> dict:
    effective = {arm: prepare_argv(config, arm, Path("episode")) for arm in config["arms"]}
    all_argv = [argument for argv in effective.values() for argument in argv]
    available = []
    for argv in effective.values():
        try:
            start = argv.index("--available-tools") + 1
        except ValueError:
            continue
        while start < len(argv) and not argv[start].startswith("-"):
            available.append(argv[start])
            start += 1
        break
    config_values = []
    for argv in effective.values():
        for index, argument in enumerate(argv[:-1]):
            if argument in {"--config", "--mcp-config", "--additional-mcp-config"}:
                config_values.append((argument, argv[index + 1]))
    return {
        "available_tools": available,
        "autoapprove_tools": "--allow-all-tools" in all_argv,
        "all_paths": any(arg.split("=", 1)[0] in {"--allow-all", "--allow-all-paths"} for arg in all_argv),
        "all_urls": any(arg.split("=", 1)[0] in {"--allow-all", "--allow-all-urls"} for arg in all_argv),
        "personal_config": any(flag != "--additional-mcp-config" or not value.startswith("@")
                                or not value.endswith("mcp.json") for flag, value in config_values),
        "builtin_mcps": "--disable-builtin-mcps" not in all_argv,
        "os_isolation": False,
        "effective_argv_sha256": {arm: _hash(argv) for arm, argv in effective.items()},
    }


def _environment(directory: Path) -> dict[str, str]:
    # No provider configuration, personal homes, auth caches or blanket inheritance.
    env = {key: os.environ[key] for key in ("SYSTEMROOT", "WINDIR", "COMSPEC", "PATHEXT", "PATH")
           if key in os.environ}
    for name, subdir in (("COPILOT_HOME", "copilot-home"), ("HOME", "home"),
                         ("USERPROFILE", "home"), ("APPDATA", "appdata"),
                         ("LOCALAPPDATA", "localappdata"), ("TEMP", "scratch"), ("TMP", "scratch")):
        target = directory / subdir
        target.mkdir(exist_ok=True)
        env[name] = str(target)
    env["PYTHONIOENCODING"] = "utf-8"
    return env


def _prompt(arm: str, config: dict) -> str:
    if arm == "filesystem":
        return PROMPT
    if arm == "cli":
        command = config.get("tool", {}).get("command", [])
        return PROMPT + (
            "Specialist CLI command argv: " + json.dumps(command, ensure_ascii=False)
            + ". Resource folder: ./corpus. Consult its --help for supported read/search commands. "
            "Do not assume unsupported terminology or operational commands exist."
        )
    return PROMPT + (
        "Specialist FHIR MCP server: fhir. Only the configured pinned tools are available. "
        "Its resource folder is ./corpus. Use the common public terminology assets "
        "for operations not natively supported by the tool."
    )


def prepare_argv(config: dict, arm: str, directory: Path) -> list[str]:
    work = directory / "workspace"
    argv = config["copilot_command"] + [
        "-C", str(work), "-p", _prompt(arm, config), "--model", config["model"],
        "--output-format", "json", "--stream", "off",
        "--usage-output-file", str(directory / "usage.json"),
        "--no-custom-instructions", "--no-ask-user", "--no-auto-update",
        "--no-remote", "--no-remote-export", "--disable-builtin-mcps",
        "--disallow-temp-dir", "--log-dir", str(directory / "logs"),
        "--allow-all-tools", "--available-tools", *BUILTIN_TOOLS,
    ]
    if arm == "mcp" and "tool" in config:
        names = config["tool"]["mcp_tools"]
        argv.extend("fhir-" + name for name in names)
        argv += ["--additional-mcp-config", "@" + str(directory / "mcp.json")]
    argv += ["--secret-env-vars", *config["auth_env"],
             "--max-ai-credits", str(config["max_ai_credits"])]
    if config.get("reasoning_effort"):
        argv += ["--reasoning-effort", config["reasoning_effort"]]
    return argv


def _fake_command(path: Path) -> list[str]:
    _check_links(path)
    if not path.is_file() or path.suffix.lower() != ".py":
        raise ValueError("fake-process injection requires an explicitly supplied trusted .py fixture")
    return [sys.executable, "-I", "-B", str(path.resolve())]


def _probe_mcp(command: list[str], directory: Path, config: dict, env: dict) -> dict:
    state: dict[str, Any] = {"initialized": False, "tools": None}

    def reply(line, process):
        row = json.loads(line)
        if row.get("jsonrpc") != "2.0" or row.get("error"):
            raise ValueError("invalid MCP JSON-RPC reply")
        if row.get("id") == 1:
            result = row["result"]
            if (state["initialized"] or result["protocolVersion"] != "2024-11-05"
                    or not isinstance(result["serverInfo"], dict)
                    or not isinstance(result["capabilities"], dict)):
                raise ValueError("invalid MCP initialize result")
            state["initialized"] = True
            process.stdin.write(b'{"jsonrpc":"2.0","method":"notifications/initialized"}\n'
                                b'{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}\n')
            process.stdin.flush()
        elif row.get("id") == 2:
            if not state["initialized"] or state["tools"] is not None:
                raise ValueError("out of order MCP tools/list")
            result = row["result"]
            if result.get("nextCursor"):
                raise ValueError("paginated tools/list is not supported by this offline gate")
            names = [tool["name"] for tool in result["tools"]]
            if any(not isinstance(name, str) for name in names) or len(set(names)) != len(names):
                raise ValueError("invalid MCP tool names")
            state["tools"] = sorted(names)
            process.stdin.close()

    result = capture_process(command + ["mcp", str(directory / "corpus")],
                             directory, env, config["wall_timeout_seconds"],
                             config["max_trace_bytes"], reply)
    (directory / "mcp-stdout.jsonl").write_bytes(result.stdout)
    (directory / "mcp-stderr.txt").write_bytes(result.stderr)
    expected = sorted(config["tool"]["mcp_tools"])
    return {
        "mode": "offline-fake-process", "tools": state["tools"],
        "success": result.failure is None and result.exit_code == 0 and state["tools"] == expected,
        "exit_code": result.exit_code, "failure": result.failure,
        "errors": list(result.errors),
        "cli_search_behavior_verified": False,
        "real_tool_verified": False,
    }


def _read_regular(path: Path, limit: int) -> bytes:
    _check_links(path)
    info = path.stat()
    if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1:
        raise ValueError("not a single-link regular artifact")
    if info.st_size > limit:
        raise OverflowError("artifact exceeds byte limit")
    # All fake descendants have been stopped before collection. Live collection
    # requires a real read-only boundary and is not enabled by this check.
    with path.open("rb") as stream:
        data = stream.read(limit + 1)
    if len(data) > limit:
        raise OverflowError("artifact exceeds byte limit")
    return data


def _collect(row: dict, process, question, gold, corpus, root: Path, config: dict,
             source_guard: dict[str, str], workspace_guard: dict[str, str]) -> None:
    directory = Path(row["directory"])
    work = Path(row["workspace"])
    (directory / "stdout.jsonl").write_bytes(process.stdout)
    (directory / "stderr.txt").write_bytes(process.stderr)
    row.update(exit_code=process.exit_code, elapsed_seconds=process.elapsed_seconds,
               trace_truncated=process.trace_truncated)
    row["errors"].extend(process.errors)
    row["status"] = process.failure or ("process_failed" if process.exit_code != 0 else "completed")
    try:
        unchanged = (_protected_inventory(work, {"answer.json"}) == workspace_guard
                     and _protected_inventory(root) == source_guard)
        corpus.assert_unchanged()
        verify_dataset_manifest(root)
    except (OSError, ValueError, OverflowError) as error:
        unchanged = False
        row["errors"].append(f"integrity check: {error}")
    row["corpus_unchanged"] = unchanged
    if not unchanged:
        row["status"] = "data_mutation"
    events = []
    trace_invalid = False
    terminal_seen_in_trace = False
    for line in process.stdout.splitlines():
        try:
            event = json.loads(line)
            if isinstance(event, dict):
                events.append(event)
                terminal_seen_in_trace = terminal_seen_in_trace or event.get("type") == "session.idle"
            else:
                trace_invalid = True
        except RecursionError:
            trace_invalid = True
        except (ValueError, UnicodeError):
            if terminal_seen_in_trace:
                trace_invalid = True
            continue  # Non-JSON trace is preserved, never interpreted as an answer.
    lifecycle_types = {
        "session.idle", "session.error", "tool.execution_start", "tool.execution_complete",
        "assistant.message", "session.start",
    }
    recognized = [event for event in events if event.get("type") in lifecycle_types]
    row["tool_calls"] = sum(event["type"] == "tool.execution_start" for event in recognized) if recognized else None
    terminal_activity = False
    terminal_seen = False
    for event in events:
        event_type = event.get("type")
        if event_type in lifecycle_types:
            if terminal_seen:
                terminal_activity = True
            if event_type == "session.idle":
                terminal_seen = True
                terminal_activity = False
        elif terminal_seen and event_type != "telemetry":
            terminal_activity = True
    if row["status"] == "completed":
        if trace_invalid:
            row["status"] = "invalid_trace"
            row["errors"].append("trace contains a non-object or over-nested JSON record")
        elif any(event.get("type") == "session.error" for event in recognized):
            row["status"] = "session_failed"
        elif not recognized or not any(event.get("type") == "session.idle" for event in recognized):
            row["status"] = "missing_terminal_event"
        elif recognized[-1].get("type") != "session.idle" or terminal_activity:
            row["status"] = "invalid_lifecycle"
        else:
            session_ids = []
            for event in recognized:
                data = event.get("data")
                identifier = event.get("sessionId")
                if isinstance(data, dict):
                    identifier = data.get("sessionId", identifier)
                if identifier is not None:
                    session_ids.append(identifier)
            if any(not isinstance(identifier, str) or not identifier for identifier in session_ids) or len(set(session_ids)) > 1:
                row["status"] = "invalid_lifecycle"
    try:
        raw_usage = _read_regular(directory / "usage.json", config["max_answer_bytes"])
        usage = json.loads(raw_usage)
        if not isinstance(usage, dict):
            raise ValueError("usage must be an object")
        row["usage"] = usage
    except FileNotFoundError:
        pass  # Unknown is distinct from zero.
    except (OSError, ValueError, OverflowError, UnicodeError, RecursionError) as error:
        row["errors"].append(f"invalid usage: {error}")
        if row["status"] == "completed":
            row["status"] = "invalid_usage"
    if row["status"] != "completed":
        return
    try:
        raw = _read_regular(work / "answer.json", config["max_answer_bytes"])
        response = AgentResponse.from_dict(json.loads(raw))
        grade = grade_response(question, gold, response, corpus, root)
        row["grade"] = grade.as_dict()
        row["strict_success"] = grade.strict_success
        if not grade.strict_success:
            row["status"] = "answer_failed"
    except FileNotFoundError:
        row["status"] = "missing_answer"
    except OverflowError:
        row["status"] = "answer_overflow"
    except (ValueError, UnicodeError, RecursionError) as error:
        row["errors"].append(str(error))
        row["status"] = "unsafe_answer" if isinstance(error, ValueError) and (
            "link" in str(error) or "reparse" in str(error)) else "invalid_response"
    except OSError as error:
        row["status"] = "unsafe_answer"
        row["errors"].append(str(error))


def run_benchmark(config_path: Path, output: Path, data_root: Path, *,
                  execute: bool = False, approve_total_credits: float | None = None,
                  fake_process: Path | None = None, fake_tool_process: Path | None = None) -> dict:
    config = read_config(config_path)
    if execute and fake_process:
        raise ValueError("--execute and --fake-process are mutually exclusive")
    if fake_tool_process and not fake_process:
        raise ValueError("--fake-tool-process requires --fake-process")
    if fake_tool_process and "tool" not in config:
        raise ValueError("--fake-tool-process requires configured tool")
    fake = _fake_command(fake_process) if fake_process else None
    fake_tool = _fake_command(fake_tool_process) if fake_tool_process else None
    verify_dataset_manifest(data_root)
    source_guard = _protected_inventory(data_root)
    available_questions = {question.id: question for question in load_questions(data_root / "questions.jsonl")}
    missing_initial = set(INITIAL_20_QUESTION_IDS) - available_questions.keys()
    if missing_initial:
        raise ValueError(f"initial20 question set is incomplete: {sorted(missing_initial)}")
    requested = config.get("question_ids", list(INITIAL_20_QUESTION_IDS))
    outside_initial = set(requested) - set(INITIAL_20_QUESTION_IDS)
    if outside_initial:
        raise ValueError(f"runner gate supports only the pinned initial20 IDs: {sorted(outside_initial)}")
    questions = [available_questions[question_id] for question_id in requested]
    for question in questions:
        actual_hash = _hash(question.as_dict())
        if actual_hash != INITIAL_20_QUESTION_SHA256[question.id]:
            raise ValueError(f"pinned initial20 question hash mismatch: {question.id}")
    corpus = load_corpus(data_root / "agent" / "corpus-manifest.json")
    gold = {q.id: derive_gold(q, corpus, data_root) for q in questions}
    planned = len(questions) * len(config["arms"])
    total_credits = planned * config["max_ai_credits"]
    if execute and (approve_total_credits is None or approve_total_credits != total_credits):
        raise ValueError(f"--execute requires --approve-total-credits exactly {total_credits}")
    if approve_total_credits is not None and not execute:
        raise ValueError("--approve-total-credits is only valid with --execute")
    _check_links(output.absolute())
    output = output.resolve()
    if output.exists() and (not output.is_dir() or any(output.iterdir())):
        raise ValueError(f"refusing non-empty run output: {output}")
    for protected in (data_root.resolve(), Path(__file__).parent.resolve()):
        if output == protected or output.is_relative_to(protected):
            raise ValueError("run output must not be inside source/private data")
    output.mkdir(parents=True, exist_ok=True)
    mode = "offline-fake-process" if fake else "live-blocked" if execute else "dry-run"
    tool = executable_identity(config["tool"]["command"]) if "tool" in config else None
    if tool:
        tool.update(expected_revision=config["tool"]["expected_revision"],
                    revision_status="unverified-declaration")
    workspace_guards: dict[str, dict[str, str]] = {}
    snapshot = {
        "schema_version": 1,
        "runtime": {"python": platform.python_version(), "system": platform.system(),
                    "release": platform.release(), "machine": platform.machine()},
        "source_sha256": {path.name: _file_hash(path) for path in sorted(Path(__file__).parent.glob("*.py"))},
        "config_sha256": _hash(config),
        "config": config,
        "prompt_sha256": {arm: _hash(_prompt(arm, config)) for arm in config["arms"]},
        "permissions": _effective_permissions(config),
        "copilot": executable_identity(config["copilot_command"]),
        "expected_copilot_version": config["copilot_version"],
        "actual_copilot_version": None,
        "copilot_version_status": "unverified; no real executable preflight",
        "requested_model": config["model"], "actual_model_version": None,
        "inference_settings": {"reasoning_effort": config.get("reasoning_effort"), "repetitions": 1},
        "budgets": {key: config[key] for key in ("max_ai_credits", "wall_timeout_seconds",
                                               "max_answer_bytes", "max_trace_bytes")},
        "tool": tool,
        "question_sha256": {q.id: _hash(q.as_dict()) for q in questions},
        "gold_sha256": {q.id: _hash(gold[q.id].as_dict()) for q in questions},
        "corpus_hash": corpus.corpus_hash,
        "dataset_assets_sha256": {name: digest for name, digest in source_guard.items()
                                   if not name.endswith("/")},
        "mode": mode, "exposure": "adaptive-engineering",
        "fake_process_sha256": _file_hash(fake_process) if fake_process else None,
        "fake_tool_sha256": _file_hash(fake_tool_process) if fake_tool_process else None,
        "fake_command": fake,
        "fake_tool_command": fake_tool,
        "fake_external_dependencies": "unverified; fixture results cannot be reused as benchmark evidence",
    }
    report = {
        "schema_version": 1, "mode": mode, "measured_agent_performance": False,
        "snapshot": snapshot, "snapshot_id": _hash(snapshot),
        "result_reuse_policy": "exact-snapshot-only; no historical pooling",
        "acceptance": "20 provisional synthetic engineering items; not clinical acceptance",
        "containment": {"verified": False, "live_submission_enabled": False,
                        "reason": "no verified descendant/read-only/private-data/network boundary implemented"},
        "budget": {"per_episode_credit_soft_cap": config["max_ai_credits"],
                   "total_credit_soft_cap": total_credits,
                   "wall_timeout_seconds": config["wall_timeout_seconds"],
                   "pricing_usd": None, "hard_currency_limit": False,
                   "warning": "credit soft cap may overshoot; no token/turn/currency guarantee"},
        "preflight": {"mode": mode, "real_tool_verified": False,
                      "copilot_version": None, "mcp": None},
        "availability": {
            "copilot_executable_found": snapshot["copilot"]["available"],
            "specialist_executable_found": tool["available"] if tool else False,
            "specialist_configured": tool is not None,
            "specialist_behavior_verified": False,
            "tool_revision_verified": False,
            "tool_name_integration_verified": False,
        },
        "episodes": [],
    }
    for question in questions:
        for arm in config["arms"]:
            directory = output / "episodes" / question.id / arm
            directory.mkdir(parents=True)
            work = directory / "workspace"
            prepare_question(question, corpus, data_root, work)
            public_question = {key: value for key, value in question.as_dict().items() if key in {
                "id", "cutoff", "corpus_id", "protocol_id", "selection_policy",
                "answer_type", "prompt", "parameters",
            }}
            _write_json(work / "question.json", public_question)
            manifest = json.loads((work / "prepared-manifest.json").read_text(encoding="utf-8"))
            manifest["question_sha256"] = _hash(public_question)
            manifest["export_kind"] = "runner public question package; evaluation labels omitted"
            _write_json(work / "prepared-manifest.json", manifest)
            workspace_guards[str(directory)] = _protected_inventory(work, {"answer.json"})
            env = _environment(directory)
            _write_json(directory / "copilot-home" / "config.json", {"mcpServers": {}})
            if arm == "mcp" and "tool" in config:
                command = config["tool"]["command"]
                _write_json(directory / "mcp.json", {"mcpServers": {"fhir": {
                    "type": "stdio", "command": command[0],
                    "args": command[1:] + ["mcp", str(work / "corpus")],
                    "tools": config["tool"]["mcp_tools"],
                    "env": {name: env[name] for name in ("HOME", "USERPROFILE", "TEMP", "TMP")},
                }}})
            row = {
                "question_id": question.id, "arm": arm, "directory": str(directory),
                "workspace": str(work), "config_home": env["COPILOT_HOME"],
                "copilot_argv": prepare_argv(config, arm, directory),
                "status": "not_run", "errors": [], "strict_success": False,
                "exit_code": None, "elapsed_seconds": None, "usage": None, "pricing_usd": None,
                "tool_calls": None, "trace_truncated": False, "corpus_unchanged": None,
                "native_capability": "generic-scripting" if arm == "filesystem" else "unverified",
                "permitted_fallback": "generic-scripting",
                "snapshot_id": report["snapshot_id"],
            }
            report["episodes"].append(row)
    if fake:
        probe = output / "preflight"
        probe.mkdir()
        env = _environment(probe)
        try:
            version = capture_process(fake + ["--version"], probe, env,
                                      config["wall_timeout_seconds"], config["max_trace_bytes"])
            (probe / "version-stdout.txt").write_bytes(version.stdout)
            (probe / "version-stderr.txt").write_bytes(version.stderr)
            report["preflight"]["version_probe"] = {
                "exit_code": version.exit_code, "elapsed_seconds": version.elapsed_seconds,
                "failure": version.failure, "trace_truncated": version.trace_truncated,
                "errors": list(version.errors),
            }
            if version.failure or version.exit_code:
                raise ValueError("fake executable version preflight failed")
            report["preflight"]["copilot_version"] = version.stdout.decode("utf-8").strip()
        except (OSError, ValueError, UnicodeError, subprocess.SubprocessError, RecursionError) as error:
            report["preflight"]["error"] = str(error)
        if fake_tool and "tool" in config and not report["preflight"].get("error"):
            try:
                report["preflight"]["mcp"] = _probe_mcp(fake_tool, probe, config, env)
            except (OSError, ValueError, UnicodeError, subprocess.SubprocessError, RecursionError) as error:
                report["preflight"]["mcp_error"] = str(error)
                report["preflight"]["mcp"] = {"mode": "offline-fake-process", "success": False,
                                               "failure": "startup_error", "errors": [str(error)],
                                               "real_tool_verified": False}
    integrity_stop = False
    cleanup_stop = False
    for row in report["episodes"]:
        if not execute and not fake:
            continue
        if integrity_stop:
            row["status"] = "blocked_integrity"
            continue
        if cleanup_stop:
            row["status"] = "blocked_cleanup"
            continue
        arm = row["arm"]
        directory = Path(row["directory"])
        work = Path(row["workspace"])
        try:
            source_matches = _protected_inventory(data_root) == source_guard
            workspace_matches = _protected_inventory(work, {"answer.json"}) == workspace_guards[str(directory)]
        except (OSError, ValueError, OverflowError) as error:
            source_matches = workspace_matches = False
            row["errors"].append(f"pre-dispatch integrity check: {error}")
        if not source_matches or not workspace_matches:
            row["status"] = "blocked_integrity"
            row["errors"].append("protected source or prepared workspace changed before dispatch")
            integrity_stop = True
            continue
        if fake:
            if report["preflight"].get("error"):
                row["status"] = "blocked_preflight"
                row["errors"].append(report["preflight"]["error"])
                continue
            if arm != "filesystem" and not (
                report["preflight"]["mcp"] and report["preflight"]["mcp"]["success"]
            ):
                row["status"] = "blocked_tool"
                row["errors"].append(report["preflight"].get("mcp_error") or
                                     "explicit offline specialist fixture preflight absent or failed; no fallback")
                continue
            env = _environment(directory)
            try:
                # Test-only substitution, never a fallback for the configured real executable.
                argv = fake + row["copilot_argv"][len(config["copilot_command"]):]
                process = capture_process(argv, work, env, config["wall_timeout_seconds"],
                                          config["max_trace_bytes"])
                if process.failure == "cleanup_failed":
                    (directory / "stdout.jsonl").write_bytes(process.stdout)
                    (directory / "stderr.txt").write_bytes(process.stderr)
                    row.update(status="cleanup_failed", exit_code=process.exit_code,
                               elapsed_seconds=process.elapsed_seconds,
                               trace_truncated=process.trace_truncated)
                    row["errors"].extend(process.errors)
                    cleanup_stop = True
                else:
                    question = next(q for q in questions if q.id == row["question_id"])
                    _collect(row, process, question, gold[question.id], corpus, data_root, config,
                             source_guard, workspace_guards[str(directory)])
            except (OSError, ValueError, subprocess.SubprocessError, RecursionError) as error:
                row["status"] = "infrastructure_failed"
                row["errors"].append(str(error))
            integrity_stop = row["status"] == "data_mutation"
        else:
            blockers = ["no verified process containment; live launch not implemented"]
            if not any(os.environ.get(name) for name in config["auth_env"]):
                blockers.append("missing auth: set one of the configured auth_env names")
            if arm != "filesystem":
                blockers.append("real specialist behavior/MCP preflight not verified")
            row["errors"] = blockers
            row["status"] = "blocked_auth" if len(blockers) > 1 and blockers[1].startswith("missing auth") else "blocked_containment"
    counts = Counter(row["status"] for row in report["episodes"])
    report["counts"] = {
        "planned": planned, "completed": counts["completed"],
        "failed": sum(count for status, count in counts.items()
                      if status not in {"completed", "not_run"} and not status.startswith("blocked_")),
        "blocked": sum(count for status, count in counts.items() if status.startswith("blocked_")),
        "not_run": counts["not_run"],
        "strict_success": sum(row["strict_success"] for row in report["episodes"]),
    }
    report["by_arm"] = {}
    for arm in config["arms"]:
        rows = [row for row in report["episodes"] if row["arm"] == arm]
        report["by_arm"][arm] = {
            "planned": len(rows), "status_counts": dict(Counter(row["status"] for row in rows)),
            "strict_success": sum(row["strict_success"] for row in rows),
        }
    _write_json(output / "report.json", report)
    lines = [
        "# FHIR runner engineering report", "",
        f"Mode: **{mode}**; not model performance. No real inference was submitted.",
        "Offline fake checks do not prove isolation or clinical acceptance.", "",
        f"Snapshot: `{report['snapshot_id']}`",
        "Result reuse: exact snapshot only; no historical pooling.", "",
        f"Planned: {planned}; completed: {report['counts']['completed']}; "
        f"failed: {report['counts']['failed']}; blocked: {report['counts']['blocked']}; "
        f"not run: {report['counts']['not_run']}.",
        f"Engineering strict successes / all planned slots: {report['counts']['strict_success']}/{planned}.",
        "Unknown usage/pricing/tool counts remain null, not zero.", "",
        "| Question | Arm | Status | Strict engineering success |",
        "|---|---|---|---|",
    ]
    lines.extend(f"| {r['question_id']} | {r['arm']} | {r['status']} | {r['strict_success']} |"
                 for r in report["episodes"])
    (output / "report.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    return report
