from __future__ import annotations

import hashlib
import json
from datetime import date, datetime
from decimal import Decimal
from pathlib import Path
from typing import Any

from .contracts import EvidenceRef, GoldAnswer, Question
from .corpus import Corpus, ResolvedResource, json_pointer_value
from .references import _resolve_reference


class OracleError(ValueError):
    """A question cannot be independently evaluated under its declared protocol."""


DATA_ROOT = Path(__file__).parent / "data"


def verify_dataset_manifest(data_root: Path | str | None = None) -> str:
    root = Path(data_root) if data_root is not None else DATA_ROOT
    manifest_path = root / "dataset-manifest.json"
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise OracleError(f"cannot load dataset manifest: {error}") from error
    if not isinstance(manifest, dict) or manifest.get("version") != 1 or not isinstance(manifest.get("files"), list):
        raise OracleError("dataset manifest must use version 1 and include files")
    canonical = json.dumps(manifest, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()
    seen = set()
    for entry in manifest["files"]:
        if not isinstance(entry, dict) or set(entry) != {"path", "sha256"}:
            raise OracleError("dataset manifest entries require path and sha256")
        relative = entry["path"]
        if not isinstance(relative, str) or relative.startswith(("/", "\\")) or "\\" in relative:
            raise OracleError("dataset manifest paths must be relative and normalized")
        if any(part in {"", ".", ".."} for part in relative.split("/")) or relative in seen:
            raise OracleError(f"invalid or duplicate dataset manifest path {relative!r}")
        seen.add(relative)
        path = root.joinpath(*relative.split("/")).resolve()
        try:
            path.relative_to(root.resolve())
        except ValueError as error:
            raise OracleError(f"dataset source escapes root: {relative}") from error
        try:
            digest = hashlib.sha256(path.read_bytes()).hexdigest()
        except OSError as error:
            raise OracleError(f"cannot read dataset source {relative}: {error}") from error
        if digest != entry["sha256"]:
            raise OracleError(f"SHA-256 mismatch for dataset source {relative}")
    required = {
        "questions.jsonl",
        "agent/protocols/protocols.json",
        "agent/corpus-manifest.json",
        "agent/response-contract.json",
        "agent/selection-policies.json",
        "review/review-ledger.jsonl",
    }
    if seen != required:
        raise OracleError(f"dataset manifest file set differs from the required assets: {sorted(seen)}")
    return hashlib.sha256(canonical).hexdigest()


def load_questions(path: Path | str | None = None) -> list[Question]:
    source = Path(path) if path is not None else DATA_ROOT / "questions.jsonl"
    result = []
    try:
        for line_number, line in enumerate(source.read_text(encoding="utf-8-sig").splitlines(), start=1):
            if not line.strip():
                continue
            try:
                result.append(Question.from_dict(json.loads(line)))
            except (json.JSONDecodeError, ValueError) as error:
                raise OracleError(f"invalid question on line {line_number}: {error}") from error
    except OSError as error:
        raise OracleError(f"cannot load questions: {error}") from error
    ids = [question.id for question in result]
    if len(ids) != len(set(ids)):
        raise OracleError("question IDs must be unique")
    return result


def load_protocols(data_root: Path | str | None = None) -> dict[str, dict[str, Any]]:
    root = Path(data_root) if data_root is not None else DATA_ROOT
    path = root / "agent" / "protocols" / "protocols.json"
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise OracleError(f"cannot load protocol registry: {error}") from error
    protocols = document.get("protocols") if isinstance(document, dict) else None
    if not isinstance(protocols, list):
        raise OracleError("protocol registry must contain a protocols array")
    by_id = {item.get("id"): item for item in protocols if isinstance(item, dict)}
    if len(by_id) != len(protocols):
        raise OracleError("protocol IDs must be unique")
    return by_id


def load_selection_policies(data_root: Path | str | None = None) -> dict[str, Any]:
    root = Path(data_root) if data_root is not None else DATA_ROOT
    path = root / "agent" / "selection-policies.json"
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise OracleError(f"cannot load public selection policies: {error}") from error
    if (
        not isinstance(document, dict)
        or document.get("version") != 1
        or document.get("policy_version") != 1
    ):
        raise OracleError("selection policy registry must use version 1")
    policies = document.get("policies")
    if not isinstance(policies, dict) or not policies:
        raise OracleError("selection policy registry must contain policies")
    for policy_id, policy in policies.items():
        if not isinstance(policy, dict) or not isinstance(policy.get("operation"), str):
            raise OracleError(f"invalid public selection policy {policy_id!r}")
        if not isinstance(policy.get("criteria"), dict):
            raise OracleError(f"selection policy {policy_id!r} requires criteria")
        if not isinstance(policy.get("completeness"), dict):
            raise OracleError(f"selection policy {policy_id!r} requires completeness rules")
    return policies


def _single(corpus: Corpus, identity: str) -> ResolvedResource:
    matches = corpus.find(identity)
    if len(matches) != 1:
        raise OracleError(f"expected one {identity}, found {len(matches)}")
    return matches[0]


def _code(resource: dict[str, Any]) -> list[dict[str, Any]]:
    return resource.get("code", {}).get("coding", [])


def _coding_matches(resource: dict[str, Any], system: str, code: str) -> bool:
    return any(item.get("system") == system and item.get("code") == code for item in _code(resource))


def _subject_is(resource: ResolvedResource, patient_ref: str, corpus: Corpus) -> bool:
    ref = resource.resource.get("subject", {}).get("reference")
    if not ref:
        raise OracleError(f"{resource.identity} has no subject reference; patient membership is indeterminate")
    result = _resolve_reference(resource, ref, corpus)
    if result.status != "resolved":
        raise OracleError(f"{resource.identity} subject reference is {result.status}; patient membership is indeterminate")
    if result.targets[0].identity != patient_ref:
        return False
    requested = _resolve_reference(resource, patient_ref, corpus)
    if requested.status != "resolved":
        raise OracleError(f"patient scope {patient_ref!r} is {requested.status} in source context")
    return _same_location(result.targets[0], requested.targets[0])


def _same_location(left: ResolvedResource, right: ResolvedResource) -> bool:
    return (
        left.identity,
        left.file,
        left.source_hash,
        left.line,
        left.entry_index,
    ) == (
        right.identity,
        right.file,
        right.source_hash,
        right.line,
        right.entry_index,
    )


def _resolve_required(
    source: ResolvedResource,
    reference: str,
    corpus: Corpus,
    role: str,
) -> ResolvedResource:
    resolution = _resolve_reference(source, reference, corpus)
    if resolution.status != "resolved":
        raise OracleError(f"{source.identity} {role} reference is {resolution.status}")
    return resolution.targets[0]


def _instant(value: Any) -> datetime:
    if not isinstance(value, str):
        raise OracleError(f"expected FHIR dateTime, got {value!r}")
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as error:
        raise OracleError(f"invalid FHIR dateTime {value!r}") from error
    if parsed.tzinfo is None or parsed.utcoffset() is None:
        raise OracleError(f"FHIR dateTime is missing a timezone: {value!r}")
    return parsed


def _patient_resources(corpus: Corpus) -> list[ResolvedResource]:
    return [item for item in corpus.resources if item.resource.get("resourceType") == "Patient"]


def _evidence(corpus: Corpus, pointers: list[tuple[ResolvedResource, str]]) -> tuple[EvidenceRef, ...]:
    evidence = []
    seen = set()
    for resource, pointer in pointers:
        key = (resource.file, resource.source_hash, resource.identity, pointer, resource.line, resource.entry_index)
        if key in seen:
            continue
        seen.add(key)
        evidence.append(EvidenceRef.from_dict(resource.evidence(f"e{len(evidence) + 1}", pointer)))
    return tuple(evidence)


def _evidence_for(resource: ResolvedResource, *pointers: str) -> list[tuple[ResolvedResource, str]]:
    return [(resource, pointer) for pointer in pointers]


def _question_time(question: Question) -> datetime:
    return question.cutoff


def _temporal_evidence(item: ResolvedResource) -> list[tuple[ResolvedResource, str]]:
    pointers = [
        "/subject/reference",
        "/code",
        "/status",
        "/effectiveDateTime",
    ]
    if "valueQuantity" in item.resource:
        pointers.append("/valueQuantity")
    elif "dataAbsentReason" in item.resource:
        pointers.append("/dataAbsentReason")
    elif item.resource.get("status") == "final":
        raise OracleError(f"{item.identity} has neither valueQuantity nor dataAbsentReason")
    return [
        (item, pointer)
        for pointer in pointers
        if _pointer_exists(item.resource, pointer)
    ]


def _pointer_exists(resource: dict[str, Any], pointer: str) -> bool:
    try:
        json_pointer_value(resource, pointer)
        return True
    except (KeyError, IndexError, ValueError, TypeError):
        return False


def _scope(
    question: Question,
    resource_type: str,
    patient_reference: str,
    criteria: dict[str, Any],
    selection_policy_version: int = 1,
) -> dict[str, Any]:
    return {
        "protocol_id": question.protocol_id,
        "selection_policy": question.selection_policy,
        "selection_policy_version": selection_policy_version,
        "resource_type": resource_type,
        "patient_reference": patient_reference,
        "cutoff": question.cutoff.isoformat(),
        "criteria": criteria,
    }


def _query_observations(
    question: Question,
    corpus: Corpus,
    *,
    patient_ref: str | None = None,
    system: str | None = None,
    code: str | None = None,
    required_status: str | None = None,
) -> list[ResolvedResource]:
    matches = []
    for item in corpus.resources:
        resource = item.resource
        if resource.get("resourceType") != "Observation":
            continue
        if system is not None and code is not None and not _coding_matches(resource, system, code):
            continue
        if patient_ref is not None and not _subject_is(item, patient_ref, corpus):
            continue
        effective = resource.get("effectiveDateTime")
        if system is not None and code is not None and not effective:
            if required_status and resource.get("status") and resource["status"] != required_status:
                matches.append(item)
                continue
            raise OracleError(f"{item.identity} has no effectiveDateTime; cutoff membership is indeterminate")
        if effective and _instant(effective) > _question_time(question):
            continue
        matches.append(item)
    return matches


def _quantity(resource: ResolvedResource) -> dict[str, str]:
    value = resource.resource.get("valueQuantity")
    if not isinstance(value, dict) or "value" not in value or not isinstance(value.get("unit"), str):
        raise OracleError(f"{resource.identity} has no supported valueQuantity")
    decimal_value = Decimal(str(value["value"]))
    if not decimal_value.is_finite():
        raise OracleError(f"{resource.identity} has a non-finite valueQuantity")
    return {"value": format(decimal_value.normalize(), "f"), "unit": value["unit"]}


def _term_resources(corpus: Corpus) -> tuple[list[ResolvedResource], list[ResolvedResource]]:
    return (
        [item for item in corpus.resources if item.resource.get("resourceType") == "CodeSystem"],
        [item for item in corpus.resources if item.resource.get("resourceType") == "ConceptMap"],
    )


def _term_concept(corpus: Corpus, system: str, code: str) -> tuple[ResolvedResource, dict[str, Any]]:
    systems, _ = _term_resources(corpus)
    matches = []
    for source in systems:
        if source.resource.get("url") != system:
            continue
        matches.extend(
            (source, concept)
            for concept in source.resource.get("concept", [])
            if concept.get("code") == code
        )
    if len(matches) != 1:
        raise OracleError(f"expected one concept {system}|{code}, found {len(matches)}")
    return matches[0]


def _mapping_targets(
    corpus: Corpus,
    map_id: str,
    source_system: str,
    source_code: str,
) -> tuple[ResolvedResource, list[tuple[str, dict[str, Any]]]]:
    _, maps = _term_resources(corpus)
    candidates = [item for item in maps if item.resource.get("url") == map_id]
    if len(candidates) != 1:
        raise OracleError(f"expected one ConceptMap {map_id}, found {len(candidates)}")
    concept_map = candidates[0]
    found = []
    for group in concept_map.resource.get("group", []):
        if group.get("source") != source_system:
            continue
        for element in group.get("element", []):
            if element.get("code") == source_code:
                found.extend(
                    (group.get("target", ""), target)
                    for target in element.get("target", [])
                )
    return concept_map, found


def derive_gold(
    question: Question,
    corpus: Corpus,
    data_root: Path | str | None = None,
) -> GoldAnswer:
    corpus.assert_unchanged()
    if question.corpus_id != corpus.corpus_id:
        raise OracleError(f"question corpus {question.corpus_id!r} does not match loaded corpus")
    protocols = load_protocols(data_root)
    protocol = protocols.get(question.protocol_id)
    if protocol is None:
        raise OracleError(f"unknown protocol {question.protocol_id!r}")
    if protocol.get("selection_policy") != question.selection_policy:
        raise OracleError("question selection policy does not match its protocol")
    if protocol.get("answer_type") != question.answer_type:
        raise OracleError("question answer type does not match its protocol")
    operation = protocol.get("operation")
    policy = load_selection_policies(data_root).get(question.selection_policy)
    if policy is None or policy.get("operation") != operation:
        raise OracleError("question selection policy does not declare this protocol operation")
    policy_version = 1
    params = question.parameters
    pointers: list[tuple[ResolvedResource, str]] = []
    scope: dict[str, Any] | None = None
    status = "answered"
    answer: dict[str, Any] | None

    if operation == "patient-birth-date":
        patient = _single(corpus, params["patient"])
        pointers = _evidence_for(patient, "/birthDate")
        answer = {"kind": "timestamp", "value": patient.resource["birthDate"]}
    elif operation == "count-observations":
        observations = [item for item in corpus.resources if item.resource.get("resourceType") == "Observation"]
        pointers = [(item, "/resourceType") for item in observations]
        scope = _scope(
            question,
            "Observation",
            "*",
            {"count_bundle_containers": False, "count_contained_resources": False},
        )
        answer = {"kind": "aggregate", "value": {"count": len(observations)}}
    elif operation == "condition-code":
        condition = _single(corpus, params["condition"])
        pointers = _evidence_for(condition, "/code")
        coding = condition.resource["code"]["coding"][0]
        answer = {
            "kind": "code",
            "value": {
                "system": coding["system"],
                "code": coding["code"],
                "display": coding.get("display", ""),
            },
        }
    elif operation == "direct-quantity":
        resource = _single(corpus, params["observation"])
        pointers = _evidence_for(resource, "/valueQuantity")
        answer = {"kind": "quantity", "value": _quantity(resource)}
    elif operation == "latest-final-observation":
        candidates = _query_observations(
            question,
            corpus,
            patient_ref=params["patient"],
            system=params["system"],
            code=params["code"],
            required_status=policy["criteria"]["required_status"],
        )
        required_status = policy["criteria"]["required_status"]
        for item in candidates:
            if not item.resource.get("status"):
                raise OracleError(f"{item.identity} has no status; temporal eligibility is indeterminate")
            if item.resource.get("status") == required_status and not item.resource.get("valueQuantity") and not item.resource.get("dataAbsentReason"):
                raise OracleError(f"{item.identity} has neither valueQuantity nor dataAbsentReason")
        valid = [
            item for item in candidates
            if item.resource.get("status") == required_status
            and item.resource.get("valueQuantity") is not None
        ]
        if not valid:
            raise OracleError(f"protocol found no qualifying {required_status} observation")
        chosen = max(valid, key=lambda item: _instant(item.resource["effectiveDateTime"]))
        latest = _instant(chosen.resource["effectiveDateTime"])
        if sum(_instant(item.resource["effectiveDateTime"]) == latest for item in valid) > 1:
            raise OracleError("latest observation timestamp is tied; selection is indeterminate")
        pointers = [
            pair
            for item in candidates
            for pair in _temporal_evidence(item)
        ]
        scope = _scope(
            question,
            "Observation",
            params["patient"],
            {
                "system": params["system"],
                "code": params["code"],
                "required_status": required_status,
                "cutoff_inclusive": True,
            },
            policy_version,
        )
        answer = {"kind": "quantity", "value": _quantity(chosen)}
    elif operation == "final-observation-series":
        candidates = _query_observations(
            question,
            corpus,
            patient_ref=params["patient"],
            system=params["system"],
            code=params["code"],
            required_status=policy["criteria"]["required_status"],
        )
        required_status = policy["criteria"]["required_status"]
        for item in candidates:
            if not item.resource.get("status"):
                raise OracleError(f"{item.identity} has no status; temporal eligibility is indeterminate")
            if item.resource.get("status") == required_status and not item.resource.get("valueQuantity") and not item.resource.get("dataAbsentReason"):
                raise OracleError(f"{item.identity} has neither valueQuantity nor dataAbsentReason")
        valid = sorted(
            (
                item for item in candidates
                if item.resource.get("status") == required_status
                and item.resource.get("valueQuantity") is not None
            ),
            key=lambda item: (_instant(item.resource["effectiveDateTime"]), item.identity),
        )
        pointers = [
            pair
            for item in candidates
            for pair in _temporal_evidence(item)
        ]
        scope = _scope(
            question,
            "Observation",
            params["patient"],
            {
                "system": params["system"],
                "code": params["code"],
                "required_status": required_status,
                "cutoff_inclusive": True,
            },
            policy_version,
        )
        answer = {
            "kind": "series",
            "value": [
                {"time": item.resource["effectiveDateTime"], **_quantity(item)}
                for item in valid
            ],
        }
    elif operation == "active-conditions":
        conditions = [
            item for item in corpus.resources
            if item.resource.get("resourceType") == "Condition"
            and _subject_is(item, params["patient"], corpus)
            and item.resource.get("clinicalStatus", {}).get("coding", [{}])[0].get("code") == "active"
            and (not item.resource.get("onsetDateTime") or _instant(item.resource["onsetDateTime"]) <= question.cutoff)
        ]
        pointers = [
            pair
            for item in corpus.resources
            if item.resource.get("resourceType") == "Condition"
            and _subject_is(item, params["patient"], corpus)
            for pair in _evidence_for(item, "/subject/reference", "/clinicalStatus", "/onsetDateTime")
        ]
        scope = _scope(
            question,
            "Condition",
            params["patient"],
            {"clinical_status": "active", "onset_at_or_before_cutoff": True},
        )
        answer = {"kind": "set", "value": sorted(item.identity for item in conditions)}
    elif operation == "encounter-status":
        encounter = _single(corpus, params["encounter"])
        pointers = _evidence_for(encounter, "/status")
        answer = {"kind": "narrative", "value": encounter.resource["status"]}
    elif operation == "medication-order-administration-set":
        encounter_ref = params["encounter"]
        encounter = _single(corpus, encounter_ref)
        patient_ref = encounter.resource.get("subject", {}).get("reference", "")
        patient = _resolve_required(encounter, patient_ref, corpus, "subject")
        rows = []
        for item in corpus.resources:
            resource_type = item.resource.get("resourceType")
            if resource_type not in {"MedicationRequest", "MedicationAdministration"}:
                continue
            context_field = "encounter" if resource_type == "MedicationRequest" else "context"
            context_reference = item.resource.get(context_field, {}).get("reference", "")
            subject_reference = item.resource.get("subject", {}).get("reference", "")
            linked_encounter_result = _resolve_reference(item, context_reference, corpus)
            linked_patient_result = _resolve_reference(item, subject_reference, corpus)
            if (
                linked_encounter_result.status == "resolved"
                and not _same_location(linked_encounter_result.targets[0], encounter)
            ) or (
                linked_patient_result.status == "resolved"
                and not _same_location(linked_patient_result.targets[0], patient)
            ):
                continue
            if linked_encounter_result.status != "resolved":
                raise OracleError(f"{item.identity} {context_field} reference is {linked_encounter_result.status}")
            if linked_patient_result.status != "resolved":
                raise OracleError(f"{item.identity} subject reference is {linked_patient_result.status}")
            rows.append(item)
        row_locations: dict[str, tuple[Any, ...]] = {}
        for item in rows:
            location = (item.file, item.source_hash, item.line, item.entry_index)
            if item.identity in row_locations and row_locations[item.identity] != location:
                raise OracleError(f"medication identity {item.identity} is duplicated across source locations")
            row_locations[item.identity] = location
        pointers = [
            pair
            for item in rows
            for pair in _evidence_for(
                item,
                "/status",
                "/subject/reference",
                "/encounter/reference" if item.resource["resourceType"] == "MedicationRequest" else "/context/reference",
            )
        ]
        pointers.extend(_evidence_for(encounter, "/subject/reference"))
        answer = {"kind": "set", "value": sorted(item.identity for item in rows)}
    elif operation == "administration-medication-code":
        administration = _single(corpus, params["administration"])
        reference = administration.resource["medicationReference"]["reference"]
        resolution = _resolve_reference(administration, reference, corpus)
        if resolution.status != "resolved":
            raise OracleError(f"medication reference is {resolution.status}")
        medication = resolution.targets[0]
        pointers = _evidence_for(administration, "/medicationReference/reference")
        pointers.extend(_evidence_for(medication, "/code"))
        coding = medication.resource["code"]["coding"][0]
        answer = {
            "kind": "code",
            "value": {"system": coding["system"], "code": coding["code"], "display": coding.get("display", "")},
        }
    elif operation == "bundle-alias-patient":
        observation = _single(corpus, params["observation"])
        ref = observation.resource["subject"]["reference"]
        resolution = _resolve_reference(observation, ref, corpus)
        if resolution.status != "resolved":
            raise OracleError(f"subject reference is {resolution.status}")
        patient = resolution.targets[0]
        pointers = _evidence_for(observation, "/subject/reference") + _evidence_for(patient, "/id")
        answer = {"kind": "set", "value": [patient.identity]}
    elif operation == "encounter-diagnosis-conditions":
        encounter = _single(corpus, params["encounter"])
        diagnoses = []
        for item in encounter.resource.get("diagnosis", []):
            resolution = _resolve_reference(encounter, item.get("condition", {}).get("reference", ""), corpus)
            if resolution.status != "resolved":
                raise OracleError(f"encounter diagnosis reference is {resolution.status}")
            diagnoses.extend(resolution.targets)
        pointers = _evidence_for(encounter, "/diagnosis")
        pointers.extend((item, "/code") for item in diagnoses)
        answer = {"kind": "set", "value": sorted(item.identity for item in diagnoses)}
    elif operation == "terminology-lookup":
        system, code = params["system"], params["code"]
        system_resource, concept = _term_concept(corpus, system, code)
        pointers = _evidence_for(system_resource, "/concept")
        answer = {
            "kind": "code",
            "value": {"system": system, "code": concept["code"], "display": concept.get("display", "")},
        }
    elif operation in {"terminology-map-exact", "terminology-map-ambiguous", "terminology-map-absent"}:
        map_resource, targets = _mapping_targets(
            corpus, params["map_id"], params["system"], params["code"]
        )
        concept_system, _ = _term_concept(corpus, params["system"], params["code"])
        pointers = _evidence_for(map_resource, "/group") + _evidence_for(concept_system, "/concept")
        if operation == "terminology-map-exact":
            exact = [
                (target_system, target)
                for target_system, target in targets
                if target.get("equivalence") in {"equivalent", "equal"}
            ]
            if len(exact) != 1:
                raise OracleError("exact mapping protocol did not produce exactly one equivalent target")
            target_system, item = exact[0]
            answer = {
                "kind": "code",
                "value": {
                    "match": "equivalent",
                    "targets": [
                        {
                            "system": target_system,
                            "code": item["code"],
                            "equivalence": item["equivalence"],
                            **({"display": item["display"]} if item.get("display") else {}),
                        }
                    ],
                },
            }
        elif operation == "terminology-map-ambiguous":
            values = [
                {
                    "system": target_system,
                    "code": target.get("code", ""),
                    "equivalence": target.get("equivalence", ""),
                }
                for target_system, target in targets
            ]
            if len(values) < 2:
                raise OracleError("mapping ambiguity protocol requires at least two targets")
            answer = {"kind": "set", "value": sorted(values, key=lambda item: (item["system"], item["code"]))}
            status = "ambiguous"
        else:
            if targets:
                raise OracleError("unmatched mapping protocol unexpectedly has targets")
            answer = {"kind": "code", "value": {"match": "unmatched", "targets": []}}
    elif operation in {"adult-screen-cohort", "adult-screen-count"}:
        qualifying = []
        member_locations: dict[str, tuple[Any, ...]] = {}
        required_status = policy["criteria"]["required_status"]
        for observation in corpus.resources:
            if observation.resource.get("resourceType") != "Observation":
                continue
            if not _coding_matches(observation.resource, params["system"], params["code"]):
                continue
            if not observation.resource.get("status"):
                raise OracleError(f"{observation.identity} has no status; cohort eligibility is indeterminate")
            if observation.resource.get("status") != required_status:
                continue
            effective = observation.resource.get("effectiveDateTime")
            if not effective:
                raise OracleError(f"{observation.identity} has no effectiveDateTime; cohort cutoff membership is indeterminate")
            if _instant(effective) > question.cutoff:
                continue
            patient_ref = observation.resource.get("subject", {}).get("reference")
            resolution = _resolve_reference(observation, patient_ref or "", corpus)
            if resolution.status != "resolved":
                raise OracleError(f"{observation.identity} subject reference is {resolution.status}; cohort membership is indeterminate")
            patient = resolution.targets[0]
            value = observation.resource.get("valueQuantity", {}).get("value")
            if value is None:
                if observation.resource.get("dataAbsentReason"):
                    continue
                raise OracleError(f"{observation.identity} has no valueQuantity or dataAbsentReason")
            birth = date.fromisoformat(patient.resource["birthDate"])
            cutoff = question.cutoff.date()
            age = cutoff.year - birth.year - ((cutoff.month, cutoff.day) < (birth.month, birth.day))
            if type(value) in {int, Decimal} and Decimal(str(value)) >= Decimal(str(params["minimum_value"])) and age >= params["minimum_age"]:
                qualifying.append((observation, patient))
                location = (patient.file, patient.source_hash, patient.line, patient.entry_index)
                if patient.identity in member_locations and member_locations[patient.identity] != location:
                    raise OracleError(f"cohort Patient identity {patient.identity} is duplicated across source locations")
                member_locations[patient.identity] = location
        members = sorted(member_locations)
        observations = [item for item in corpus.resources if item.resource.get("resourceType") == "Observation"]
        patients = _patient_resources(corpus)
        pointers = [
            pair
            for observation in observations
            for pair in _evidence_for(
                observation,
                *[
                    pointer
                    for pointer, field in (
                        ("/subject/reference", "subject"),
                        ("/code", "code"),
                        ("/status", "status"),
                        ("/effectiveDateTime", "effectiveDateTime"),
                        ("/valueQuantity", "valueQuantity"),
                    )
                    if field in observation.resource
                ],
            )
        ]
        pointers.extend(
            pair
            for patient in patients
            for pair in _evidence_for(patient, "/birthDate")
        )
        scope = _scope(
            question,
            "Observation",
            "*",
            {
                "system": params["system"],
                "code": params["code"],
                "minimum_value": params["minimum_value"],
                "minimum_age": params["minimum_age"],
                "cutoff_inclusive": True,
                "unique_patients": True,
                "required_status": required_status,
            },
            policy_version,
        )
        if operation == "adult-screen-count":
            answer = {"kind": "aggregate", "value": {"count": len(members)}}
        else:
            answer = {"kind": "set", "value": members}
    elif operation in {"missing-weight", "absence-code"}:
        patient_ref = params["patient"]
        patient = _single(corpus, patient_ref)
        code_matches = _query_observations(
            question,
            corpus,
            patient_ref=patient_ref,
            system=params["system"],
            code=params["code"],
        )
        if operation == "missing-weight":
            pointers = _evidence_for(patient, "/id")
            pointers.extend(
                pair
                for item in code_matches
                for pair in _evidence_for(item, "/subject/reference", "/code", "/effectiveDateTime", "/valueQuantity")
            )
            if code_matches:
                answer = {"kind": "quantity", "value": _quantity(code_matches[-1])}
            else:
                status = "insufficient_evidence"
                answer = None
        else:
            pointers = _evidence_for(patient, "/id")
            answer = {"kind": "set", "value": sorted(item.identity for item in code_matches)}
        if operation == "absence-code":
            pointers.extend(
                pair
                for item in _query_observations(
                    question,
                    corpus,
                    patient_ref=patient_ref,
                    system=params["system"],
                    code=params["code"],
                )
                for pair in _evidence_for(item, "/subject/reference", "/code", "/effectiveDateTime")
            )
        scope = _scope(
            question,
            "Observation",
            patient_ref,
            {"system": params["system"], "code": params["code"], "cutoff_inclusive": True},
        )
    else:
        raise OracleError(f"unsupported protocol operation {operation!r}")

    return GoldAnswer.from_dict(
        {
            "question_id": question.id,
            "status": status,
            "answer": answer,
            "rule_id": protocol["rule_id"],
            "required_evidence": [item.as_dict() for item in _evidence(corpus, pointers)],
            **({"scope_obligation": scope} if scope is not None else {}),
        }
    )


def verify_gold(question: Question, gold: GoldAnswer, corpus: Corpus) -> None:
    actual = derive_gold(question, corpus, corpus.manifest_path.parent.parent)
    expected = json.dumps(actual.as_dict(), sort_keys=True, separators=(",", ":"), ensure_ascii=False)
    supplied = json.dumps(gold.as_dict(), sort_keys=True, separators=(",", ":"), ensure_ascii=False)
    if expected != supplied:
        raise OracleError(f"gold for {question.id} differs from the independently recomputed source result")


def audit_dataset(data_root: Path | str | None = None) -> dict[str, Any]:
    root = Path(data_root) if data_root is not None else DATA_ROOT
    dataset_hash = verify_dataset_manifest(root)
    questions = load_questions(root / "questions.jsonl")
    from .corpus import load_corpus

    corpus = load_corpus(root / "agent" / "corpus-manifest.json")
    families = {family: sum(item.primary_family == family for item in questions) for family in sorted({q.primary_family for q in questions})}
    protocols = load_protocols(root)
    review_rows = []
    for line_number, line in enumerate((root / "review" / "review-ledger.jsonl").read_text(encoding="utf-8").splitlines(), start=1):
        if not line.strip():
            continue
        try:
            review_rows.append(json.loads(line))
        except json.JSONDecodeError as error:
            raise OracleError(f"invalid review ledger line {line_number}") from error
    if (
        len(review_rows) != len(questions)
        or len({item.get("question_id") for item in review_rows}) != len(review_rows)
        or {item.get("question_id") for item in review_rows} != {item.id for item in questions}
    ):
        raise OracleError("review ledger must include exactly one row per question")
    if any(item.get("review_state") != "provisional" for item in review_rows):
        raise OracleError("the smoke dataset review ledger must remain provisional")
    gold_count = 0
    for question in questions:
        gold = derive_gold(question, corpus, root)
        if question.protocol_id not in protocols:
            raise OracleError(f"missing protocol {question.protocol_id}")
        GoldAnswer.from_dict(gold.as_dict())
        from .demo import make_demo_response
        from .grading import grade_response

        if not grade_response(question, gold, make_demo_response(question, gold), corpus).strict_success:
            raise OracleError(f"gold evidence failed validation for {question.id}")
        gold_count += 1
    if len(questions) != 20 or len(set(families.values())) != 1 or set(families.values()) != {4}:
        raise OracleError("smoke dataset must contain exactly four questions in each of five families")
    return {
        "question_count": len(questions),
        "family_counts": families,
        "corpus_id": corpus.corpus_id,
        "corpus_hash": corpus.corpus_hash,
        "dataset_hash": dataset_hash,
        "resource_count": len(corpus.resources),
        "protocol_count": len(protocols),
        "review_count": len(review_rows),
        "gold_recomputed": gold_count,
        "review_state": "provisional",
    }
