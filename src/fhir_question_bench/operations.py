"""Private offline recomputation and whole-output grading for 50 operations.

Agent-visible material is exclusively operations/agent. This module and the
private expected tree must NOT be mounted into an eventual execution boundary.
There is intentionally no live tool/agent execution interface.
"""
from __future__ import annotations

import copy
import fnmatch
import hashlib
import json
import re
import shutil
import stat
from collections import Counter
from datetime import datetime, timezone
from decimal import Decimal
from pathlib import Path
from typing import Any

from . import operations_reference as reference


OPERATIONS_ROOT = Path(__file__).parent / "data" / "operations"
CATEGORIES = frozenset({"deidentification", "mapping", "view", "validation", "mixed"})
MAX_OUTPUT_BYTES = 8 * 1024 * 1024


def canonical(value: Any) -> bytes:
    """Canonical JSON without rounding decimal-valued FHIR/tabular numbers."""
    def encode(item: Any) -> str:
        if isinstance(item, Decimal):
            if not item.is_finite():
                raise ValueError("non-finite JSON decimal")
            return format(item, "f")
        if isinstance(item, dict):
            return "{" + ",".join(encode(k) + ":" + encode(v) for k, v in sorted(item.items())) + "}"
        if isinstance(item, list):
            return "[" + ",".join(encode(v) for v in item) + "]"
        return json.dumps(item, ensure_ascii=False, allow_nan=False, separators=(",", ":"))
    return encode(value).encode("utf-8")


def _pairs(items: list[tuple[str, Any]]) -> dict:
    result = {}
    for key, value in items:
        if key in result:
            raise ValueError(f"duplicate JSON key: {key}")
        result[key] = value
    return result


def read_json(path: Path) -> Any:
    def invalid_constant(value: str) -> None:
        raise ValueError(f"non-finite JSON number: {value}")
    return json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=_pairs,
                      parse_float=Decimal, parse_constant=invalid_constant)


def _relative(name: str) -> str:
    if (not isinstance(name, str) or not name or "\\" in name or ":" in name
            or any(part in {"", ".", ".."} or part.endswith((" ", ".")) for part in name.split("/"))
            or name.startswith("/")):
        raise ValueError(f"unsafe artifact path: {name!r}")
    return name


def _regular(path: Path) -> None:
    info = path.lstat()
    if (path.is_symlink() or getattr(info, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT
            or not stat.S_ISREG(info.st_mode) or info.st_nlink != 1):
        raise ValueError(f"artifact must be a regular single-link file: {path.name}")


def _asset(root: Path, name: str) -> Path:
    _relative(name)
    path = root
    for part in name.split("/"):
        path /= part
        info = path.lstat()
        if path.is_symlink() or getattr(info, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
            raise ValueError(f"link/reparse point in asset path: {name}")
    _regular(path)
    return path


def load_tasks(root: Path | str = OPERATIONS_ROOT) -> list[dict]:
    tasks = read_json(_asset(Path(root), "agent/tasks.json"))
    if not isinstance(tasks, list) or len(tasks) != 50:
        raise ValueError("operations requires exactly 50 tasks")
    seen = set()
    for task in tasks:
        required = {"id", "version", "category", "tier", "title", "prompt", "verification_status",
                    "capabilities_advantaged", "inputs", "steps", "contract", "provenance"}
        if not isinstance(task, dict) or set(task) != required:
            raise ValueError("invalid operational task fields")
        if task["id"] in seen or not re.fullmatch(r"[a-z]+-\d{2}", task["id"]):
            raise ValueError("invalid or duplicate operational task ID")
        seen.add(task["id"])
        if (task["version"] != 1 or task["verification_status"] != "unverified"
                or task["category"] not in CATEGORIES or type(task["tier"]) is not int
                or task["tier"] not in range(1, 6)):
            raise ValueError("invalid task version, category, tier, or verification state")
        if not task["prompt"] or not task["capabilities_advantaged"] or not task["inputs"]:
            raise ValueError("task requires prompt, capabilities and inputs")
        for name in task["inputs"]:
            _relative(name)
        contract = task["contract"]
        if (contract["version"] != 1 or contract["id_policy"] != "exact"
                or contract["timestamp_tolerance_seconds"] != 0
                or contract["permitted_extras"] != [] or contract["partial_success"] is not False):
            raise ValueError("unsupported whole-output contract")
        if not contract["inventory"]:
            raise ValueError("empty output inventory")
        if set(contract["input_sha256"]) != set(task["inputs"]):
            raise ValueError("input hash inventory differs from declared inputs")
        for name in contract["inventory"]:
            _relative(name)
    return tasks


def _resources(document: Any) -> list[dict]:
    if isinstance(document, list):
        return document
    if document.get("resourceType") == "Bundle":
        return [entry["resource"] for entry in document.get("entry", [])]
    return [document]


def recompute_task(task: dict, root: Path | str = OPERATIONS_ROOT) -> dict[str, Any]:
    """Run deterministic trusted reference steps, in memory, against public inputs."""
    public = Path(root) / "agent"
    available = {}
    for name in task["inputs"]:
        path = _asset(public, name)
        if hashlib.sha256(path.read_bytes()).hexdigest() != task["contract"]["input_sha256"][name]:
            raise ValueError(f"operations input hash mismatch: {name}")
        available[name] = read_json(path)
    produced = {}
    for step in task["steps"]:
        kind = step["kind"]
        source = available[step["input"]]
        if kind == "deidentify":
            value = reference.deidentify(source, available[step["policy"]])
        elif kind == "map":
            value = reference.map_resource(source, available[step["map"]])
        elif kind == "view":
            value = reference.view_rows(_resources(source), available[step["view"]])
        elif kind == "validate":
            term = available[step["terminology"]] if "terminology" in step else {}
            value = reference.validate(source, available[step["profile"]], term,
                                       resources=_resources(available[step["folder"]]) if "folder" in step else None,
                                       check_references=step.get("check_references", False))
        elif kind in {"translate", "validate-code", "expand"}:
            function = {"translate": reference.translate, "validate-code": reference.validate_code,
                        "expand": reference.expand}[kind]
            value = function(source, available[step["terminology"]])
        else:
            raise ValueError(f"unsupported operation: {kind}")
        available[step["output"]] = value
        if step["output"] in task["contract"]["inventory"]:
            produced[step["output"]] = value
    if set(produced) != set(task["contract"]["inventory"]):
        raise ValueError(f"incomplete reference output inventory: {task['id']}")
    return produced


def load_expected(task: dict, root: Path | str = OPERATIONS_ROOT) -> dict[str, Any]:
    private = Path(root) / "private" / "expected" / task["id"]
    return {name: read_json(_asset(private, name)) for name in task["contract"]["inventory"]}


def _matches(path: str, patterns: list[str]) -> bool:
    # '*' is exactly one JSON-pointer segment, not an unbounded fnmatch wildcard.
    return any(len(path.split("/")) == len(pattern.split("/"))
               and all(fnmatch.fnmatchcase(part, wanted) for part, wanted in
                       zip(path.split("/"), pattern.split("/"))) for pattern in patterns)


def _normalized(value: Any, contract: dict, path: str = "") -> Any:
    if isinstance(value, dict):
        members = []
        for key, child in sorted(value.items()):
            child_path = path + "/" + key.replace("~", "~0").replace("/", "~1")
            if _matches(child_path, contract["diagnostic_text_paths"]):
                if not isinstance(child, str):
                    raise ValueError("diagnostic wording must be text")
                continue
            if (isinstance(child, dict) and set(child) == {"text"}
                    and _matches(child_path + "/text", contract["diagnostic_text_paths"])):
                if not isinstance(child["text"], str):
                    raise ValueError("diagnostic wording must be text")
                continue
            members.append((key, _normalized(child, contract, child_path)))
        return ("object", tuple(members))
    if isinstance(value, list):
        items = [_normalized(child, contract, path + f"/{index}") for index, child in enumerate(value)]
        if _matches(path, contract["unordered_arrays"]):
            items.sort(key=repr)
        return ("array", tuple(items))
    if isinstance(value, str):
        if _matches(path, contract["timestamp_paths"]):
            parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
            if parsed.tzinfo is None:
                raise ValueError("timestamp requires timezone")
            return ("timestamp", parsed.astimezone(timezone.utc).isoformat())
        return ("string", value)
    if value is None:
        return ("null",)
    if type(value) is bool:
        return ("boolean", value)
    if type(value) in {int, float, Decimal}:
        number = Decimal(str(value))
        if not number.is_finite():
            raise ValueError("non-finite number")
        return ("number", number)
    raise ValueError(f"unsupported artifact JSON value: {type(value).__name__}")


def grade_artifacts(task: dict, expected: dict, actual: dict) -> dict:
    """Compare complete inventory, values, multiplicity and preservation obligations.

    Exact whole-value comparison enforces required/forbidden content and conserved
    fields/relationships. Only explicitly named arrays and timestamps normalize;
    diagnostics text is optional, but issue code/severity/location/details are not.
    """
    errors = []
    try:
        if not isinstance(actual, dict):
            raise ValueError("output inventory must be an object")
        for name in actual:
            _relative(name)
        inventory = set(task["contract"]["inventory"])
        if set(expected) != inventory:
            raise ValueError("private gold inventory differs from contract")
        if set(actual) != inventory:
            errors.append("output inventory differs: missing/extra artifacts (including logs/copies)")
        for name in inventory & actual.keys():
            if _normalized(expected[name], task["contract"]) != _normalized(actual[name], task["contract"]):
                errors.append(f"whole-output mismatch: {name}")
    except (ValueError, TypeError, KeyError, RecursionError) as error:
        errors.append(str(error))
    return {"task_id": task["id"], "contract_version": task["contract"]["version"],
            "strict_success": not errors, "errors": errors}


def grade_output_tree(task: dict, expected: dict, output: Path | str) -> dict:
    """Collect/hash EVERY output file; reject links, extras and noncanonical paths.

    Trusted offline collection only: this is not race-free hostile-process
    containment. Live collection requires a quiesced enforced boundary first.
    """
    output = Path(output)
    hashes, actual = {}, {}
    try:
        root_info = output.lstat()
        if (output.is_symlink() or not output.is_dir()
                or getattr(root_info, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT):
            raise ValueError("output root must be a non-link directory")
        allowed_dirs = set()
        for name in task["contract"]["inventory"]:
            parent = Path(name).parent
            while parent != Path("."):
                allowed_dirs.add(parent.as_posix())
                parent = parent.parent
        total = 0
        # Do not follow directory links, including Windows junctions.
        pending = [output]
        inventory_errors = []
        while pending:
            directory = pending.pop()
            for path in sorted(directory.iterdir()):
                relative = path.relative_to(output).as_posix()
                _relative(relative)
                info = path.lstat()
                if path.is_symlink() or getattr(info, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
                    raise ValueError(f"output link/reparse point: {relative}")
                if path.is_dir():
                    if relative not in allowed_dirs:
                        inventory_errors.append(f"extra output directory: {relative}")
                    pending.append(path)
                    continue
                _regular(path)
                total += info.st_size
                if total > MAX_OUTPUT_BYTES:
                    raise ValueError("output byte budget exceeded")
                raw = path.read_bytes()
                hashes[relative] = hashlib.sha256(raw).hexdigest()
                if relative in task["contract"]["inventory"]:
                    actual[relative] = read_json(path)
                else:
                    inventory_errors.append(f"extra output file: {relative}")
        result = grade_artifacts(task, expected, actual)
        result["errors"].extend(inventory_errors)
        result["strict_success"] = not result["errors"]
    except (OSError, ValueError, RecursionError) as error:
        result = {"task_id": task["id"], "contract_version": task["contract"]["version"],
                  "strict_success": False, "errors": [str(error)]}
    result["output_sha256"] = hashes
    return result


def wrong_outputs(task: dict, expected: dict) -> dict[str, dict]:
    """Construct bad inventories independently of reference execution.

    Includes plausible category-specific semantic mistakes, incomplete outputs,
    multiplicity errors and the sanitized-primary-plus-original-copy attack.
    """
    first = next(iter(expected))
    missing = copy.deepcopy(expected)
    del missing[first]
    extra = {**copy.deepcopy(expected), "unaltered-copy.json": copy.deepcopy(expected[first])}
    partial = copy.deepcopy(expected)
    partial[first] = {"status": "partial", "result": partial[first]}
    wrong = copy.deepcopy(expected)
    value = wrong[first]
    if "rows" in value:
        if value["rows"]:
            key = next(iter(value["rows"][0]))
            value["rows"][0][key] = "__wrong__"
        else:
            value["rows"].append({c["name"]: None for c in value["columns"]})
    elif value.get("resourceType") == "OperationOutcome":
        value["issue"][0]["severity"] = "fatal" if value["issue"][0]["severity"] != "fatal" else "warning"
    elif value.get("resourceType") == "Parameters":
        value["parameter"][0]["valueBoolean"] = not value["parameter"][0]["valueBoolean"]
    elif value.get("resourceType") == "ValueSet":
        value["expansion"]["total"] += 1
    elif task["category"] == "deidentification":
        target = value["entry"][0]["resource"] if value.get("resourceType") == "Bundle" else value
        target["name"] = [{"text": "Synthetic forbidden retained name"}]
    else:
        fields = [key for key in value if key != "resourceType"]
        if fields:
            del value[fields[-1]]
        else:
            value["unauthorized"] = True
    duplicate = copy.deepcopy(expected)
    item = duplicate[first]
    arrays = [item[key] for key in ("rows", "issue", "entry", "parameter")
              if isinstance(item.get(key), list) and item[key]]
    if arrays:
        arrays[0].append(copy.deepcopy(arrays[0][0]))
    else:
        item["unauthorized_duplicate"] = [copy.deepcopy(item)]
    bad_id = copy.deepcopy(expected)
    bad_id[first]["id"] = "unauthorized-generated-id"
    return {"extra_file": extra, "missing_file": missing, "wrong_value": wrong, "partial": partial,
            "duplicate": duplicate, "wrong_id": bad_id}


def verify_manifest(root: Path | str = OPERATIONS_ROOT) -> str:
    root = Path(root)
    manifest = read_json(_asset(root, "operations-manifest.json"))
    if manifest["version"] != 1 or manifest["task_count"] != 50:
        raise ValueError("unsupported operations manifest")
    entries = manifest["files"]
    seen = set()
    for entry in entries:
        name = _relative(entry["path"])
        if name in seen:
            raise ValueError("duplicate operations manifest path")
        seen.add(name)
        digest = hashlib.sha256(_asset(root, name).read_bytes()).hexdigest()
        if digest != entry["sha256"]:
            raise ValueError(f"operations hash mismatch: {name}")
    actual = {p.relative_to(root).as_posix() for p in root.rglob("*") if not p.is_dir()}
    if actual != seen | {"operations-manifest.json"}:
        raise ValueError("operations manifest inventory mismatch")
    for filename, field in (("operations_reference.py", "reference_sha256"), ("operations.py", "grader_sha256")):
        if hashlib.sha256(Path(__file__).with_name(filename).read_bytes()).hexdigest() != manifest[field]:
            raise ValueError(f"operations implementation hash mismatch: {filename}")
    return hashlib.sha256(canonical(manifest)).hexdigest()


def audit_operations(root: Path | str = OPERATIONS_ROOT) -> dict:
    """Offline self-check; verification_status never changes as a side effect."""
    root = Path(root)
    digest = verify_manifest(root)
    tasks = load_tasks(root)
    recomputed = accepted = rejected = 0
    for task in tasks:
        expected = load_expected(task, root)
        actual = recompute_task(task, root)
        if actual != expected:
            raise ValueError(f"private expected output does not recompute: {task['id']}")
        recomputed += 1
        if not grade_artifacts(task, expected, actual)["strict_success"]:
            raise ValueError(f"gold rejected: {task['id']}")
        accepted += 1
        for name, wrong in wrong_outputs(task, expected).items():
            if grade_artifacts(task, expected, wrong)["strict_success"]:
                raise ValueError(f"bad output accepted: {task['id']}/{name}")
            rejected += 1
    return {"mode": "offline-reference-and-grader-self-check", "task_count": len(tasks),
            "recomputed": recomputed, "gold_accepted": accepted, "wrong_outputs_rejected": rejected,
            "category_counts": dict(sorted(Counter(t["category"] for t in tasks).items())),
            "tier_counts": dict(sorted(Counter(t["tier"] for t in tasks).items())),
            "verification_status": "unverified", "manifest_sha256": digest,
            "live_execution": "blocked", "measured_agent_performance": False}


def prepare_operation(task_id: str, output: Path | str, root: Path | str = OPERATIONS_ROOT) -> Path:
    """Export only the exact locked public task and its explicitly declared inputs."""
    root, output = Path(root), Path(output)
    verify_manifest(root)
    task = next((t for t in load_tasks(root) if t["id"] == task_id), None)
    if task is None:
        raise ValueError(f"unknown operations task: {task_id}")
    resolved = output.resolve()
    if resolved == root.resolve() or root.resolve() in resolved.parents:
        raise ValueError("preparation destination must be outside immutable operations assets")
    if output.exists() and (output.is_symlink() or not output.is_dir() or any(output.iterdir())):
        raise ValueError("refusing non-empty or linked preparation destination")
    output.mkdir(parents=True, exist_ok=True)
    for name in task["inputs"]:
        source = _asset(root / "agent", name)
        target = output / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
    (output / "task.json").write_bytes(canonical(task) + b"\n")
    return output
