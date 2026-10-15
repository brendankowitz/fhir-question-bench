"""Independent hand-derived checks in addition to corpus recomputation."""
from __future__ import annotations

import copy
import json
import os
import subprocess
import sys
from collections import Counter
from decimal import Decimal
from pathlib import Path

import pytest

from fhir_question_bench import operations as ops
from fhir_question_bench.operations_reference import (
    deidentify, evaluate_path, expand, map_resource, validate, validate_code,
    translate, view_rows,
)


def test_deidentification_removes_scoped_fields_and_preserves_references():
    source = {"resourceType": "Bundle", "type": "collection", "entry": [
        {"fullUrl": "urn:uuid:alice", "resource": {
            "resourceType": "Patient", "id": "alice", "name": [{"text": "Synthetic Alice"}],
            "birthDate": "1930-02-28", "address": [{"postalCode": "12345", "city": "Fakeville"}]}},
        {"resource": {"resourceType": "Observation", "id": "lab", "status": "final",
                      "subject": {"reference": "urn:uuid:alice"},
                      "effectiveDateTime": "2024-02-28T23:00:00Z", "note": [{"text": "Remove me"}]}}
    ]}
    original = copy.deepcopy(source)
    policy = {
        "remove_fields": ["name", "note", "city"],
        "year_fields": ["birthDate"], "shift_fields": ["effectiveDateTime"],
        "shift_days": 2, "zip3": True, "small_zip_prefixes": ["123"],
        "id_map": {"Patient/alice": "Patient/p1", "urn:uuid:alice": "urn:uuid:p1",
                   "Observation/lab": "Observation/o1"},
    }
    actual = deidentify(source, policy)
    assert actual["entry"][0] == {"fullUrl": "urn:uuid:p1", "resource": {
        "resourceType": "Patient", "id": "p1", "birthDate": "1930",
        "address": [{"postalCode": "000"}]}}
    assert actual["entry"][1]["resource"] == {
        "resourceType": "Observation", "id": "o1", "status": "final",
        "subject": {"reference": "urn:uuid:p1"}, "effectiveDateTime": "2024-03-01T23:00:00Z"}
    assert source == original


def test_deidentification_patient_offsets_apply_to_all_dates_including_periods():
    source = {"resourceType": "Encounter", "id": "e", "subject": {"reference": "Patient/a"},
              "period": {"start": "2024-02-28T00:00:00Z", "end": "2024-03-01T00:00:00Z"}}
    policy = {"remove_fields": [], "year_fields": [], "shift_fields": ["start", "end"],
              "shift_days": 0, "patient_shift_days": {"Patient/a": -1}, "id_map": {}}
    assert deidentify(source, policy)["period"] == {
        "start": "2024-02-27T00:00:00Z", "end": "2024-02-29T00:00:00Z"}


def test_fhirpath_collection_null_and_filter_semantics():
    resource = {"component": [{"code": {"text": "A"}, "valueInteger": 0},
                              {"code": {"text": "B"}, "valueInteger": 2}]}
    assert evaluate_path(resource, "component.where(code.text = 'A').valueInteger") == [0]
    assert evaluate_path(resource, "component.valueInteger.first()") == [0]
    assert evaluate_path(resource, "missing.exists()") == [False]
    with pytest.raises(ValueError, match="unsupported"):
        evaluate_path(resource, "component.resolve()")


def test_structuremap_copy_create_constant_condition_and_atomic_failure():
    mapping = {"resourceType": "StructureMap", "group": [{
        "input": [{"name": "src", "mode": "source"}, {"name": "tgt", "mode": "target"}],
        "rule": [
            {"name": "id", "source": [{"context": "src", "element": "id", "variable": "v", "min": 1}],
             "target": [{"context": "tgt", "element": "id", "transform": "copy",
                         "parameter": [{"valueId": "v"}]}]},
            {"name": "active", "source": [{"context": "src", "condition": "active = true"}],
             "target": [{"context": "tgt", "element": "active", "transform": "copy",
                         "parameter": [{"valueBoolean": True}]}]},
        ]}], "structure": [{"url": "http://hl7.org/fhir/StructureDefinition/Patient",
                           "mode": "target", "alias": "Patient"}]}
    result = map_resource({"resourceType": "Patient", "id": "x", "active": False}, mapping)
    assert result == {"resourceType": "Patient", "id": "x"}
    failure = map_resource({"resourceType": "Patient"}, mapping)
    assert failure["resourceType"] == "OperationOutcome"
    assert [(i["severity"], i["code"]) for i in failure["issue"]] == [("error", "required")]


def test_viewdefinition_outer_unnest_multiset_and_typed_nulls():
    resources = [{"resourceType": "Patient", "id": "a", "name": [{"given": ["Al", "A"]}]},
                 {"resourceType": "Patient", "id": "b"}]
    view = {"resourceType": "ViewDefinition", "resource": "Patient", "select": [
        {"column": [{"name": "id", "path": "id", "type": "id"}]},
        {"forEachOrNull": "name.given", "column": [{"name": "given", "path": "$this", "type": "string"}]}
    ]}
    assert view_rows(resources, view) == {"columns": [{"name": "id", "type": "id"},
                                                      {"name": "given", "type": "string"}],
                                         "rows": [{"id": "a", "given": "Al"}, {"id": "a", "given": "A"},
                                                  {"id": "b", "given": None}]}


def test_validation_hand_derived_rule_location_severity_and_success():
    profile = {"resourceType": "StructureDefinition", "type": "Observation", "differential": {"element": [
        {"id": "Observation.status", "path": "Observation.status", "min": 1, "max": "1",
         "fixedCode": "final"},
        {"id": "Observation.valueQuantity", "path": "Observation.valueQuantity", "min": 1,
         "max": "1", "type": [{"code": "Quantity"}]},
    ]}}
    actual = validate({"resourceType": "Observation", "id": "x", "status": "preliminary"}, profile, {})
    assert {(i["severity"], i["code"], tuple(i["expression"])) for i in actual["issue"]} == {
        ("error", "value", ("Observation.status",)),
        ("error", "required", ("Observation.valueQuantity",))}
    valid = validate({"resourceType": "Observation", "status": "final", "valueQuantity": {"value": 0}},
                     profile, {})
    assert [(i["severity"], i["code"]) for i in valid["issue"]] == [("information", "informational")]


def test_owned_terminology_never_guesses_or_drops_many_targets():
    terminology = ops.read_json(ops.OPERATIONS_ROOT / "agent/terminology.json")
    assert translate({"system": "urn:ops:source", "version": "1", "code": "many"}, terminology)[
        "parameter"][0] == {"name": "result", "valueBoolean": True}
    result = translate({"system": "urn:ops:source", "version": "1", "code": "many"}, terminology)
    assert len([p for p in result["parameter"] if p["name"] == "match"]) == 2
    assert validate_code({"system": "urn:ops:source", "version": "1", "code": "retired",
                          "activeOnly": True}, terminology)["parameter"][0]["valueBoolean"] is False
    assert translate({"system": "urn:ops:source", "version": "99", "code": "a"}, terminology)[
        "parameter"][0]["valueBoolean"] is False
    expanded = expand({"url": "urn:ops:values", "activeOnly": True, "offset": 1, "count": 1}, terminology)
    assert expanded["expansion"]["total"] == 3
    assert len(expanded["expansion"]["contains"]) == 1


def test_exact_50_unverified_with_balanced_categories_and_tiers():
    tasks = ops.load_tasks()
    assert len(tasks) == len({t["id"] for t in tasks}) == 50
    assert Counter(t["category"] for t in tasks) == {
        "deidentification": 10, "mapping": 10, "view": 10, "validation": 10, "mixed": 10}
    assert Counter(t["tier"] for t in tasks) == {1: 10, 2: 10, 3: 10, 4: 10, 5: 10}
    for task in tasks:
        assert task["verification_status"] == "unverified"
        assert task["capabilities_advantaged"]
        assert task["contract"]["version"] == 1
        assert task["contract"]["timestamp_tolerance_seconds"] == 0
        assert task["contract"]["id_policy"] == "exact"
        assert task["inputs"]
        assert "private" not in json.dumps(task)


def test_audit_recomputes_gold_and_rejects_all_mutations():
    result = ops.audit_operations()
    assert result["task_count"] == result["recomputed"] == result["gold_accepted"] == 50
    assert result["wrong_outputs_rejected"] >= 250
    assert result["live_execution"] == "blocked"


@pytest.mark.parametrize("index", range(50))
def test_each_task_gold_and_wrong_whole_outputs(index):
    task = ops.load_tasks()[index]
    expected = ops.load_expected(task)
    assert ops.recompute_task(task) == expected
    assert ops.grade_artifacts(task, expected, expected)["strict_success"]
    mutations = ops.wrong_outputs(task, expected)
    assert {"extra_file", "missing_file", "wrong_value", "partial", "duplicate"} <= mutations.keys()
    for label, wrong in mutations.items():
        assert not ops.grade_artifacts(task, expected, wrong)["strict_success"], (task["id"], label)


def test_grader_normalizes_only_declared_arrays_and_diagnostic_wording():
    task = next(t for t in ops.load_tasks() if t["id"] == "validation-05")
    gold = ops.load_expected(task)
    actual = copy.deepcopy(gold)
    actual["result.json"]["issue"].reverse()
    for issue in actual["result.json"]["issue"]:
        issue["diagnostics"] = "Equivalent independently phrased diagnostic"
    assert ops.grade_artifacts(task, gold, actual)["strict_success"]
    actual["result.json"]["issue"][0]["severity"] = "information"
    assert not ops.grade_artifacts(task, gold, actual)["strict_success"]


def test_grader_allows_optional_details_text_without_ignoring_coded_details():
    task = next(t for t in ops.load_tasks() if t["id"] == "validation-01")
    gold = ops.load_expected(task)
    actual = copy.deepcopy(gold)
    actual["result.json"]["issue"][0]["details"] = {"text": "No violations under the supplied rules"}
    assert ops.grade_artifacts(task, gold, actual)["strict_success"]
    actual["result.json"]["issue"][0]["details"]["coding"] = [{"system": "urn:ops:constraint", "code": "extra"}]
    assert not ops.grade_artifacts(task, gold, actual)["strict_success"]


def test_table_duplicate_mutation_duplicates_a_row_not_a_schema_column():
    task = next(t for t in ops.load_tasks() if t["id"] == "view-09")
    gold = ops.load_expected(task)
    duplicate = ops.wrong_outputs(task, gold)["duplicate"]["result.json"]
    assert duplicate["columns"] == gold["result.json"]["columns"]
    assert len(duplicate["rows"]) == len(gold["result.json"]["rows"]) + 1


@pytest.mark.parametrize("identity,expected", [
    ("mapping-01", {"resourceType": "Patient", "id": "a", "active": True}),
    ("mapping-04", {"resourceType": "Patient", "id": "a",
                    "name": [{"family": "Synthetic", "given": ["First", "Second"]}, {"given": ["Alias"]}]}),
    ("mapping-05", {"resourceType": "Patient", "id": "a",
                    "identifier": [{"system": "urn:ops:export-id", "value": "a"}]}),
    ("mapping-06", {"resourceType": "Observation", "id": "a", "status": "final",
                    "subject": {"reference": "Patient/a"}, "valueBoolean": True,
                    "code": {"coding": [{"system": "urn:ops:derived", "code": "active-marker",
                                          "display": "Owned synthetic active marker"}]}}),
    ("mapping-07", {"resourceType": "Observation", "id": "c", "status": "final",
                    "subject": {"reference": "Patient/a"}, "code": {"text": "Owned finding"}}),
    ("mapping-08", {"resourceType": "OperationOutcome", "issue": [
        {"severity": "error", "code": "required", "expression": ["src.subject"]}]}),
    ("mapping-10", {"resourceType": "Observation", "id": "a", "status": "final", "valueInteger": 3,
                    "subject": {"reference": "Patient/a"},
                    "code": {"coding": [{"system": "urn:ops:derived", "code": "given-name-count",
                                          "display": "Owned synthetic given-name count"}]}}),
])
def test_authored_maps_have_hand_derived_outputs(identity, expected):
    task = next(t for t in ops.load_tasks() if t["id"] == identity)
    assert ops.recompute_task(task) == {"result.json": expected}


def test_nested_component_map_and_scalar_selection_have_hand_derived_quantities():
    tasks = {t["id"]: t for t in ops.load_tasks()}
    scalar = ops.recompute_task(tasks["mapping-03"])["result.json"]
    assert scalar["valueQuantity"] == {"value": 2, "unit": "mg"}
    assert "component" not in scalar
    nested = ops.recompute_task(tasks["mapping-09"])["result.json"]
    assert nested["component"] == [
        {"code": {"text": "A"}, "valueQuantity": {"value": 1, "unit": "mg"}},
        {"code": {"text": "B"}, "valueQuantity": {"value": 1, "unit": "mg"}}]
    normalized = ops.recompute_task(tasks["mapping-02"])["result.json"]
    assert normalized["status"] == "final"
    assert normalized["valueQuantity"] == {
        "value": 0, "system": "http://unitsofmeasure.org", "code": "mg", "unit": "mg"}


@pytest.mark.parametrize("identity,expected", [
    ("view-01", [{"patient_id": "a", "active": True}, {"patient_id": "b", "active": False}]),
    ("view-02", [{"id": "o1", "value": 0}, {"id": "o3", "value": 3}]),
    ("view-03", [{"id": "o1", "value": 0, "unit": "mg"},
                 {"id": "o2", "value": 7.5, "unit": "mg"},
                 {"id": "o3", "value": 3, "unit": "g"}, {"id": "o4", "value": None, "unit": None}]),
    ("view-04", [{"id": "a", "given": "Person"}, {"id": "a", "given": "A"}]),
    ("view-05", [{"id": "a", "given": "Person"}, {"id": "a", "given": "A"}, {"id": "b", "given": None}]),
    ("view-06", [{"id": "o3", "panel": "panel-a", "value": 2},
                 {"id": "o3", "panel": "panel-b", "value": 4},
                 {"id": "o4", "panel": "panel-a", "value": 2}]),
    ("view-07", [{"id": "a", "given_names": ["Person", "A"]}, {"id": "b", "given_names": []}]),
    ("view-08", [{"id": "o1", "value": 0, "origin": "scalar"},
                 {"id": "o2", "value": 7.5, "origin": "scalar"},
                 {"id": "o3", "value": 3, "origin": "scalar"},
                 {"id": "o3", "value": 2, "origin": "component"},
                 {"id": "o3", "value": 4, "origin": "component"},
                 {"id": "o4", "value": None, "origin": "scalar"},
                 {"id": "o4", "value": 2, "origin": "component"}]),
    ("view-09", [{"subject": "Patient/b", "value": 2, "unit": "mg"},
                 {"subject": "Patient/a", "value": 2, "unit": "mg"},
                 {"subject": "Patient/b", "value": 2, "unit": "mg"}]),
    ("view-10", [{"id": "a", "given": "Person", "system": "urn:ops:record-number", "identifier": "SYN-a"},
                 {"id": "a", "given": "Person", "system": "urn:ops:secondary", "identifier": "ALT-a"},
                 {"id": "a", "given": "A", "system": "urn:ops:record-number", "identifier": "SYN-a"},
                 {"id": "a", "given": "A", "system": "urn:ops:secondary", "identifier": "ALT-a"},
                 {"id": "b", "given": None, "system": "urn:ops:record-number", "identifier": "SYN-b"}]),
])
def test_all_authored_views_have_hand_derived_rows(identity, expected):
    task = next(t for t in ops.load_tasks() if t["id"] == identity)
    assert ops.recompute_task(task)["result.json"]["rows"] == expected


@pytest.mark.parametrize("number,expected", [
    (1, [("information", "informational", "Observation")]),
    (2, [("error", "required", "Observation.status")]),
    (3, [("error", "value", "Observation.status")]),
    (4, [("error", "structure", "Patient.name")]),
    (5, [("error", "value", "Observation.status"), ("error", "required", "Observation.valueQuantity")]),
    (6, [("error", "value", "Observation.valueQuantity"), ("error", "structure", "Observation.valueQuantity.value")]),
    (7, [("error", "structure", "Observation.value[x]")]),
    (8, [("error", "code-invalid", "Observation.code")]),
    (9, [("warning", "invariant", "Observation"), ("error", "code-invalid", "Observation.code")]),
    (10, [("error", "not-found", "Observation.subject.reference")]),
])
def test_all_authored_validation_issue_tuples_are_hand_derived(number, expected):
    task = next(t for t in ops.load_tasks() if t["id"] == f"validation-{number:02d}")
    issues = ops.recompute_task(task)["result.json"]["issue"]
    assert [(i["severity"], i["code"], i["expression"][0]) for i in issues] == expected
    if number == 9:
        assert issues[0]["details"]["coding"] == [{"system": "urn:ops:constraint", "code": "subject-present"}]


def test_owned_terminology_exact_matches_membership_display_and_expansion():
    term = ops.read_json(ops.OPERATIONS_ROOT / "agent/terminology.json")
    request = {"system": "urn:ops:source", "version": "1", "code": "a"}
    assert translate(request, term) == {"resourceType": "Parameters", "parameter": [
        {"name": "result", "valueBoolean": True}, {"name": "match", "part": [
            {"name": "equivalence", "valueCode": "equivalent"},
            {"name": "concept", "valueCoding": {"system": "urn:ops:target", "version": "1",
                                              "code": "x", "display": "Owned destination X"}}]}]}
    for overrides in ({"system": "urn:ops:other"}, {"version": "2"}, {"code": "unknown"},
                      {"code": "context"}, {"code": "retired"}):
        assert translate({**request, **overrides}, term) == {
            "resourceType": "Parameters", "parameter": [{"name": "result", "valueBoolean": False}]}
    for specimen, target in [("serum", "x"), ("urine", "z")]:
        actual = translate({**request, "code": "context", "context": {"urn:ops:specimen": specimen}}, term)
        assert actual["parameter"][1]["part"][1]["valueCoding"]["code"] == target
    for overrides in ({"display": "wrong display"}, {"code": "A"}, {"version": "99"},
                      {"code": "context", "valueSet": "urn:ops:values"}):
        assert not validate_code({**request, **overrides}, term)["parameter"][0]["valueBoolean"]
    filtered = expand({"url": "urn:ops:values", "activeOnly": True, "filter": "SIGNAL"}, term)
    assert [c["code"] for c in filtered["expansion"]["contains"]] == ["a", "b", "many"]
    page = expand({"url": "urn:ops:values", "activeOnly": True, "offset": 1, "count": 1}, term)
    assert page["expansion"]["contains"] == [{"system": "urn:ops:source", "version": "1",
                                              "code": "b", "display": "Owned signal B"}]
    assert [c["code"] for c in expand({"url": "urn:ops:values"}, term)["expansion"]["contains"]] == [
        "a", "b", "many", "retired"]


def test_privacy_date_and_reference_invariants_across_authored_bundles():
    tasks = {t["id"]: t for t in ops.load_tasks()}
    per_patient = ops.recompute_task(tasks["deidentification-06"])["result.json"]
    assert [e["resource"]["effectiveDateTime"] for e in per_patient["entry"][2:]] == [
        "2024-03-02T00:00:00Z", "2024-02-24T00:00:00Z"]
    contained = ops.recompute_task(tasks["deidentification-07"])["result.json"]
    assert contained["subject"] == {"reference": "#anon"}
    assert contained["contained"][0]["id"] == "anon"
    assert "name" not in contained["contained"][0]
    aliases = ops.recompute_task(tasks["deidentification-05"])["result.json"]
    assert aliases["entry"][0]["fullUrl"] == aliases["entry"][1]["resource"]["subject"]["reference"]
    assert aliases["entry"][0]["request"]["url"] == "Patient/p100"
    capped = ops.recompute_task(tasks["deidentification-08"])["result.json"]
    assert [(e["resource"]["age"], e["resource"]["address"][0]["postalCode"])
            for e in capped["entry"]] == [(90, "000"), (89, "987")]
    nested = ops.recompute_task(tasks["deidentification-10"])["result.json"]
    assert nested["entry"][1]["resource"]["component"][0]["code"]["text"] == "owned panel label"
    assert nested["entry"][2]["resource"]["period"] == {
        "start": "2024-01-01T23:00:00Z", "end": "2024-01-02T01:00:00Z"}
    assert nested["entry"][2]["resource"]["participant"][0]["individual"]["reference"] == "#staff"


def test_mixed_pipeline_uses_prior_output_not_original_and_requires_all_artifacts():
    tasks = {t["id"]: t for t in ops.load_tasks()}
    result = ops.recompute_task(tasks["mixed-08"])
    assert result["result.json"]["rows"] == [{"id": "anon-lab", "subject": "Patient/anon", "value": 0}]
    assert result["sanitized.json"]["entry"][1]["resource"]["effectiveDateTime"] == "2024-03-01T00:00:00Z"
    assert ops.recompute_task(tasks["mixed-09"])["result.json"]["issue"] == [
        {"severity": "error", "code": "code-invalid", "expression": ["Observation.code"]}]
    last = ops.recompute_task(tasks["mixed-10"])
    assert last["result.json"]["parameter"] == [{"name": "result", "valueBoolean": False}]
    assert {(i["code"], i["expression"][0]) for i in last["validation.json"]["issue"]} == {
        ("value", "Observation.status"), ("code-invalid", "Observation.code")}


def test_numeric_and_timestamp_normalization_is_typed_exact_and_scoped():
    tasks = {t["id"]: t for t in ops.load_tasks()}
    task = tasks["view-02"]
    gold = ops.load_expected(task)
    actual = copy.deepcopy(gold)
    actual["result.json"]["rows"][0]["value"] = 0.0
    assert ops.grade_artifacts(task, gold, actual)["strict_success"]
    for wrong in (False, "0", None):
        actual["result.json"]["rows"][0]["value"] = wrong
        assert not ops.grade_artifacts(task, gold, actual)["strict_success"]
    task = tasks["deidentification-02"]
    gold = ops.load_expected(task)
    actual = copy.deepcopy(gold)
    actual["result.json"]["effectiveDateTime"] = "2024-02-28T01:00:00+01:00"
    assert ops.grade_artifacts(task, gold, actual)["strict_success"]
    actual["result.json"]["effectiveDateTime"] = "2024-02-28T00:00:01Z"
    assert not ops.grade_artifacts(task, gold, actual)["strict_success"]
    actual = copy.deepcopy(gold)
    actual["result.json"]["id"] = "different-id"
    assert not ops.grade_artifacts(task, gold, actual)["strict_success"]


def test_output_collector_rejects_invalid_duplicate_key_json_and_symlinks(tmp_path):
    task = ops.load_tasks()[0]
    gold = ops.load_expected(task)
    output = tmp_path / "outputs"
    output.mkdir()
    result = output / "result.json"
    for text in ('{"a":1,"a":2}', '{"value":NaN}', '{"truncated":'):
        result.write_text(text, encoding="utf-8")
        assert not ops.grade_output_tree(task, gold, output)["strict_success"]
    result.unlink()
    external = tmp_path / "outside.json"
    external.write_text(json.dumps(gold["result.json"]), encoding="utf-8")
    try:
        result.symlink_to(external)
    except OSError:
        return  # Windows without Developer Mode: hardlink rejection is tested separately.
    assert not ops.grade_output_tree(task, gold, output)["strict_success"]


def test_json_decimal_precision_is_not_lost_during_collection(tmp_path):
    path = tmp_path / "number.json"
    path.write_text('{"number":1.00000000000000000001}', encoding="utf-8")
    parsed = ops.read_json(path)
    assert parsed["number"] == Decimal("1.00000000000000000001")
    assert ops.canonical(parsed) == b'{"number":1.00000000000000000001}'
    task = next(t for t in ops.load_tasks() if t["id"] == "view-02")
    gold = ops.load_expected(task)
    gold["result.json"]["rows"][0]["value"] = Decimal("1.00000000000000000001")
    actual = copy.deepcopy(gold)
    actual["result.json"]["rows"][0]["value"] = Decimal("1.00000000000000000002")
    assert not ops.grade_artifacts(task, gold, actual)["strict_success"]
    assert ops.grade_artifacts(task, gold, gold)["strict_success"]


def test_structuremap_rejects_wrong_source_type_without_partial_target():
    mapping = ops.read_json(ops.OPERATIONS_ROOT / "agent/inputs/mapping-01/map.json")
    actual = map_resource({"resourceType": "Observation", "id": "not-a-patient"}, mapping)
    assert actual == {"resourceType": "OperationOutcome", "issue": [
        {"severity": "error", "code": "structure", "expression": ["Observation"]}]}


def test_recomputation_binds_public_inputs_to_the_task_contract_hashes():
    task = copy.deepcopy(ops.load_tasks()[0])
    first = task["inputs"][0]
    task["contract"]["input_sha256"][first] = "0" * 64
    with pytest.raises(ValueError, match="input hash"):
        ops.recompute_task(task)


def test_collector_hashes_entire_tree_and_rejects_extras_links_and_unsafe_paths(tmp_path):
    task = ops.load_tasks()[0]
    gold = ops.load_expected(task)
    for name, artifact in gold.items():
        (tmp_path / name).write_text(json.dumps(artifact), encoding="utf-8")
    grade = ops.grade_output_tree(task, gold, tmp_path)
    assert grade["strict_success"]
    assert set(grade["output_sha256"]) == set(gold)
    (tmp_path / "unaltered-copy.txt").write_text("synthetic protected content", encoding="utf-8")
    assert not ops.grade_output_tree(task, gold, tmp_path)["strict_success"]
    (tmp_path / "unaltered-copy.txt").unlink()
    (tmp_path / "unexpected-empty-dir").mkdir()
    assert not ops.grade_output_tree(task, gold, tmp_path)["strict_success"]
    (tmp_path / "unexpected-empty-dir").rmdir()
    (tmp_path / "result.json").unlink()
    target = tmp_path.parent / (tmp_path.name + "-outside.json")
    target.write_text(json.dumps(gold["result.json"]), encoding="utf-8")
    os.link(target, tmp_path / "result.json")
    assert not ops.grade_output_tree(task, gold, tmp_path)["strict_success"]
    for name in ("../escape.json", "/absolute.json", "C:/drive.json", "x\\y.json", "./result.json"):
        assert not ops.grade_artifacts(task, gold, {name: {}})["strict_success"]


def test_manifest_tamper_is_detected(tmp_path):
    import shutil
    shutil.copytree(ops.OPERATIONS_ROOT, tmp_path / "operations")
    root = tmp_path / "operations"
    tasks = ops.load_tasks(root)
    path = root / "agent" / tasks[0]["inputs"][0]
    path.write_text("{}", encoding="utf-8")
    with pytest.raises(ValueError, match="hash"):
        ops.audit_operations(root)


def test_prepare_exports_only_public_selected_assets_and_never_gold(tmp_path):
    task = ops.load_tasks()[0]
    output = tmp_path / "prepared"
    ops.prepare_operation(task["id"], output)
    assert (output / "task.json").is_file()
    assert not any("private" in p.parts or "expected" in p.parts for p in output.rglob("*"))
    assert set(p.relative_to(output).as_posix() for p in output.rglob("*") if p.is_file()) == {
        "task.json", *task["inputs"]}
    with pytest.raises(ValueError, match="non-empty"):
        ops.prepare_operation(task["id"], output)


def test_cli_audit_is_offline_and_independent_of_qa_dataset(tmp_path):
    completed = subprocess.run([sys.executable, "-m", "fhir_question_bench", "--data-root", str(tmp_path),
                                "operations-audit", "--operations-root", str(ops.OPERATIONS_ROOT)],
                               capture_output=True, text=True, timeout=60)
    assert completed.returncode == 0, completed.stderr
    result = json.loads(completed.stdout)
    assert result["task_count"] == 50
    assert result["measured_agent_performance"] is False
