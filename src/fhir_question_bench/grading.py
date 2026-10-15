from __future__ import annotations

import json
from decimal import Decimal
from typing import Any

from .contracts import AgentResponse, EvidenceRef, GoldAnswer, Grade, Question
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
    if kind == "set":
        return sorted(_canonical(item) for item in a) == sorted(_canonical(item) for item in b)
    return _canonical(a) == _canonical(b)


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


def _locator(evidence: EvidenceRef) -> tuple[Any, ...]:
    return (
        evidence.corpus_hash,
        evidence.resource_identity,
        evidence.resource_reference,
        evidence.source_hash,
        evidence.file,
        evidence.pointer,
        evidence.line,
        evidence.entry_index,
        evidence.version,
        _canonical(evidence.source_value),
    )


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
) -> Grade:
    if isinstance(gold, dict):
        gold = GoldAnswer.from_dict(gold)
    if isinstance(response, dict):
        response = AgentResponse.from_dict(response)
    errors: list[str] = []
    if question.id != gold.question_id or question.id != response.question_id:
        errors.append("question IDs do not match")
    try:
        current_gold = derive_gold(question, corpus, corpus.manifest_path.parent.parent)
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

    actual_locators: list[tuple[Any, ...]] = []
    for evidence in response.evidence:
        valid, reason = _validated_evidence(evidence, corpus)
        if not valid:
            errors.append(reason or f"evidence {evidence.id} is invalid")
        actual_locators.append(_locator(evidence))
    required_locators = [_locator(item) for item in gold.required_evidence]
    if sorted(map(_canonical, actual_locators)) != sorted(map(_canonical, required_locators)):
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
    if _canonical(actual_scopes) != _canonical(expected_scopes):
        errors.append("scope/completeness check does not match the required absence or cohort obligation")
    return Grade(question.id, not errors, tuple(dict.fromkeys(errors)))
