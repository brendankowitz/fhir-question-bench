from __future__ import annotations

import json
import hashlib
import copy
import shutil
from pathlib import Path

import pytest

from fhir_question_bench.corpus import CorpusError, load_corpus
from fhir_question_bench.references import resolve_reference


DATA = Path(__file__).parents[1] / "src" / "fhir_question_bench" / "data" / "agent"


def test_fixture_corpus_has_verified_hashes_and_both_formats() -> None:
    corpus = load_corpus(DATA / "corpus-manifest.json")
    assert len(corpus.resources) >= 20
    assert corpus.corpus_hash
    assert any(resource.line is not None for resource in corpus.resources)
    assert any(resource.entry_index is not None for resource in corpus.resources)


def test_corrupt_source_hash_is_rejected(tmp_path: Path) -> None:
    copied = tmp_path / "data"
    shutil.copytree(DATA, copied)
    source = copied / "corpus" / "resources.ndjson"
    source.write_bytes(source.read_bytes() + b"\n")
    with pytest.raises(CorpusError, match="SHA-256"):
        load_corpus(copied / "corpus-manifest.json")


def test_loaded_resource_mutation_is_rejected() -> None:
    corpus = load_corpus(DATA / "corpus-manifest.json")
    mutated = copy.deepcopy(corpus)
    mutated.resources[0].resource["active"] = False
    with pytest.raises(CorpusError, match="changed"):
        mutated.assert_unchanged()


def test_bundle_aliases_are_scoped_and_exact(tmp_path: Path) -> None:
    bundle = {
        "resourceType": "Bundle",
        "type": "transaction",
        "entry": [
            {
                "fullUrl": "urn:uuid:patient-a",
                "resource": {"resourceType": "Patient", "id": "pa"},
            },
            {
                "fullUrl": "urn:uuid:obs-a",
                "resource": {
                    "resourceType": "Observation",
                    "id": "oa",
                    "subject": {"reference": "urn:uuid:patient-a"},
                },
            },
            {
                "fullUrl": "https://example.test/fhir/Patient/absolute",
                "resource": {"resourceType": "Patient", "id": "absolute"},
            },
        ],
    }
    path = tmp_path / "bundle.json"
    path.write_text(json.dumps(bundle), encoding="utf-8")
    manifest = {
        "version": 1,
        "corpus_id": "alias-test",
        "files": [{"path": "bundle.json", "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}],
    }
    (tmp_path / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
    corpus = load_corpus(tmp_path / "manifest.json")
    source = next(item for item in corpus.resources if item.identity == "Observation/oa")
    assert resolve_reference(source, "urn:uuid:patient-a", corpus).targets[0].identity == "Patient/pa"
    assert (
        resolve_reference(
            source,
            "https://example.test/fhir/Patient/absolute",
            corpus,
        ).targets[0].identity
        == "Patient/absolute"
    )

    other = {
        "resourceType": "Bundle",
        "type": "transaction",
        "entry": [
            {
                "fullUrl": "urn:uuid:patient-a",
                "resource": {"resourceType": "Patient", "id": "pb"},
            }
        ],
    }
    second = tmp_path / "other.json"
    second.write_text(json.dumps(other), encoding="utf-8")
    manifest["files"].append(
        {"path": "other.json", "sha256": hashlib.sha256(second.read_bytes()).hexdigest()}
    )
    (tmp_path / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
    corpus = load_corpus(tmp_path / "manifest.json")
    source = next(item for item in corpus.resources if item.identity == "Observation/oa")
    assert resolve_reference(source, "urn:uuid:patient-a", corpus).targets[0].identity == "Patient/pa"
    assert resolve_reference(source, "urn:uuid:absent", corpus).status == "missing"
    assert resolve_reference(source, "https://elsewhere.example/Patient/pa", corpus).status == "missing"


def test_contained_and_duplicate_type_id_references_are_explicit(tmp_path: Path) -> None:
    corpus = load_corpus(DATA / "corpus-manifest.json")
    parent = next(
        resource
        for resource in corpus.resources
        if any(item.get("id") == "contained-specimen" for item in resource.resource.get("contained", []))
    )
    resolved = resolve_reference(parent, "#contained-specimen", corpus)
    assert resolved.status == "resolved"
    assert resolved.targets[0].identity.endswith("#contained-specimen")

    copied_root = tmp_path / "data"
    shutil.copytree(DATA.parent, copied_root)
    manifest_path = copied_root / "agent" / "corpus-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    duplicate = {"resourceType": "Patient", "id": "p-ada", "active": True, "birthDate": "1978-04-12"}
    source = copied_root / "agent" / "corpus" / "resources.ndjson"
    source.write_bytes(source.read_bytes() + json.dumps(duplicate, separators=(",", ":")).encode() + b"\n")
    source_entry = next(item for item in manifest["files"] if item["path"] == "corpus/resources.ndjson")
    source_entry["sha256"] = hashlib.sha256(source.read_bytes()).hexdigest()
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
    duplicate_corpus = load_corpus(manifest_path)
    duplicate_parent = next(
        resource
        for resource in duplicate_corpus.resources
        if resource.identity == "Observation/o-hgb-a"
    )
    result = resolve_reference(duplicate_parent, "Patient/p-ada", duplicate_corpus)
    assert result.status == "ambiguous"


def test_utf8_bom_ndjson_keeps_honest_one_based_line_locator(tmp_path: Path) -> None:
    content = b'\xef\xbb\xbf{"resourceType":"Patient","id":"bom"}\n'
    source = tmp_path / "records.ndjson"
    source.write_bytes(content)
    manifest = {
        "version": 1,
        "corpus_id": "bom-test",
        "files": [{"path": "records.ndjson", "sha256": hashlib.sha256(content).hexdigest()}],
    }
    path = tmp_path / "manifest.json"
    path.write_text(json.dumps(manifest), encoding="utf-8")
    resource = load_corpus(path).resources[0]
    assert resource.line == 1
    assert resource.evidence("e1", "/id")["pointer"] == "/id"
    assert "byte_offset" not in resource.evidence("e1", "/id")
