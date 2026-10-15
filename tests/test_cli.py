from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path


ROOT = Path(__file__).parents[1]


def run_cli(*args: str, cwd: Path = ROOT) -> subprocess.CompletedProcess[str]:
    env = dict(os.environ)
    env["PYTHONPATH"] = str(ROOT / "src")
    return subprocess.run(
        [sys.executable, "-m", "fhir_question_bench", *args],
        cwd=cwd,
        env=env,
        text=True,
        capture_output=True,
        check=False,
    )


def test_cli_audit_prepare_recompute_and_offline_demo(tmp_path: Path) -> None:
    audit = run_cli("audit")
    assert audit.returncode == 0, audit.stderr
    assert json.loads(audit.stdout)["question_count"] == 100

    prepared = tmp_path / "prepared"
    result = run_cli("prepare", "--question-id", "direct-001", "--out", str(prepared))
    assert result.returncode == 0, result.stderr
    assert (prepared / "question.json").exists()
    assert (prepared / "corpus" / "resources.ndjson").exists()
    assert (prepared / "response-contract.json").exists()
    assert (prepared / "selection-policies.json").exists()
    assert not list(prepared.rglob("*gold*"))
    repeated = tmp_path / "prepared-again"
    second = run_cli("prepare", "--question-id", "direct-001", "--out", str(repeated))
    assert second.returncode == 0, second.stderr
    assert (prepared / "prepared-manifest.json").read_bytes() == (
        repeated / "prepared-manifest.json"
    ).read_bytes()

    gold_path = tmp_path / "gold.jsonl"
    result = run_cli("recompute", "--out", str(gold_path))
    assert result.returncode == 0, result.stderr
    assert len(gold_path.read_text(encoding="utf-8").splitlines()) == 100

    protected_path = ROOT / "src" / "fhir_question_bench" / "data" / "agent" / "gold.jsonl"
    protected = run_cli("recompute", "--out", str(protected_path))
    assert protected.returncode == 2
    assert not protected_path.exists()

    report = tmp_path / "report.md"
    result = run_cli("demo", "--out", str(report))
    assert result.returncode == 0, result.stderr
    assert report.exists()
    assert "oracle/self-check" in report.read_text(encoding="utf-8").lower()
