from __future__ import annotations

from datetime import datetime, timezone

import pytest

from fhir_question_bench.contracts import (
    ANSWER_STATES,
    ContractError,
    EvidenceRef,
    GoldAnswer,
    Question,
    validate_answer,
)


def valid_question() -> dict:
    return {
        "id": "test-001",
        "primary_family": "direct_facts",
        "task_family_id": "test-family",
        "difficulty": "L1",
        "persona": "informatics",
        "cutoff": "2026-01-01T00:00:00Z",
        "corpus_id": "synthetic-v1",
        "protocol_id": "direct-patient-date",
        "selection_policy": "patient-date-v1",
        "answer_type": "timestamp",
        "review_state": "provisional",
        "prompt": "What is recorded?",
        "parameters": {"patient": "Patient/p1"},
    }


def test_answer_states_are_the_six_contract_states() -> None:
    assert ANSWER_STATES == {
        "answered",
        "partial",
        "insufficient_evidence",
        "ambiguous",
        "unsupported_capability",
        "invalid_data",
    }


def test_question_requires_timezone_aware_cutoff_and_protocol() -> None:
    record = valid_question()
    record.pop("protocol_id")
    with pytest.raises(ContractError):
        Question.from_dict(record)

    record = valid_question()
    record["cutoff"] = "2026-01-01T00:00:00"
    with pytest.raises(ContractError):
        Question.from_dict(record)


def test_evidence_rejects_traversal_and_invalid_json_pointer() -> None:
    base = {
        "corpus_hash": "a" * 64,
        "id": "e1",
        "resource_identity": "Patient/p1",
        "resource_reference": "Patient/p1",
        "source_hash": "b" * 64,
        "file": "patients.json",
        "pointer": "/birthDate",
        "source_value": "1970-01-01",
    }
    with pytest.raises(ContractError):
        EvidenceRef.from_dict({**base, "file": "../secret.json"})
    with pytest.raises(ContractError):
        EvidenceRef.from_dict({**base, "pointer": "/bad~2escape"})


def test_gold_rejects_unknown_answer_state_and_unsupported_tolerance() -> None:
    with pytest.raises(ContractError):
        GoldAnswer.from_dict(
            {
                "question_id": "test-001",
                "status": "unknown",
                "answer": None,
                "rule_id": "rule",
                "required_evidence": [],
            }
        )
    with pytest.raises(ContractError):
        GoldAnswer.from_dict(
            {
                "question_id": "test-001",
                "status": "answered",
                "answer": {"kind": "quantity", "value": {"value": "1", "unit": "mg"}},
                "rule_id": "rule",
                "required_evidence": [],
                "tolerance": {"value": "1", "unit": "mg"},
            }
        )


def test_boolean_contract_does_not_accept_numeric_values() -> None:
    with pytest.raises(ContractError):
        validate_answer({"kind": "boolean", "value": 1})


def test_question_cutoff_is_parsed_as_aware_datetime() -> None:
    question = Question.from_dict(valid_question())
    assert isinstance(question.cutoff, datetime)
    assert question.cutoff.tzinfo == timezone.utc
