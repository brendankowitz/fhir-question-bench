from __future__ import annotations

from typing import Any

from .contracts import GoldAnswer, Question


def make_demo_response(question: Question, gold: GoldAnswer) -> dict[str, Any]:
    """Build a deterministic oracle self-check response; this is not an agent run."""
    response: dict[str, Any] = {
        "question_id": question.id,
        "status": gold.status,
        "answer": gold.answer,
        "claims": [
            {
                "id": "claim-1",
                "answer_pointer": "/answer",
                "evidence_ids": [item.id for item in gold.required_evidence],
                "rule_id": gold.rule_id,
            }
        ],
        "evidence": [item.as_dict() for item in gold.required_evidence],
        "limitations": [],
    }
    if gold.scope_obligation is not None:
        response["scope_checks"] = [
            {
                "id": "scope-1",
                "claim_id": "claim-1",
                "corpus_hash": gold.required_evidence[0].corpus_hash,
                **gold.scope_obligation,
                "complete_scan": True,
            }
        ]
    return response
