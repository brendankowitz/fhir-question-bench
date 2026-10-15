from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any

from .contracts import AgentResponse, GoldAnswer
from .corpus import load_corpus
from .demo import make_demo_response
from .grading import grade_response
from .oracle import DATA_ROOT, audit_dataset, derive_gold, load_questions
from .prepare import prepare_question


def _json(value: Any) -> str:
    return json.dumps(value, sort_keys=True, indent=2, ensure_ascii=False)


def _dataset(root: Path):
    questions = load_questions(root / "questions.jsonl")
    corpus = load_corpus(root / "agent" / "corpus-manifest.json")
    return questions, corpus


def _write(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")


def _read_gold(path: Path, question_id: str) -> GoldAnswer:
    text = path.read_text(encoding="utf-8")
    try:
        document = json.loads(text)
    except json.JSONDecodeError:
        document = []
        for line_number, line in enumerate(text.splitlines(), start=1):
            if not line.strip():
                continue
            try:
                document.append(json.loads(line))
            except json.JSONDecodeError as error:
                raise ValueError(f"invalid gold JSON on line {line_number}: {error}") from error
    rows = document if isinstance(document, list) else [document]
    for row in rows:
        if isinstance(row, dict) and row.get("question_id") == question_id:
            return GoldAnswer.from_dict(row)
    if not rows:
        raise ValueError(f"gold file does not contain question {question_id}")
    raise ValueError(f"gold file does not contain question {question_id}")


def _summary_markdown(title: str, rows: list[dict[str, Any]], label: str) -> str:
    successes = sum(bool(row["strict_success"]) for row in rows)
    lines = [
        f"# {title}",
        "",
        f"**Mode:** {label}",
        "",
        f"Strict grounded success: **{successes}/{len(rows)}**",
        "",
        "| Question | Strict success | Errors |",
        "|---|---:|---|",
    ]
    lines.extend(
        f"| {row['question_id']} | {'yes' if row['strict_success'] else 'no'} | "
        f"{'; '.join(row['errors']) or '—'} |"
        for row in rows
    )
    return "\n".join(lines) + "\n"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="fhir_question_bench")
    parser.add_argument("--data-root", type=Path, default=DATA_ROOT)
    subparsers = parser.add_subparsers(dest="command", required=True)

    audit = subparsers.add_parser("audit", help="validate the provisional smoke dataset and recompute all gold")
    audit.add_argument("--out", type=Path)
    prepare = subparsers.add_parser("prepare", help="export one question and agent-visible assets")
    prepare.add_argument("--question-id", required=True)
    prepare.add_argument("--out", type=Path, required=True)
    recompute = subparsers.add_parser("recompute", help="derive private gold from the locked source bytes")
    recompute.add_argument("--out", type=Path, required=True)
    grade = subparsers.add_parser("grade", help="grade one strict JSON response")
    grade.add_argument("--question-id", required=True)
    grade.add_argument("--response", type=Path, required=True)
    grade.add_argument("--gold", type=Path, required=True)
    grade.add_argument("--out", type=Path, required=True)
    demo = subparsers.add_parser("demo", help="run the offline oracle self-check; not measured agent performance")
    demo.add_argument("--out", type=Path, required=True)
    run = subparsers.add_parser("run", help="prepare initial20 episodes; dry-run by default, live blocked")
    run.add_argument("--config", type=Path, required=True)
    run.add_argument("--output", type=Path, required=True)
    run.add_argument("--execute", action="store_true")
    run.add_argument("--approve-total-credits", type=float)
    run.add_argument("--fake-process", type=Path, help="trusted offline .py fixture ONLY; no model performance")
    run.add_argument("--fake-tool-process", type=Path, help="explicit trusted offline MCP fixture ONLY")

    args = parser.parse_args(argv)
    try:
        if args.command == "run":
            from .runner import run_benchmark

            report = run_benchmark(
                args.config, args.output, args.data_root, execute=args.execute,
                approve_total_credits=args.approve_total_credits,
                fake_process=args.fake_process, fake_tool_process=args.fake_tool_process,
            )
            print(_json({"mode": report["mode"], "output": str(args.output),
                         "snapshot_id": report["snapshot_id"], **report["counts"],
                         "budget": report["budget"]}))
            return 1 if report["counts"]["failed"] or report["counts"]["blocked"] else 0
        questions, corpus = _dataset(args.data_root)
        if args.command == "audit":
            result = audit_dataset(args.data_root)
            rendered = _json(result) + "\n"
            if args.out:
                _write(args.out, rendered)
            print(rendered, end="")
            return 0
        if args.command == "prepare":
            question = next((item for item in questions if item.id == args.question_id), None)
            if question is None:
                raise ValueError(f"unknown question ID {args.question_id}")
            prepared = prepare_question(question, corpus, args.data_root, args.out)
            print(_json({"prepared": str(prepared), "question_id": question.id, "corpus_hash": corpus.corpus_hash}))
            return 0
        if args.command == "recompute":
            output_path = args.out.resolve()
            try:
                output_path.relative_to((args.data_root / "agent").resolve())
            except ValueError:
                pass
            else:
                raise ValueError("private gold output must not be written inside the agent-visible data directory")
            rows = [
                derive_gold(question, corpus, args.data_root).as_dict()
                for question in questions
            ]
            _write(output_path, "".join(_json(row).replace("\n", "") + "\n" for row in rows))
            print(_json({"gold_count": len(rows), "output": str(output_path), "mode": "private offline recompute"}))
            return 0
        if args.command == "grade":
            question = next((item for item in questions if item.id == args.question_id), None)
            if question is None:
                raise ValueError(f"unknown question ID {args.question_id}")
            gold = _read_gold(args.gold, args.question_id)
            response = AgentResponse.from_dict(json.loads(args.response.read_text(encoding="utf-8")))
            result = grade_response(question, gold, response, corpus, args.data_root)
            _write(args.out, _json(result.as_dict()) + "\n")
            markdown = args.out.with_suffix(".md")
            _write(markdown, _summary_markdown("FHIR benchmark grading", [result.as_dict()], "response grading"))
            print(_json({"json": str(args.out), "markdown": str(markdown), **result.as_dict()}))
            return 0 if result.strict_success else 1
        if args.command == "demo":
            rows = []
            for question in questions:
                gold = derive_gold(question, corpus, args.data_root)
                response = make_demo_response(question, gold)
                rows.append(grade_response(question, gold, response, corpus).as_dict())
            json_path = args.out.with_suffix(".json")
            _write(
                json_path,
                _json(
                    {
                        "mode": "oracle/self-check",
                        "measured_agent_performance": False,
                        "corpus_hash": corpus.corpus_hash,
                        "grades": rows,
                    }
                )
                + "\n",
            )
            _write(args.out, _summary_markdown("FHIR benchmark offline demo", rows, "oracle/self-check; not measured agent performance"))
            print(_json({"json": str(json_path), "markdown": str(args.out), "strict_success": sum(row["strict_success"] for row in rows), "question_count": len(rows), "mode": "oracle/self-check"}))
            return 0 if all(row["strict_success"] for row in rows) else 1
        raise ValueError(f"unsupported command {args.command}")
    except (OSError, ValueError, KeyError, StopIteration) as error:
        parser.exit(2, f"error: {error}\n")
    return 2
