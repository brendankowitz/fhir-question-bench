from __future__ import annotations

import json
from datetime import datetime, timezone
from decimal import Decimal
from pathlib import Path
from typing import Any

from .contracts import AgentResponse, ContractError, EvidenceRef, GoldAnswer, Grade, Question
from .corpus import Corpus, ResolvedResource, json_pointer_value, source_literal
from .oracle import derive_gold


def _canonical(value: Any) -> str:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def _answer_equal(left: Any, right: Any) -> bool:
    if left is None or right is None:
        return left is right
    if left.get("kind") != right.get("kind"):
        return False
    kind = left["kind"]
    a, b = left["value"], right["value"]
    if kind == "quantity":
        return Decimal(a["value"]) == Decimal(b["value"]) and a["unit"] == b["unit"]
    if kind == "timestamp":
        return _timestamp_equal(a, b)
    if kind == "series":
        return len(a) == len(b) and all(
            _timestamp_equal(actual["time"], expected["time"])
            and Decimal(actual["value"]) == Decimal(expected["value"])
            and actual["unit"] == expected["unit"]
            for actual, expected in zip(a, b)
        )
    if kind == "set":
        return sorted(_canonical(item) for item in a) == sorted(_canonical(item) for item in b)
    return _canonical(a) == _canonical(b)


def _timestamp_equal(left: str, right: str) -> bool:
    if "T" not in left or "T" not in right:
        return "T" not in left and "T" not in right and left == right
    parse = lambda value: datetime.fromisoformat(value.replace("Z", "+00:00"))
    return parse(left).astimezone(timezone.utc) == parse(right).astimezone(timezone.utc)


def _resource_for_evidence(
    evidence: EvidenceRef,
    corpus: Corpus,
) -> tuple[ResolvedResource, str] | None:
    candidates = [
        item for item in corpus.resources
        if item.identity == evidence.resource_identity
        and item.file == evidence.file
        and item.source_hash == evidence.source_hash
        and item.line == evidence.line
        and item.entry_index == evidence.entry_index
    ]
    if not candidates and "#" in evidence.resource_identity:
        parent_id, contained_id = evidence.resource_identity.rsplit("#", 1)
        parents = [
            item for item in corpus.resources
            if item.identity == parent_id
            and item.file == evidence.file
            and item.source_hash == evidence.source_hash
            and item.line == evidence.line
            and item.entry_index == evidence.entry_index
        ]
        if len(parents) == 1:
            parent = parents[0]
            for index, contained in enumerate(parent.resource.get("contained", [])):
                if contained.get("id") == contained_id:
                    wrapped = ResolvedResource(
                        resource=contained,
                        identity=evidence.resource_identity,
                        file=parent.file,
                        source_hash=parent.source_hash,
                        corpus_hash=parent.corpus_hash,
                        line=parent.line,
                        entry_index=parent.entry_index,
                        bundle_file=parent.bundle_file,
                        pointer_prefix=parent.pointer_prefix + f"/contained/{index}",
                        parent=parent,
                    )
                    return wrapped, evidence.pointer[len(wrapped.pointer_prefix):]
    if len(candidates) != 1:
        return None
    resource = candidates[0]
    relative_pointer = evidence.pointer
    if resource.pointer_prefix:
        if not relative_pointer.startswith(resource.pointer_prefix):
            return None
        relative_pointer = relative_pointer[len(resource.pointer_prefix):]
    return resource, relative_pointer


def _same_source(left: EvidenceRef, right: EvidenceRef) -> bool:
    return (
        left.corpus_hash,
        left.resource_identity,
        left.resource_reference,
        left.source_hash,
        left.file,
        left.line,
        left.entry_index,
        left.version,
    ) == (
        right.corpus_hash,
        right.resource_identity,
        right.resource_reference,
        right.source_hash,
        right.file,
        right.line,
        right.entry_index,
        right.version,
    )


def _is_ancestor(parent: str, child: str) -> bool:
    return parent == child or (parent != "" and child.startswith(parent.rstrip("/") + "/"))


def _one_level_parent(parent: str, child: str) -> bool:
    return (
        parent != ""
        and _is_ancestor(parent, child)
        and len(child.split("/")) == len(parent.split("/")) + 1
    )


def _leaf_pointers(value: Any, pointer: str) -> list[str]:
    if isinstance(value, dict) and value:
        return [
            child
            for key, item in value.items()
            for child in _leaf_pointers(item, f"{pointer}/{str(key).replace('~', '~0').replace('/', '~1')}")
        ]
    if isinstance(value, list) and value:
        return [
            child
            for index, item in enumerate(value)
            for child in _leaf_pointers(item, f"{pointer}/{index}")
        ]
    return [pointer]


def _evidence_coverage(
    actual: list[EvidenceRef],
    required: tuple[EvidenceRef, ...],
) -> bool:
    if not required:
        return not actual
    covered_actual: set[str] = set()
    for obligation in required:
        same_source = [item for item in actual if _same_source(item, obligation)]
        if any(
            item.pointer == obligation.pointer
            or _one_level_parent(item.pointer, obligation.pointer)
            for item in same_source
        ):
            covered_actual.update(item.id for item in same_source if (
                item.pointer == obligation.pointer or _one_level_parent(item.pointer, obligation.pointer)
            ))
            continue
        if isinstance(obligation.source_value, (dict, list)):
            leaves = _leaf_pointers(obligation.source_value, obligation.pointer)
            leaf_evidence = [
                item for item in same_source
                if item.pointer.startswith(obligation.pointer.rstrip("/") + "/")
            ]
            if leaves and all(
                any(_is_ancestor(item.pointer, leaf) for item in leaf_evidence)
                for leaf in leaves
            ):
                covered_actual.update(
                    item.id for item in leaf_evidence
                    if any(_is_ancestor(item.pointer, leaf) for leaf in leaves)
                )
                continue
        return False
    return len(covered_actual) == len(actual)


def _normalize_scope(value: dict[str, Any]) -> dict[str, Any]:
    normalized = dict(value)
    cutoff = normalized.get("cutoff")
    if isinstance(cutoff, str) and "T" in cutoff:
        normalized["cutoff"] = datetime.fromisoformat(cutoff.replace("Z", "+00:00")).astimezone(timezone.utc).isoformat()
    return normalized


def _validated_evidence(
    evidence: EvidenceRef,
    corpus: Corpus,
) -> tuple[bool, str | None]:
    if evidence.corpus_hash != corpus.corpus_hash:
        return False, f"evidence {evidence.id} has a stale or corrupt corpus hash"
    if corpus.file_hashes.get(evidence.file) != evidence.source_hash:
        return False, f"evidence {evidence.id} has a stale or corrupt source hash"
    if evidence.resource_reference != evidence.resource_identity:
        return False, f"evidence {evidence.id} reference does not match its resource identity"
    match = _resource_for_evidence(evidence, corpus)
    if match is None:
        return False, f"evidence {evidence.id} locator does not identify a unique source resource"
    resource, pointer = match
    try:
        literal = json_pointer_value(resource.resource, pointer)
    except (KeyError, IndexError, ValueError, TypeError):
        return False, f"evidence {evidence.id} JSON Pointer does not resolve"
    if _canonical(source_literal(literal)) != _canonical(evidence.source_value):
        return False, f"evidence {evidence.id} source literal does not match its pointer"
    return True, None


def grade_response(
    question: Question,
    gold: GoldAnswer | dict[str, Any],
    response: AgentResponse | dict[str, Any],
    corpus: Corpus,
    data_root: Path | str | None = None,
) -> Grade:
    try:
        gold = GoldAnswer.from_dict(gold.as_dict() if isinstance(gold, GoldAnswer) else gold)
        response = AgentResponse.from_dict(response.as_dict() if isinstance(response, AgentResponse) else response)
        if gold.answer is not None:
            from .contracts import validate_answer

            validate_answer(gold.answer, question.answer_type)
        if response.answer is not None:
            from .contracts import validate_answer

            validate_answer(response.answer, question.answer_type)
    except (ContractError, TypeError, ValueError) as error:
        return Grade(question.id, False, (f"response or gold contract is invalid: {error}",))
    errors: list[str] = []
    if question.id != gold.question_id or question.id != response.question_id:
        errors.append("question IDs do not match")
    try:
        current_gold = derive_gold(question, corpus, data_root or corpus.manifest_path.parent.parent)
    except (KeyError, ValueError) as error:
        return Grade(question.id, False, (f"gold cannot be recomputed from current source: {error}",))
    if _canonical(current_gold.as_dict()) != _canonical(gold.as_dict()):
        errors.append("gold is stale or does not match the currently loaded source corpus")
    if response.status != gold.status:
        errors.append("answer state does not match the independent gold")
    if not _answer_equal(response.answer, gold.answer):
        errors.append("typed answer does not match the independent gold")

    evidence_by_id = {item.id: item for item in response.evidence}
    cited_ids = {evidence_id for claim in response.claims for evidence_id in claim["evidence_ids"]}
    for claim in response.claims:
        if claim["rule_id"] != gold.rule_id:
            errors.append(f"claim {claim['id']} uses an unsupported rule")
        for evidence_id in claim["evidence_ids"]:
            if evidence_id not in evidence_by_id:
                errors.append(f"claim {claim['id']} cites missing evidence {evidence_id}")
    if not response.claims:
        errors.append("response has no evidence-bound claim")
    if len(response.claims) != 1:
        errors.append("this slice accepts exactly one answer-bound claim")
    if set(evidence_by_id) != cited_ids:
        errors.append("response contains unbound evidence or cites unknown evidence")

    for evidence in response.evidence:
        valid, reason = _validated_evidence(evidence, corpus)
        if not valid:
            errors.append(reason or f"evidence {evidence.id} is invalid")
    if not _evidence_coverage(list(response.evidence), gold.required_evidence):
        errors.append("required evidence is incomplete, irrelevant, or cites the wrong source identity")

    expected_scopes = []
    if gold.scope_obligation is not None and response.claims:
        expected_scopes = [
            {
                "claim_id": response.claims[0]["id"],
                "corpus_hash": corpus.corpus_hash,
                **gold.scope_obligation,
                "complete_scan": True,
            }
        ]
    actual_scopes = [
        {key: value for key, value in scope.items() if key != "id"}
        for scope in response.scope_checks
    ]
    if gold.scope_obligation is not None and response.claims:
        if any(scope["claim_id"] != response.claims[0]["id"] for scope in response.scope_checks):
            errors.append("scope/completeness check is not bound to the answer claim")
    if len(actual_scopes) != len(expected_scopes) or any(
        _canonical(_normalize_scope(actual)) != _canonical(_normalize_scope(expected))
        for actual, expected in zip(actual_scopes, expected_scopes)
    ):
        errors.append("scope/completeness check does not match the required absence or cohort obligation")
    return Grade(question.id, not errors, tuple(dict.fromkeys(errors)))
