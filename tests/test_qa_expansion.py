from __future__ import annotations

import hashlib
import json
import shutil
from dataclasses import replace
from collections import Counter
from decimal import Decimal
from pathlib import Path

import pytest

from fhir_question_bench.contracts import ContractError, Question
from fhir_question_bench.corpus import load_corpus
from fhir_question_bench.demo import make_demo_response
from fhir_question_bench.grading import grade_response
from fhir_question_bench.oracle import audit_dataset, derive_gold, load_questions
from fhir_question_bench.prepare import prepare_question


DATA = Path(__file__).parents[1] / "src/fhir_question_bench/data"


def test_expansion_metadata_and_recomputed_audit():
    questions = load_questions()
    assert len(questions) == 100
    assert Counter(q.tier for q in questions) == {1: 15, 2: 20, 3: 20, 4: 20, 5: 25}
    assert all(q.verification_status == "provisional" for q in questions[:20])
    assert all(q.verification_status == "unverified" for q in questions[20:])
    assert all(q.capabilities_advantaged for q in questions)
    audit = audit_dataset()
    assert audit["gold_recomputed"] == 100
    assert audit["tier_counts"] == {"1": 15, "2": 20, "3": 20, "4": 20, "5": 25}
    assert audit["verification_counts"] == {"provisional": 20, "unverified": 80}


def test_legacy_question_defaults_and_metadata_validation():
    row = json.loads((DATA / "questions.jsonl").read_text().splitlines()[0])
    for key in ("tier", "verification_status", "capabilities_advantaged"):
        row.pop(key, None)
    legacy = Question.from_dict(row)
    assert legacy.tier == 1
    assert legacy.verification_status == "provisional"
    for key, value in [("tier", True), ("tier", 6), ("verification_status", "verified"),
                       ("capabilities_advantaged", [""]), ("capabilities_advantaged", "lookup")]:
        invalid = {**row, key: value}
        with pytest.raises(ContractError):
            Question.from_dict(invalid)


# These literals are hand-calculated from the authored scenario, not from oracle output.
@pytest.mark.parametrize(("qid", "status", "value"), [
    ("hard-001", "answered", {"match": "equivalent", "targets": [
        {"system": "http://snomed.info/sct", "code": "390906007", "equivalence": "equivalent"}]}),
    ("hard-004", "insufficient_evidence", None),
    ("hard-007", "insufficient_evidence", None),
    ("hard-008", "insufficient_evidence", None),
    ("hard-009", "ambiguous", [
        {"system": "urn:bench:qa:target", "code": "a", "equivalence": "equivalent"},
        {"system": "urn:bench:qa:target", "code": "b", "equivalence": "equivalent"}]),
    ("hard-011", "answered", True),
    ("hard-012", "answered", False),
    ("hard-013", "answered", False),
    ("hard-014", "answered", {"system": "urn:bench:qa:lab", "code": "renal", "display": "Owned renal marker"}),
    ("hard-016", "invalid_data", None),
    ("hard-017", "answered", {"system": "urn:bench:qa:ambiguous", "code": "X", "display": "Owned specimen marker"}),
    ("hard-018", "answered", {"system": "urn:bench:qa:ambiguous", "code": "X", "display": "Owned medication marker"}),
    ("hard-019", "ambiguous", None),
    ("hard-021", "ambiguous", None),
    ("hard-022", "answered", {"match": "equivalent", "targets": [
        {"system": "urn:bench:qa:target", "code": "serum", "equivalence": "equivalent"}]}),
    ("hard-023", "answered", {"value": "72.5", "unit": "kg"}),
    ("hard-024", "answered", {"value": "128", "unit": "g/L"}),
    ("hard-025", "insufficient_evidence", None),
    ("qa-cohort-001", "answered", ["Patient/qa-01", "Patient/qa-02", "Patient/qa-07"]),
    ("qa-cohort-002", "answered", ["Patient/qa-01", "Patient/qa-02", "Patient/qa-07"]),
    ("qa-cohort-003", "answered", []),
    ("qa-cohort-004", "answered", ["Patient/qa-01", "Patient/qa-02", "Patient/qa-04", "Patient/qa-07"]),
    ("qa-cohort-005", "answered", []),
    ("qa-cohort-006", "answered", ["Patient/qa-07"]),
    ("qa-cohort-007", "answered", ["Patient/qa-01", "Patient/qa-02", "Patient/qa-07", "Patient/qa-08"]),
    ("qa-cohort-008", "answered", ["Patient/qa-01", "Patient/qa-02", "Patient/qa-07"]),
    ("qa-join-011", "answered", ["Observation/qa-01-marker-a", "Observation/qa-01-marker-b"]),
])
def test_hand_calculated_expansion_answers(qid, status, value):
    question = next(q for q in load_questions() if q.id == qid)
    corpus = load_corpus(DATA / "agent/corpus-manifest.json")
    gold = derive_gold(question, corpus)
    assert gold.status == status
    actual = gold.answer["value"] if gold.answer else None
    if question.answer_type == "quantity" and value is not None:
        assert Decimal(actual["value"]) == Decimal(value["value"])
        assert actual["unit"] == value["unit"]
    else:
        assert actual == value
    assert gold.required_evidence
    assert grade_response(question, gold, make_demo_response(question, gold), corpus).strict_success


@pytest.mark.parametrize("field", ["map_version", "version", "target_version", "target_system"])
def test_translation_does_not_cross_a_version_or_system_boundary(field):
    question = next(q for q in load_questions() if q.id == "hard-001")
    changed = replace(question, parameters={**question.parameters, field: "not-the-pinned-value"})
    corpus = load_corpus(DATA / "agent/corpus-manifest.json")
    if field == "map_version":
        with pytest.raises(ValueError, match="pinned ConceptMap"):
            derive_gold(changed, corpus)
    else:
        gold = derive_gold(changed, corpus)
        assert gold.status == "insufficient_evidence"
        assert gold.answer is None


@pytest.mark.parametrize(("qid", "answer"), [
    ("hard-023", {"kind": "quantity", "value": {"value": "72500", "unit": "kg"}}),
    ("hard-024", {"kind": "quantity", "value": {"value": "12.8", "unit": "g/L"}}),
    ("hard-012", {"kind": "boolean", "value": True}),
    ("qa-cohort-001", {"kind": "set", "value": ["Patient/qa-01", "Patient/qa-02", "Patient/qa-06", "Patient/qa-07"]}),
])
def test_plausible_label_direction_and_cohort_errors_are_rejected(qid, answer):
    corpus = load_corpus(DATA / "agent/corpus-manifest.json")
    question = next(q for q in load_questions() if q.id == qid)
    gold = derive_gold(question, corpus)
    response = make_demo_response(question, gold)
    response["answer"] = answer
    assert not grade_response(question, gold, response, corpus).strict_success


def test_preparation_does_not_export_oracle_or_review(tmp_path):
    corpus = load_corpus(DATA / "agent/corpus-manifest.json")
    question = next(q for q in load_questions() if q.id == "hard-022")
    prepare_question(question, corpus, DATA, tmp_path / "public")
    names = [p.name for p in (tmp_path / "public").rglob("*")]
    assert not any("gold" in name or "oracle" in name or "review" in name for name in names)
    exported = json.loads((tmp_path / "public/question.json").read_text())
    assert exported["verification_status"] == "unverified"


@pytest.mark.parametrize(("qid", "resource_id", "mutate"), [
    ("hard-001", "qa-map", lambda r: r["group"][0]["element"][0]["target"][0].update(equivalence="wider")),
    ("hard-011", "qa-hierarchy", lambda r: r["concept"][0].update(concept=[])),
    ("hard-023", "qa-unit-weight", lambda r: r["valueQuantity"].update(code="g/dL")),
    ("hard-016", "qa-lab", lambda r: r["concept"][1]["property"][0].update(valueBoolean=False)),
])
def test_decisive_source_mutation_cannot_pass_stale_gold(tmp_path, qid, resource_id, mutate):
    root = tmp_path / "data"
    shutil.copytree(DATA, root)
    corpus = load_corpus(root / "agent/corpus-manifest.json")
    question = next(q for q in load_questions(root / "questions.jsonl") if q.id == qid)
    gold = derive_gold(question, corpus, root)
    response = make_demo_response(question, gold)
    source = root / "agent/terminology/qa-fixtures.json"
    bundle = json.loads(source.read_text())
    resource = next(e["resource"] for e in bundle["entry"] if e["resource"]["id"] == resource_id)
    mutate(resource)
    source.write_text(json.dumps(bundle) + "\n", encoding="utf-8")
    manifest_path = root / "agent/corpus-manifest.json"
    manifest = json.loads(manifest_path.read_text())
    next(e for e in manifest["files"] if e["path"] == "terminology/qa-fixtures.json")["sha256"] = hashlib.sha256(source.read_bytes()).hexdigest()
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
    changed = load_corpus(manifest_path)
    assert not grade_response(question, gold, response, changed, root).strict_success
