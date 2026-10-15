from __future__ import annotations

import copy
import hashlib
import json
import shutil
from pathlib import Path

import pytest

from fhir_question_bench.contracts import AgentResponse
from fhir_question_bench.corpus import load_corpus
from fhir_question_bench.grading import grade_response
from fhir_question_bench.oracle import derive_gold, load_questions
from fhir_question_bench.oracle import OracleError


DATA = Path(__file__).parents[1] / "src" / "fhir_question_bench" / "data"

EXPECTED = {
    "direct-001": ("answered", "timestamp", "1978-04-12"),
    "direct-002": ("answered", "aggregate", {"count": 14}),
    "direct-003": ("answered", "code", {"system": "urn:bench:condition", "code": "local-condition-a", "display": "Synthetic local condition A"}),
    "direct-004": ("answered", "quantity", {"value": "72.5", "unit": "kg"}),
    "temporal-001": ("answered", "quantity", {"value": "4.1", "unit": "mmol/L"}),
    "temporal-002": ("answered", "series", 2),
    "temporal-003": ("answered", "set", ["Condition/c-ada-active"]),
    "temporal-004": ("answered", "narrative", "finished"),
    "join-001": ("answered", "set", 2),
    "join-002": ("answered", "code", {"system": "urn:bench:med", "code": "med-a", "display": "Example medicine A"}),
    "join-003": ("answered", "set", ["Patient/p-transaction"]),
    "join-004": ("answered", "set", ["Condition/c-ada-active"]),
    "term-001": ("answered", "code", {"system": "urn:bench:local-lab", "code": "local-a", "display": "Local analyte A"}),
    "term-002": ("answered", "code", {"match": "equivalent", "targets": [{"system": "urn:bench:target", "code": "target-a", "equivalence": "equivalent", "display": "Synthetic target A"}]}),
    "term-003": (
        "ambiguous",
        "set",
        [
            {"system": "urn:bench:target", "code": "target-broad", "equivalence": "wider"},
            {"system": "urn:bench:target", "code": "target-related", "equivalence": "relatedto"},
        ],
    ),
    "term-004": ("answered", "code", {"match": "unmatched", "targets": []}),
    "cohort-001": ("answered", "set", ["Patient/p-ada", "Patient/p-bryn"]),
    "cohort-002": ("answered", "aggregate", {"count": 2}),
    "cohort-003": ("insufficient_evidence", None, None),
    "cohort-004": ("answered", "set", []),
}


def setup(data_root: Path = DATA):
    corpus = load_corpus(data_root / "agent" / "corpus-manifest.json")
    questions = load_questions(data_root / "questions.jsonl")
    return corpus, questions


def copy_fixture_data(tmp_path: Path) -> Path:
    target = tmp_path / "data"
    shutil.copytree(DATA, target)
    return target


def refresh_corpus_hash(data_root: Path, relative_path: str) -> None:
    manifest_path = data_root / "agent" / "corpus-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    source = data_root / "agent" / Path(*relative_path.split("/"))
    entry = next(item for item in manifest["files"] if item["path"] == relative_path)
    entry["sha256"] = hashlib.sha256(source.read_bytes()).hexdigest()
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")


def mutate_ndjson(data_root: Path, resource_id: str, mutate) -> None:
    source = data_root / "agent" / "corpus" / "resources.ndjson"
    resources = [json.loads(line) for line in source.read_text(encoding="utf-8").splitlines()]
    target = next(item for item in resources if item.get("id") == resource_id)
    mutate(target)
    source.write_text(
        "".join(json.dumps(item, separators=(",", ":"), ensure_ascii=False) + "\n" for item in resources),
        encoding="utf-8",
    )
    refresh_corpus_hash(data_root, "corpus/resources.ndjson")


def test_duplicate_question_ids_are_rejected(tmp_path: Path) -> None:
    original = (DATA / "questions.jsonl").read_text(encoding="utf-8").splitlines()[0]
    path = tmp_path / "questions.jsonl"
    path.write_text(original + "\n" + original + "\n", encoding="utf-8")
    with pytest.raises(OracleError, match="unique"):
        load_questions(path)


def test_exactly_twenty_original_questions_and_hand_calculated_gold() -> None:
    corpus, questions = setup()
    assert len(questions) == 20
    assert len({question.id for question in questions}) == 20
    for family in {
        "direct_facts",
        "temporal_status_trends",
        "cross_resource_joins",
        "owned_local_terminology",
        "cohort_missing_evidence",
    }:
        assert sum(question.primary_family == family for question in questions) == 4

    for question in questions:
        gold = derive_gold(question, corpus)
        status, kind, expected = EXPECTED[question.id]
        assert gold.status == status
        if kind is None:
            assert gold.answer is None
        else:
            assert gold.answer["kind"] == kind
        if kind is None:
            continue
        if isinstance(expected, int) and kind in {"series", "set"}:
            assert len(gold.answer["value"]) == expected
        else:
            assert gold.answer["value"] == expected
    mapping = derive_gold(next(item for item in questions if item.id == "term-003"), corpus)
    assert {item["equivalence"] for item in mapping.answer["value"]} == {"wider", "relatedto"}


def test_gold_changes_when_a_decisive_source_value_changes(tmp_path: Path) -> None:
    data_root = copy_fixture_data(tmp_path)
    corpus, questions = setup(data_root)
    question = next(item for item in questions if item.id == "temporal-001")
    original = derive_gold(question, corpus)
    mutate_ndjson(
        data_root,
        "o-potassium-at-cutoff",
        lambda resource: resource["valueQuantity"].update({"value": 5.2}),
    )
    mutated = load_corpus(data_root / "agent" / "corpus-manifest.json")
    changed = derive_gold(question, mutated, data_root)
    assert original.answer["value"]["value"] == "4.1"
    assert changed.answer["value"]["value"] == "5.2"
    assert "2026-01-15T00:00:00Z" in {
        item.source_value for item in original.required_evidence if item.pointer.endswith("effectiveDateTime")
    }


def test_mapping_relation_mutation_cannot_become_an_exact_translation(tmp_path: Path) -> None:
    from fhir_question_bench.oracle import OracleError

    data_root = copy_fixture_data(tmp_path)
    corpus, questions = setup(data_root)
    question = next(item for item in questions if item.id == "term-002")
    concept_map_path = data_root / "agent" / "terminology" / "ConceptMap.json"
    concept_map = json.loads(concept_map_path.read_text(encoding="utf-8"))
    concept_map["group"][0]["element"][0]["target"][0]["equivalence"] = "wider"
    concept_map_path.write_text(json.dumps(concept_map, indent=2) + "\n", encoding="utf-8")
    refresh_corpus_hash(data_root, "terminology/ConceptMap.json")
    mutated = load_corpus(data_root / "agent" / "corpus-manifest.json")
    with pytest.raises(OracleError, match="exactly one equivalent"):
        derive_gold(question, mutated, data_root)


def test_demo_response_is_strictly_graded_and_wrong_patient_evidence_fails() -> None:
    from fhir_question_bench.demo import make_demo_response

    corpus, questions = setup()
    question = next(item for item in questions if item.id == "temporal-001")
    gold = derive_gold(question, corpus)
    response = make_demo_response(question, gold)
    assert grade_response(question, gold, response, corpus).strict_success

    altered = copy.deepcopy(response)
    evidence = altered["evidence"][0]
    wrong_patient = next(item for item in corpus.resources if item.identity == "Patient/p-bryn")
    evidence.update(
        {
            "resource_identity": wrong_patient.identity,
            "resource_reference": wrong_patient.identity,
            "corpus_hash": corpus.corpus_hash,
            "source_hash": wrong_patient.source_hash,
            "file": wrong_patient.file,
            "pointer": "/id",
            "source_value": wrong_patient.resource["id"],
            "line": wrong_patient.line,
            "entry_index": wrong_patient.entry_index,
        }
    )
    result = grade_response(question, gold, AgentResponse.from_dict(altered), corpus)
    assert not result.strict_success
    assert "required evidence" in " ".join(result.errors).lower()
    assert not any("pointer" in error.lower() for error in result.errors)


def test_corrupt_hash_pointer_type_and_extra_claims_are_rejected() -> None:
    from fhir_question_bench.demo import make_demo_response
    from fhir_question_bench.contracts import ContractError

    corpus, questions = setup()
    question = next(item for item in questions if item.id == "direct-004")
    gold = derive_gold(question, corpus)
    response = make_demo_response(question, gold)

    for field, value in (("corpus_hash", "0" * 64), ("source_hash", "0" * 64), ("pointer", "/missing")):
        invalid = copy.deepcopy(response)
        invalid["evidence"][0][field] = value
        result = grade_response(question, gold, invalid, corpus)
        assert not result.strict_success

    wrong_type = copy.deepcopy(response)
    wrong_type["answer"]["value"]["value"] = True
    with pytest.raises(ContractError):
        AgentResponse.from_dict(wrong_type)

    extra_claim = copy.deepcopy(response)
    extra_claim["claims"].append(
        {"id": "unsupported", "answer_pointer": "/answer", "evidence_ids": [], "rule_id": "other"}
    )
    result = grade_response(question, gold, extra_claim, corpus)
    assert not result.strict_success


def test_absence_requires_matching_scope_and_false_absence_fails_after_mutation(tmp_path: Path) -> None:
    from fhir_question_bench.demo import make_demo_response

    corpus, questions = setup()
    question = next(item for item in questions if item.id == "cohort-004")
    gold = derive_gold(question, corpus)
    response = make_demo_response(question, gold)
    assert grade_response(question, gold, response, corpus).strict_success

    response["scope_checks"][0]["resource_type"] = "MedicationRequest"
    result = grade_response(question, gold, response, corpus)
    assert not result.strict_success
    assert any("scope" in error.lower() for error in result.errors)

    response = make_demo_response(question, gold)
    response["scope_checks"][0]["claim_id"] = "other-claim"
    result = grade_response(question, gold, response, corpus)
    assert not result.strict_success
    assert any("bound" in error.lower() for error in result.errors)

    data_root = copy_fixture_data(tmp_path)
    source = data_root / "agent" / "corpus" / "resources.ndjson"
    source.write_text(
        source.read_text(encoding="utf-8")
        + json.dumps(
            {
                "resourceType": "Observation",
                "id": "false-absence",
                "status": "final",
                "subject": {"reference": "Patient/p-ada"},
                "code": {"coding": [{"system": "urn:bench:local-lab", "code": "never-present"}]},
                "effectiveDateTime": "2026-01-12T08:00:00Z",
            },
            separators=(",", ":"),
        )
        + "\n",
        encoding="utf-8",
    )
    refresh_corpus_hash(data_root, "corpus/resources.ndjson")
    changed = load_corpus(data_root / "agent" / "corpus-manifest.json")
    stale_hash_gold = derive_gold(question, changed, data_root)
    assert stale_hash_gold.answer["value"] == ["Observation/false-absence"]
    assert not grade_response(question, gold, make_demo_response(question, gold), changed).strict_success
