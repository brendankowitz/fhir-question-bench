from __future__ import annotations

import copy
import hashlib
import json
import os
import shutil
import subprocess
import sys
import zipfile
from dataclasses import replace
from pathlib import Path

import pytest

from fhir_question_bench.contracts import AgentResponse, GoldAnswer
from fhir_question_bench.corpus import Corpus, ResolvedResource, load_corpus
from fhir_question_bench.grading import grade_response
from fhir_question_bench.oracle import OracleError, derive_gold, load_questions
from fhir_question_bench.prepare import prepare_question
from fhir_question_bench.references import resolve_reference

from test_oracle_grading import DATA, copy_fixture_data, mutate_ndjson, refresh_corpus_hash, setup


def _question(questions, question_id):
    return next(item for item in questions if item.id == question_id)


def _independent_response(question, corpus, answer, evidence_fields, protocol, scope=None):
    evidence = []
    for index, (resource_id, pointer) in enumerate(evidence_fields, start=1):
        resource = next(item for item in corpus.resources if item.identity == resource_id)
        evidence.append(resource.evidence(f"e{index}", pointer))
    response = {
        "question_id": question.id,
        "status": "answered",
        "answer": {"kind": question.answer_type, "value": answer},
        "claims": [
            {
                "id": "claim-1",
                "answer_pointer": "/answer",
                "evidence_ids": [item["id"] for item in evidence],
                "rule_id": protocol["rule_id"],
            }
        ],
        "evidence": evidence,
        "limitations": [],
    }
    if scope is not None:
        response["scope_checks"] = [
            {
                "id": "scope-1",
                "claim_id": "claim-1",
                "corpus_hash": corpus.corpus_hash,
                **scope,
                "complete_scan": True,
            }
        ]
    return response


def _public_assets(data_root: Path, question_id: str):
    prepared = data_root / "prepared"
    corpus = load_corpus(prepared / "corpus-manifest.json")
    question = json.loads((prepared / "question.json").read_text(encoding="utf-8"))
    contract = json.loads((prepared / "response-contract.json").read_text(encoding="utf-8"))
    policies = json.loads((prepared / "selection-policies.json").read_text(encoding="utf-8"))
    protocol_registry = json.loads((prepared / "protocols" / "protocols.json").read_text(encoding="utf-8"))
    protocol = next(item for item in protocol_registry["protocols"] if item["id"] == question["protocol_id"])
    return prepared, corpus, question, contract, policies, protocol


def test_package_data_declares_all_manifest_formats():
    project_config = (Path(__file__).parents[1] / "pyproject.toml").read_text(encoding="utf-8")
    assert "data/**/*.ndjson" in project_config


def test_installed_wheel_audits_from_outside_checkout(tmp_path):
    root = Path(__file__).parents[1]
    source = tmp_path / "wheel-source"
    source.mkdir()
    shutil.copy2(root / "pyproject.toml", source / "pyproject.toml")
    shutil.copy2(root / "README.md", source / "README.md")
    shutil.copytree(root / "src" / "fhir_question_bench", source / "src" / "fhir_question_bench")
    wheel_dir = tmp_path / "wheels"
    wheel_dir.mkdir()
    built = subprocess.run(
        [sys.executable, "-m", "pip", "wheel", str(source), "--no-deps", "--no-build-isolation", "--wheel-dir", str(wheel_dir)],
        cwd=tmp_path, capture_output=True, text=True, check=False,
    )
    assert built.returncode == 0, built.stderr
    wheel = next(wheel_dir.glob("*.whl"))
    package_prefix = "fhir_question_bench/data/"
    with zipfile.ZipFile(wheel) as archive:
        members = set(archive.namelist())
        dataset_manifest = json.loads(archive.read(package_prefix + "dataset-manifest.json"))
        corpus_manifest = json.loads(archive.read(package_prefix + "agent/corpus-manifest.json"))
        expected = {package_prefix + item["path"] for item in dataset_manifest["files"]}
        expected.update(package_prefix + "agent/" + item["path"] for item in corpus_manifest["files"])
        assert expected <= members
        assert package_prefix + "agent/corpus/resources.ndjson" in members

    site = tmp_path / "installed-site"
    install = subprocess.run(
        [sys.executable, "-m", "pip", "install", "--no-deps", "--target", str(site), str(wheel)],
        cwd=tmp_path, capture_output=True, text=True, check=False,
    )
    assert install.returncode == 0, install.stderr
    runner = tmp_path / "outside-checkout"
    runner.mkdir()
    env = dict(os.environ)
    env["PYTHONPATH"] = str(site)
    origin = subprocess.run(
        [sys.executable, "-c", "import fhir_question_bench; print(fhir_question_bench.__file__)"],
        cwd=runner, env=env, capture_output=True, text=True, check=False,
    )
    assert origin.returncode == 0, origin.stderr
    assert Path(origin.stdout.strip()).resolve().is_relative_to(site.resolve())
    audit = subprocess.run(
        [sys.executable, "-m", "fhir_question_bench", "audit"],
        cwd=runner, env=env, capture_output=True, text=True, check=False,
    )
    assert audit.returncode == 0, audit.stderr
    assert json.loads(audit.stdout)["question_count"] == 20


def test_relative_references_are_resolved_in_the_source_rest_base():
    source = ResolvedResource(
        {"resourceType": "Observation", "id": "o", "subject": {"reference": "Patient/p"}},
        "Observation/o", "a.json", "a" * 64, "c" * 64,
        full_url="https://a.test/fhir/Observation/o", bundle_file="a.json",
    )
    remote = ResolvedResource(
        {"resourceType": "Patient", "id": "p"},
        "Patient/p", "b.json", "b" * 64, "c" * 64,
        full_url="https://b.test/fhir/Patient/p", bundle_file="b.json",
    )
    local = ResolvedResource(
        {"resourceType": "Patient", "id": "p"},
        "Patient/p", "a.json", "a" * 64, "c" * 64,
        full_url="https://a.test/fhir/Patient/p", bundle_file="a.json",
    )
    corpus = Corpus("c", "c" * 64, [source, remote, local], Path("manifest.json"), {})
    result = resolve_reference(source, "Patient/p", corpus)
    assert result.status == "resolved"
    assert result.targets == (local,)

    unrelated = Corpus("c", "c" *  64, [source, remote], Path("manifest.json"), {})
    assert resolve_reference(source, "Patient/p", unrelated).status == "missing"


def test_contained_resources_resolve_siblings_and_owner():
    owner = ResolvedResource(
        {
            "resourceType": "Observation",
            "id": "o",
            "contained": [
                {"resourceType": "Specimen", "id": "s", "subject": {"reference": "#p"}},
                {"resourceType": "Patient", "id": "p", "managingOrganization": {"reference": "#o"}},
            ],
        },
        "Observation/o", "a.json", "a" * 64, "c" * 64,
    )
    corpus = Corpus("c", "c" * 64, [owner], Path("manifest.json"), {})
    specimen = resolve_reference(owner, "#s", corpus).targets[0]
    patient = resolve_reference(specimen, "#p", corpus)
    assert patient.status == "resolved"
    assert patient.targets[0].identity == "Observation/o#p"
    owner_ref = resolve_reference(patient.targets[0], "#o", corpus)
    assert owner_ref.status == "resolved"
    assert owner_ref.targets[0] is owner


@pytest.mark.parametrize(
    ("question_id", "mutate", "message"),
    [
        ("join-004", lambda item: item["diagnosis"][0]["condition"].update(reference="Condition/not-exported"), "missing"),
        ("cohort-002", lambda item: None, "ambiguous"),
    ],
)
def test_unresolved_reference_membership_never_certifies_complete_gold(tmp_path, question_id, mutate, message):
    data_root = copy_fixture_data(tmp_path)
    if question_id == "join-004":
        mutate_ndjson(data_root, "e-ada", mutate)
    else:
        source = data_root / "agent" / "corpus" / "resources.ndjson"
        source.write_bytes(source.read_bytes() + b'{"resourceType":"Patient","id":"p-ada","birthDate":"1978-04-12"}\n')
        refresh_corpus_hash(data_root, "corpus/resources.ndjson")
    corpus = load_corpus(data_root / "agent" / "corpus-manifest.json")
    question = _question(load_questions(data_root / "questions.jsonl"), question_id)
    with pytest.raises(OracleError, match=message):
        derive_gold(question, corpus, data_root)


def test_absence_query_rejects_unresolved_candidate_membership(tmp_path):
    data_root = copy_fixture_data(tmp_path)
    source = data_root / "agent" / "corpus" / "resources.ndjson"
    source.write_bytes(source.read_bytes() + (
        b'{"resourceType":"Observation","id":"unresolved-absence","status":"final",'
        b'"code":{"coding":[{"system":"urn:bench:local-lab","code":"never-present"}]},'
        b'"subject":{"reference":"Patient/not-exported"},"effectiveDateTime":"2026-01-12T00:00:00Z"}\n'
    ))
    refresh_corpus_hash(data_root, "corpus/resources.ndjson")
    corpus = load_corpus(data_root / "agent" / "corpus-manifest.json")
    question = _question(load_questions(data_root / "questions.jsonl"), "cohort-004")
    with pytest.raises(OracleError, match="subject reference is missing"):
        derive_gold(question, corpus, data_root)


def test_medication_join_compares_resolved_bundle_locations(tmp_path):
    data_root = copy_fixture_data(tmp_path)
    bundle_path = data_root / "agent" / "corpus" / "transaction.json"
    bundle = json.loads(bundle_path.read_text(encoding="utf-8"))
    source = data_root / "agent" / "corpus" / "resources.ndjson"
    resources = [json.loads(line) for line in source.read_text(encoding="utf-8").splitlines()]
    selected = {"Patient/p-ada", "Encounter/e-ada", "MedicationRequest/mr-ada", "MedicationAdministration/ma-ada"}
    entries = []
    for item in resources:
        identity = f"{item.get('resourceType')}/{item.get('id')}"
        if identity not in selected:
            continue
        item = copy.deepcopy(item)
        if identity == "Patient/p-ada":
            item["id"] = "patient-bundle"
            item["fullUrl"] = None
        if identity == "Encounter/e-ada":
            item["id"] = "encounter-bundle"
            item["subject"]["reference"] = "urn:uuid:patient"
            item["diagnosis"] = []
        if identity in {"MedicationRequest/mr-ada", "MedicationAdministration/ma-ada"}:
            item["subject"]["reference"] = "urn:uuid:patient"
            item["encounter" if identity.startswith("MedicationRequest") else "context"]["reference"] = "Encounter/encounter-bundle"
        item.pop("fullUrl", None)
        entries.append({"fullUrl": f"urn:uuid:{identity.split('/')[0].lower()}", "resource": item})
    bundle["entry"].extend(entries)
    bundle_path.write_text(json.dumps(bundle, separators=(",", ":")), encoding="utf-8")
    manifest_path = data_root / "agent" / "corpus-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    entry = next(item for item in manifest["files"] if item["path"] == "corpus/transaction.json")
    entry["sha256"] = hashlib.sha256(bundle_path.read_bytes()).hexdigest()
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
    corpus = load_corpus(manifest_path)
    question = _question(load_questions(data_root / "questions.jsonl"), "join-001")
    question = replace(question, parameters={"encounter": "Encounter/encounter-bundle"})
    assert derive_gold(question, corpus, data_root).answer["value"] == [
        "MedicationAdministration/ma-ada", "MedicationRequest/mr-ada"
    ]


def test_medication_join_rejects_unresolved_relevant_context(tmp_path):
    data_root = copy_fixture_data(tmp_path)
    mutate_ndjson(
        data_root,
        "ma-ada",
        lambda item: item["context"].update(reference="Encounter/not-exported"),
    )
    corpus = load_corpus(data_root / "agent" / "corpus-manifest.json")
    question = _question(load_questions(data_root / "questions.jsonl"), "join-001")
    with pytest.raises(OracleError, match="context reference is missing"):
        derive_gold(question, corpus, data_root)


def test_evidence_accepts_sufficient_quantity_leaves_but_not_wrong_patient():
    corpus, questions = setup()
    question = _question(questions, "direct-004")
    resource = next(item for item in corpus.resources if item.identity == "Observation/o-weight-ada")
    evidence = [resource.evidence("e1", "/valueQuantity/value"), resource.evidence("e2", "/valueQuantity/unit")]
    response = {
        "question_id": question.id,
        "status": "answered",
        "answer": {"kind": "quantity", "value": {"value": "72.50", "unit": "kg"}},
        "claims": [{"id": "c", "answer_pointer": "/answer", "evidence_ids": ["e1", "e2"], "rule_id": "direct-quantity-v1"}],
        "evidence": evidence,
        "limitations": [],
    }
    assert grade_response(question, derive_gold(question, corpus), response, corpus).strict_success
    bad = copy.deepcopy(response)
    bad_resource = next(item for item in corpus.resources if item.identity == "Observation/o-weight-ada")
    bad["evidence"] = [bad_resource.evidence("e1", "/valueQuantity/value"), bad_resource.evidence("e2", "/valueQuantity/unit")]
    bad["evidence"][0]["resource_identity"] = "Observation/o-potassium-old"
    assert not grade_response(question, derive_gold(question, corpus), bad, corpus).strict_success


def test_mutated_typed_response_and_gold_are_revalidated():
    from fhir_question_bench.demo import make_demo_response

    corpus, questions = setup()
    question = _question(questions, "direct-004")
    gold = derive_gold(question, corpus)
    response = AgentResponse.from_dict(make_demo_response(question, gold))
    response.claims[0]["answer_pointer"] = "/not-answer"
    assert not grade_response(question, gold, response, corpus).strict_success

    response = AgentResponse.from_dict(make_demo_response(question, gold))
    response.answer["value"]["value"] = 72.5
    assert not grade_response(question, gold, response, corpus).strict_success

    directly_constructed = AgentResponse(
        question.id,
        "answered",
        {"kind": "quantity", "value": {"value": 72.5, "unit": "kg"}},
        ({"id": "claim-1", "answer_pointer": "/answer", "evidence_ids": ["e1"], "rule_id": gold.rule_id},),
        gold.required_evidence,
        (),
    )
    assert not grade_response(question, gold, directly_constructed, corpus).strict_success

    gold = derive_gold(question, corpus)
    altered_gold = GoldAnswer.from_dict(copy.deepcopy(gold.as_dict()))
    altered_gold.answer["value"]["value"] = 999
    assert not grade_response(question, altered_gold, make_demo_response(question, gold), corpus).strict_success


def test_series_and_timestamp_answers_use_semantic_numeric_and_instant_equality():
    from fhir_question_bench.demo import make_demo_response

    corpus, questions = setup()
    question = _question(questions, "temporal-002")
    gold = derive_gold(question, corpus)
    immutable_gold = GoldAnswer.from_dict(copy.deepcopy(gold.as_dict()))
    response = make_demo_response(question, gold)
    response["answer"]["value"][0]["value"] = "12.10"
    response["answer"]["value"][0]["time"] = "2026-01-01T10:00:00+01:00"
    assert grade_response(question, immutable_gold, response, corpus).strict_success
    response["answer"]["value"].reverse()
    assert not grade_response(question, immutable_gold, response, corpus).strict_success
    response = make_demo_response(question, immutable_gold)
    response["answer"]["value"][0]["unit"] = "mg/dL"
    assert not grade_response(question, immutable_gold, response, corpus).strict_success

    question = _question(questions, "direct-001")
    gold = derive_gold(question, corpus)
    response = make_demo_response(question, gold)
    response["answer"]["value"] = "1978-04-12"
    assert grade_response(question, gold, response, corpus).strict_success
    from fhir_question_bench.grading import _answer_equal

    assert _answer_equal(
        {"kind": "timestamp", "value": "2026-01-01T09:00:00Z"},
        {"kind": "timestamp", "value": "2026-01-01T10:00:00+01:00"},
    )
    assert not _answer_equal(
        {"kind": "timestamp", "value": "2026-01-01"},
        {"kind": "timestamp", "value": "2026-01-01T00:00:00Z"},
    )


def test_public_prepared_contract_and_independent_responses(tmp_path):
    corpus, questions = setup()
    data_root = DATA
    question = _question(questions, "temporal-002")
    prepared = prepare_question(question, corpus, data_root, tmp_path / "prepared")
    assert (prepared / "response-contract.json").exists()
    assert (prepared / "selection-policies.json").exists()
    assert not list(prepared.rglob("*gold*"))
    _, public_corpus, public_question, contract, policies, protocol = _public_assets(tmp_path, question.id)
    assert contract["version"] == 1
    assert policies["version"] == 1
    assert "required_status" in policies["policies"][public_question["selection_policy"]]["criteria"]
    # Independently transcribed from prepared FHIR and the published policy.
    response = _independent_response(
        question,
        public_corpus,
        [
            {"time": "2026-01-01T09:00:00Z", "value": "12.1", "unit": "g/dL"},
            {"time": "2026-01-10T09:00:00Z", "value": "12.8", "unit": "g/dL"},
        ],
        [
            ("Observation/o-hgb-a", "/subject/reference"),
            ("Observation/o-hgb-a", "/code"),
            ("Observation/o-hgb-a", "/status"),
            ("Observation/o-hgb-a", "/effectiveDateTime"),
            ("Observation/o-hgb-a", "/valueQuantity"),
            ("Observation/o-hgb-b", "/subject/reference"),
            ("Observation/o-hgb-b", "/code"),
            ("Observation/o-hgb-b", "/status"),
            ("Observation/o-hgb-b", "/effectiveDateTime"),
            ("Observation/o-hgb-b", "/valueQuantity"),
            ("Observation/o-hgb-prelim", "/subject/reference"),
            ("Observation/o-hgb-prelim", "/code"),
            ("Observation/o-hgb-prelim", "/status"),
            ("Observation/o-hgb-prelim", "/effectiveDateTime"),
            ("Observation/o-hgb-prelim", "/valueQuantity"),
        ],
        protocol,
        {
            "protocol_id": public_question["protocol_id"],
            "selection_policy": public_question["selection_policy"],
            "selection_policy_version": policies["policy_version"],
            "resource_type": "Observation",
            "patient_reference": "Patient/p-bryn",
            "cutoff": public_question["cutoff"],
            "criteria": {
                "system": "urn:bench:local-lab",
                "code": "hemoglobin",
                "required_status": "final",
                "cutoff_inclusive": True,
            },
        },
    )
    assert grade_response(question, derive_gold(question, corpus), response, public_corpus, DATA).strict_success


def test_prepared_medication_answer_is_authored_from_public_assets_only(tmp_path):
    corpus, questions = setup()
    question = _question(questions, "join-001")
    prepared = prepare_question(question, corpus, DATA, tmp_path / "prepared")
    _, public_corpus, public_question, contract, policies, protocol = _public_assets(tmp_path, question.id)
    assert contract["version"] == policies["version"] == 1
    response = _independent_response(
        question,
        public_corpus,
        ["MedicationAdministration/ma-ada", "MedicationRequest/mr-ada"],
        [
            ("MedicationRequest/mr-ada", "/status"),
            ("MedicationRequest/mr-ada", "/subject/reference"),
            ("MedicationRequest/mr-ada", "/encounter/reference"),
            ("MedicationAdministration/ma-ada", "/status"),
            ("MedicationAdministration/ma-ada", "/subject/reference"),
            ("MedicationAdministration/ma-ada", "/context/reference"),
            ("Encounter/e-ada", "/subject/reference"),
        ],
        protocol,
    )
    assert grade_response(question, derive_gold(question, corpus), response, public_corpus, DATA).strict_success


def test_temporal_absent_quantity_is_accounted_without_fabricated_pointer(tmp_path):
    data_root = copy_fixture_data(tmp_path)
    def add_absent(resource):
        resource.update({
            "resourceType": "Observation", "id": "o-potassium-absent", "status": "final",
            "code": {"coding": [{"system": "urn:bench:local-lab", "code": "potassium"}]},
            "subject": {"reference": "Patient/p-ada"},
            "effectiveDateTime": "2026-01-13T00:00:00Z",
            "dataAbsentReason": {"coding": [{"system": "urn:bench:data-absent", "code": "unknown"}]},
        })
    source = data_root / "agent" / "corpus" / "resources.ndjson"
    source.write_bytes(source.read_bytes() + b'{"resourceType":"Observation","id":"placeholder"}\n')
    mutate_ndjson(data_root, "placeholder", add_absent)
    corpus = load_corpus(data_root / "agent" / "corpus-manifest.json")
    question = _question(load_questions(data_root / "questions.jsonl"), "temporal-001")
    gold = derive_gold(question, corpus, data_root)
    assert gold.answer["value"] == {"value": "4.1", "unit": "mmol/L"}
    assert any(item.resource_identity == "Observation/o-potassium-absent" and item.pointer.endswith("/dataAbsentReason") for item in gold.required_evidence)
    assert not any(item.resource_identity == "Observation/o-potassium-absent" and item.pointer.endswith("/valueQuantity") for item in gold.required_evidence)


def test_temporal_missing_effective_time_is_indeterminate(tmp_path):
    data_root = copy_fixture_data(tmp_path)
    mutate_ndjson(data_root, "o-potassium-old", lambda item: item.pop("effectiveDateTime"))
    corpus = load_corpus(data_root / "agent" / "corpus-manifest.json")
    question = _question(load_questions(data_root / "questions.jsonl"), "temporal-001")
    with pytest.raises(OracleError, match="effectiveDateTime"):
        derive_gold(question, corpus, data_root)


def test_nonqualifying_temporal_status_excludes_missing_time_candidate(tmp_path):
    data_root = copy_fixture_data(tmp_path)
    mutate_ndjson(
        data_root,
        "o-hgb-prelim",
        lambda item: item.pop("effectiveDateTime"),
    )
    corpus = load_corpus(data_root / "agent" / "corpus-manifest.json")
    question = _question(load_questions(data_root / "questions.jsonl"), "temporal-002")
    gold = derive_gold(question, corpus, data_root)
    assert len(gold.answer["value"]) == 2
    preliminary = [
        evidence for evidence in gold.required_evidence
        if evidence.resource_identity == "Observation/o-hgb-prelim"
    ]
    assert any(evidence.pointer.endswith("/status") for evidence in preliminary)
    assert not any(evidence.pointer.endswith("/effectiveDateTime") for evidence in preliminary)


def test_tied_latest_observations_are_indeterminate(tmp_path):
    data_root = copy_fixture_data(tmp_path)
    source = data_root / "agent" / "corpus" / "resources.ndjson"
    source.write_bytes(source.read_bytes() + (
        b'{"resourceType":"Observation","id":"o-potassium-tie","status":"final",'
        b'"code":{"coding":[{"system":"urn:bench:local-lab","code":"potassium"}]},'
        b'"subject":{"reference":"Patient/p-ada"},"effectiveDateTime":"2026-01-15T00:00:00Z",'
        b'"valueQuantity":{"value":4.2,"unit":"mmol/L"}}\n'
    ))
    refresh_corpus_hash(data_root, "corpus/resources.ndjson")
    corpus = load_corpus(data_root / "agent" / "corpus-manifest.json")
    question = _question(load_questions(data_root / "questions.jsonl"), "temporal-001")
    with pytest.raises(OracleError, match="tied"):
        derive_gold(question, corpus, data_root)


def test_public_cohort_status_policy_matches_oracle_and_scope(tmp_path):
    data_root = copy_fixture_data(tmp_path)
    cohort = _question(load_questions(data_root / "questions.jsonl"), "cohort-002")
    prepared = prepare_question(cohort, load_corpus(data_root / "agent" / "corpus-manifest.json"), data_root, tmp_path / "prepared")
    policies = json.loads((prepared / "selection-policies.json").read_text(encoding="utf-8"))
    policy = policies["policies"][cohort.selection_policy]
    assert policy["criteria"]["required_status"] == "final"
    assert "final" in cohort.prompt.lower()
    protocols = json.loads((prepared / "protocols" / "protocols.json").read_text(encoding="utf-8"))
    protocol = next(item for item in protocols["protocols"] if item["id"] == cohort.protocol_id)
    assert "final" in protocol["definition"].lower()
    mutate_ndjson(data_root, "o-screen-bryn", lambda item: item.update(status="preliminary"))
    corpus = load_corpus(data_root / "agent" / "corpus-manifest.json")
    assert derive_gold(cohort, corpus, data_root).answer["value"] == {"count": 1}
    assert derive_gold(cohort, corpus, data_root).scope_obligation["criteria"]["required_status"] == "final"


def test_scope_cutoff_accepts_equivalent_timezone_instant():
    from fhir_question_bench.demo import make_demo_response

    corpus, questions = setup()
    question = _question(questions, "cohort-002")
    gold = derive_gold(question, corpus)
    response = make_demo_response(question, gold)
    response["scope_checks"][0]["cutoff"] = "2026-01-14T19:00:00-05:00"
    assert grade_response(question, gold, response, corpus).strict_success


def test_pretty_json_gold_and_actual_grade_cli(tmp_path):
    from fhir_question_bench.demo import make_demo_response

    corpus, questions = setup()
    question = _question(questions, "direct-004")
    gold = derive_gold(question, corpus)
    gold_file = tmp_path / "gold.json"
    gold_file.write_text(json.dumps(gold.as_dict(), indent=2), encoding="utf-8")
    response_file = tmp_path / "response.json"
    response_file.write_text(json.dumps(make_demo_response(question, gold)), encoding="utf-8")
    output = tmp_path / "grade.json"
    env = dict(os.environ)
    env["PYTHONPATH"] = str(Path(__file__).parents[1] / "src")
    completed = subprocess.run(
        [sys.executable, "-m", "fhir_question_bench", "grade", "--question-id", question.id,
         "--response", str(response_file), "--gold", str(gold_file), "--out", str(output)],
        cwd=tmp_path, env=env, capture_output=True, text=True, check=False,
    )
    assert completed.returncode == 0, completed.stderr
    assert json.loads(output.read_text(encoding="utf-8"))["strict_success"]


def test_malformed_pretty_gold_has_actionable_cli_error(tmp_path):
    from fhir_question_bench.cli import _read_gold

    path = tmp_path / "gold.json"
    path.write_text("{\n  \"not\": \"gold\"\n}\n", encoding="utf-8")
    with pytest.raises(ValueError, match="gold file"):
        _read_gold(path, "direct-004")


def test_grade_cli_reports_malformed_pretty_gold(tmp_path):
    response = tmp_path / "response.json"
    response.write_text("{}", encoding="utf-8")
    gold = tmp_path / "gold.json"
    gold.write_text("{\n  bad json\n}\n", encoding="utf-8")
    output = tmp_path / "grade.json"
    env = dict(os.environ)
    env["PYTHONPATH"] = str(Path(__file__).parents[1] / "src")
    completed = subprocess.run(
        [sys.executable, "-m", "fhir_question_bench", "grade", "--question-id", "direct-004",
         "--response", str(response), "--gold", str(gold), "--out", str(output)],
        cwd=tmp_path, env=env, capture_output=True, text=True, check=False,
    )
    assert completed.returncode == 2
    assert "invalid gold JSON" in completed.stderr
