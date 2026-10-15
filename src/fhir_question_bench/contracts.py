from __future__ import annotations

import re
import json
from dataclasses import dataclass
from datetime import datetime
from decimal import Decimal, InvalidOperation
from typing import Any


ANSWER_STATES = frozenset(
    {
        "answered",
        "partial",
        "insufficient_evidence",
        "ambiguous",
        "unsupported_capability",
        "invalid_data",
    }
)
ANSWER_TYPES = frozenset(
    {"boolean", "code", "quantity", "timestamp", "series", "set", "aggregate", "narrative"}
)
QUESTION_FAMILIES = frozenset(
    {
        "direct_facts",
        "temporal_status_trends",
        "cross_resource_joins",
        "owned_local_terminology",
        "cohort_missing_evidence",
    }
)
HASH_RE = re.compile(r"^[0-9a-f]{64}$")


class ContractError(ValueError):
    """A public JSON record violates the benchmark contract."""


def _object(value: Any, name: str, required: set[str], optional: set[str] = frozenset()) -> dict:
    if not isinstance(value, dict):
        raise ContractError(f"{name} must be an object")
    missing = required - value.keys()
    extra = value.keys() - required - optional
    if missing or extra:
        raise ContractError(f"{name} fields invalid; missing={sorted(missing)}, extra={sorted(extra)}")
    return value


def _nonempty(value: Any, name: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise ContractError(f"{name} must be a non-empty string")
    return value


def _aware_datetime(value: Any, name: str) -> datetime:
    if not isinstance(value, str):
        raise ContractError(f"{name} must be an ISO-8601 string")
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as error:
        raise ContractError(f"{name} must be an ISO-8601 datetime") from error
    if parsed.tzinfo is None or parsed.utcoffset() is None:
        raise ContractError(f"{name} must include a timezone")
    return parsed


def validate_answer(value: Any, answer_type: str | None = None) -> dict | None:
    if value is None:
        return None
    answer = _object(value, "answer", {"kind", "value"})
    kind = answer["kind"]
    if not isinstance(kind, str) or kind not in ANSWER_TYPES or (answer_type is not None and kind != answer_type):
        raise ContractError(f"unsupported or mismatched answer kind: {kind!r}")
    payload = answer["value"]
    if kind == "boolean" and type(payload) is not bool:
        raise ContractError("boolean answer value must be a JSON boolean")
    if kind == "timestamp":
        if not isinstance(payload, str):
            raise ContractError("timestamp answer value must be a string")
        try:
            if "T" in payload:
                _aware_datetime(payload, "timestamp answer value")
            else:
                datetime.strptime(payload, "%Y-%m-%d")
        except ValueError as error:
            raise ContractError("timestamp answer must be a date or timezone-aware datetime") from error
    if kind == "quantity":
        quantity = _object(payload, "quantity value", {"value", "unit"})
        if not isinstance(quantity["value"], str):
            raise ContractError("quantity value must be a decimal string")
        try:
            Decimal(quantity["value"])
        except InvalidOperation as error:
            raise ContractError("quantity value must be a finite decimal") from error
        if not Decimal(quantity["value"]).is_finite():
            raise ContractError("quantity value must be finite")
        _nonempty(quantity["unit"], "quantity unit")
    if kind == "code":
        if not isinstance(payload, dict):
            raise ContractError("code answer value must be an object")
        if set(payload) == {"system", "code", "display"}:
            if not all(isinstance(payload[key], str) for key in ("system", "code", "display")):
                raise ContractError("coded concept fields must be strings")
        elif set(payload) == {"match", "targets"}:
            if not isinstance(payload["match"], str) or payload["match"] not in {
                "equivalent",
                "broader",
                "narrower",
                "related",
                "unmatched",
            } or not isinstance(payload["targets"], list):
                raise ContractError("mapping answer must include a supported match and target array")
            for target in payload["targets"]:
                _object(target, "mapping target", {"system", "code", "equivalence"}, {"display"})
                if not all(isinstance(target[key], str) for key in ("system", "code", "equivalence")):
                    raise ContractError("mapping target system, code, and equivalence must be strings")
        else:
            raise ContractError("code answer must be a coded concept or mapping result")
    if kind in {"series", "set"} and not isinstance(payload, list):
        raise ContractError(f"{kind} answer value must be an array")
    if kind == "series":
        for index, item in enumerate(payload):
            row = _object(item, f"series item {index}", {"time", "value", "unit"})
            _aware_datetime(row["time"], f"series item {index}.time")
            if not isinstance(row["value"], str):
                raise ContractError(f"series item {index}.value must be a decimal string")
            try:
                number = Decimal(row["value"])
            except InvalidOperation as error:
                raise ContractError(f"series item {index}.value must be a finite decimal") from error
            if not number.is_finite():
                raise ContractError(f"series item {index}.value must be a finite decimal")
            _nonempty(row["unit"], f"series item {index}.unit")
    if kind == "set":
        try:
            if len({json.dumps(item, sort_keys=True, separators=(",", ":"), ensure_ascii=False) for item in payload}) != len(payload):
                raise ContractError("set answer must not contain duplicate members")
        except TypeError as error:
            raise ContractError("set answer members must be JSON values") from error
    if kind == "aggregate":
        aggregate = _object(payload, "aggregate value", {"count"})
        if type(aggregate["count"]) is not int or aggregate["count"] < 0:
            raise ContractError("aggregate count must be a non-negative integer")
    if kind == "narrative" and not isinstance(payload, str):
        raise ContractError("narrative answer value must be a string")
    return answer


@dataclass(frozen=True, slots=True)
class Question:
    id: str
    primary_family: str
    task_family_id: str
    difficulty: str
    persona: str
    cutoff: datetime
    corpus_id: str
    protocol_id: str
    selection_policy: str
    answer_type: str
    review_state: str
    prompt: str
    parameters: dict[str, Any]
    tier: int = 1
    verification_status: str = "provisional"
    capabilities_advantaged: tuple[str, ...] = ()

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> Question:
        fields = {
            "id",
            "primary_family",
            "task_family_id",
            "difficulty",
            "persona",
            "cutoff",
            "corpus_id",
            "protocol_id",
            "selection_policy",
            "answer_type",
            "review_state",
            "prompt",
            "parameters",
        }
        row = _object(value, "question", fields, {"tier", "verification_status", "capabilities_advantaged"})
        for key in fields - {"cutoff", "parameters", "difficulty", "primary_family", "answer_type"}:
            _nonempty(row[key], f"question.{key}")
        if not isinstance(row["primary_family"], str) or row["primary_family"] not in QUESTION_FAMILIES:
            raise ContractError(f"unknown primary family: {row['primary_family']!r}")
        if not isinstance(row["difficulty"], str) or row["difficulty"] not in {"L1", "L2", "L3", "L4", "L5"}:
            raise ContractError("question difficulty must be L1-L5")
        if not isinstance(row["answer_type"], str) or row["answer_type"] not in ANSWER_TYPES:
            raise ContractError(f"unknown answer type: {row['answer_type']!r}")
        if row["review_state"] != "provisional":
            raise ContractError("this slice only accepts provisional questions")
        if not isinstance(row["parameters"], dict):
            raise ContractError("question.parameters must be an object")
        tier = row.get("tier", int(row["difficulty"][1]))
        if type(tier) is not int or tier not in range(1, 6):
            raise ContractError("question.tier must be an integer from 1 through 5")
        verification = row.get("verification_status", "provisional")
        if verification not in ("provisional", "unverified"):
            raise ContractError("question verification_status must be provisional or unverified; expert acceptance is not implemented")
        capabilities = row.get("capabilities_advantaged", [])
        if not isinstance(capabilities, list):
            raise ContractError("question.capabilities_advantaged must be an array")
        for capability in capabilities:
            _nonempty(capability, "question.capabilities_advantaged entry")
        return cls(
            id=row["id"],
            primary_family=row["primary_family"],
            task_family_id=row["task_family_id"],
            difficulty=row["difficulty"],
            persona=row["persona"],
            cutoff=_aware_datetime(row["cutoff"], "question.cutoff"),
            corpus_id=row["corpus_id"],
            protocol_id=row["protocol_id"],
            selection_policy=row["selection_policy"],
            answer_type=row["answer_type"],
            review_state=row["review_state"],
            prompt=row["prompt"],
            parameters=row["parameters"],
            tier=tier,
            verification_status=verification,
            capabilities_advantaged=tuple(capabilities),
        )

    def as_dict(self) -> dict[str, Any]:
        return {
            "id": self.id,
            "primary_family": self.primary_family,
            "task_family_id": self.task_family_id,
            "difficulty": self.difficulty,
            "persona": self.persona,
            "cutoff": self.cutoff.isoformat(),
            "corpus_id": self.corpus_id,
            "protocol_id": self.protocol_id,
            "selection_policy": self.selection_policy,
            "answer_type": self.answer_type,
            "review_state": self.review_state,
            "prompt": self.prompt,
            "parameters": self.parameters,
            "tier": self.tier,
            "verification_status": self.verification_status,
            "capabilities_advantaged": list(self.capabilities_advantaged),
        }


@dataclass(frozen=True, slots=True)
class EvidenceRef:
    id: str
    corpus_hash: str
    resource_identity: str
    resource_reference: str
    source_hash: str
    file: str
    pointer: str
    source_value: Any
    line: int | None = None
    entry_index: int | None = None
    version: str | None = None

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> EvidenceRef:
        row = _object(
            value,
            "evidence",
            {
                "id",
                "corpus_hash",
                "resource_identity",
                "resource_reference",
                "source_hash",
                "file",
                "pointer",
                "source_value",
            },
            {"line", "entry_index", "version"},
        )
        for key in ("id", "resource_identity", "resource_reference", "file", "pointer"):
            _nonempty(row[key], f"evidence.{key}")
        for key in ("corpus_hash", "source_hash"):
            if not isinstance(row[key], str) or not HASH_RE.fullmatch(row[key]):
                raise ContractError(f"evidence.{key} must be a lowercase SHA-256")
        file_path = row["file"]
        if file_path.startswith(("/", "\\")) or "\\" in file_path or any(
            part in {"", ".", ".."} for part in file_path.split("/")
        ):
            raise ContractError("evidence.file must be a normalized relative path")
        pointer = row["pointer"]
        if pointer and not pointer.startswith("/"):
            raise ContractError("evidence.pointer must be a JSON Pointer")
        if re.search(r"~(?![01])", pointer):
            raise ContractError("evidence.pointer has an invalid JSON Pointer escape")
        line = row.get("line")
        entry_index = row.get("entry_index")
        if line is not None and (type(line) is not int or line < 1):
            raise ContractError("evidence.line must be a positive 1-based line number")
        if entry_index is not None and (type(entry_index) is not int or entry_index < 0):
            raise ContractError("evidence.entry_index must be a non-negative integer")
        if line is not None and entry_index is not None:
            raise ContractError("evidence cannot be both an NDJSON line and a Bundle entry")
        if "version" in row and row["version"] is not None:
            _nonempty(row["version"], "evidence.version")
        return cls(
            id=row["id"],
            corpus_hash=row["corpus_hash"],
            resource_identity=row["resource_identity"],
            resource_reference=row["resource_reference"],
            source_hash=row["source_hash"],
            file=file_path,
            pointer=pointer,
            source_value=row["source_value"],
            line=line,
            entry_index=entry_index,
            version=row.get("version"),
        )

    def as_dict(self) -> dict[str, Any]:
        value = {
            "id": self.id,
            "corpus_hash": self.corpus_hash,
            "resource_identity": self.resource_identity,
            "resource_reference": self.resource_reference,
            "source_hash": self.source_hash,
            "file": self.file,
            "pointer": self.pointer,
            "source_value": self.source_value,
        }
        if self.line is not None:
            value["line"] = self.line
        if self.entry_index is not None:
            value["entry_index"] = self.entry_index
        if self.version is not None:
            value["version"] = self.version
        return value


@dataclass(frozen=True, slots=True)
class GoldAnswer:
    question_id: str
    status: str
    answer: dict[str, Any] | None
    rule_id: str
    required_evidence: tuple[EvidenceRef, ...]
    scope_obligation: dict[str, Any] | None = None

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> GoldAnswer:
        row = _object(
            value,
            "gold",
            {"question_id", "status", "answer", "rule_id", "required_evidence"},
            {"scope_obligation", "tolerance"},
        )
        if "tolerance" in row:
            raise ContractError("tolerances are unsupported in this slice")
        if not isinstance(row["status"], str) or row["status"] not in ANSWER_STATES:
            raise ContractError(f"unknown answer state: {row['status']!r}")
        answer = validate_answer(row["answer"])
        if row["status"] in {"answered", "partial"} and answer is None:
            raise ContractError(f"{row['status']} gold requires an answer")
        if not isinstance(row["required_evidence"], list):
            raise ContractError("gold.required_evidence must be an array")
        evidence = tuple(EvidenceRef.from_dict(item) for item in row["required_evidence"])
        scope = row.get("scope_obligation")
        if scope is not None:
            scope = _object(
                scope,
                "scope_obligation",
                {
                    "protocol_id",
                    "selection_policy",
                    "selection_policy_version",
                    "resource_type",
                    "patient_reference",
                    "cutoff",
                    "criteria",
                },
            )
            _aware_datetime(scope["cutoff"], "scope_obligation.cutoff")
            if type(scope["selection_policy_version"]) is not int or scope["selection_policy_version"] < 1:
                raise ContractError("scope_obligation.selection_policy_version must be a positive integer")
            for key in ("protocol_id", "selection_policy", "resource_type", "patient_reference"):
                _nonempty(scope[key], f"scope_obligation.{key}")
            if not isinstance(scope["criteria"], dict):
                raise ContractError("scope_obligation.criteria must be an object")
        return cls(
            question_id=_nonempty(row["question_id"], "gold.question_id"),
            status=row["status"],
            answer=answer,
            rule_id=_nonempty(row["rule_id"], "gold.rule_id"),
            required_evidence=evidence,
            scope_obligation=scope,
        )

    def as_dict(self) -> dict[str, Any]:
        result: dict[str, Any] = {
            "question_id": self.question_id,
            "status": self.status,
            "answer": self.answer,
            "rule_id": self.rule_id,
            "required_evidence": [item.as_dict() for item in self.required_evidence],
        }
        if self.scope_obligation is not None:
            result["scope_obligation"] = self.scope_obligation
        return result


@dataclass(frozen=True, slots=True)
class AgentResponse:
    question_id: str
    status: str
    answer: dict[str, Any] | None
    claims: tuple[dict[str, Any], ...]
    evidence: tuple[EvidenceRef, ...]
    limitations: tuple[str, ...]
    scope_checks: tuple[dict[str, Any], ...] = ()

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> AgentResponse:
        row = _object(
            value,
            "response",
            {"question_id", "status", "answer", "claims", "evidence", "limitations"},
            {"scope_checks"},
        )
        if not isinstance(row["status"], str) or row["status"] not in ANSWER_STATES:
            raise ContractError(f"unknown response status: {row['status']!r}")
        if not isinstance(row["claims"], list) or not isinstance(row["evidence"], list):
            raise ContractError("response claims and evidence must be arrays")
        if not isinstance(row["limitations"], list) or any(not isinstance(x, str) for x in row["limitations"]):
            raise ContractError("response limitations must be an array of strings")
        if row["status"] in {"answered", "partial"} and row["answer"] is None:
            raise ContractError(f"{row['status']} response requires an answer")
        claims: list[dict[str, Any]] = []
        claim_ids: set[str] = set()
        for claim in row["claims"]:
            item = _object(claim, "claim", {"id", "answer_pointer", "evidence_ids", "rule_id"})
            if not all(
                isinstance(item[key], str) and item[key].strip()
                for key in ("id", "answer_pointer", "rule_id")
            ):
                raise ContractError("claim id, answer_pointer, and rule_id must be non-empty strings")
            if item["answer_pointer"] != "/answer":
                raise ContractError("claims in this slice must bind to /answer")
            if item["id"] in claim_ids:
                raise ContractError("claim IDs must be unique")
            if not isinstance(item["evidence_ids"], list) or any(
                not isinstance(evidence_id, str) for evidence_id in item["evidence_ids"]
            ):
                raise ContractError("claim.evidence_ids must be an array of strings")
            claim_ids.add(item["id"])
            claims.append(item)
        evidence = tuple(EvidenceRef.from_dict(item) for item in row["evidence"])
        if len({item.id for item in evidence}) != len(evidence):
            raise ContractError("evidence IDs must be unique")
        scopes = row.get("scope_checks", [])
        if not isinstance(scopes, list):
            raise ContractError("response.scope_checks must be an array")
        checked_scopes = []
        for item in scopes:
            scope = _object(
                item,
                "scope_check",
                {
                    "id",
                    "claim_id",
                    "corpus_hash",
                    "protocol_id",
                    "selection_policy",
                    "selection_policy_version",
                    "resource_type",
                    "patient_reference",
                    "cutoff",
                    "criteria",
                    "complete_scan",
                },
            )
            if not isinstance(scope["corpus_hash"], str) or not HASH_RE.fullmatch(scope["corpus_hash"]):
                raise ContractError("scope_check.corpus_hash must be a lowercase SHA-256")
            if type(scope["complete_scan"]) is not bool or not isinstance(scope["criteria"], dict):
                raise ContractError("scope_check requires complete_scan boolean and criteria object")
            if type(scope["selection_policy_version"]) is not int or scope["selection_policy_version"] < 1:
                raise ContractError("scope_check.selection_policy_version must be a positive integer")
            _aware_datetime(scope["cutoff"], "scope_check.cutoff")
            for key in ("id", "claim_id", "protocol_id", "selection_policy", "resource_type", "patient_reference"):
                _nonempty(scope[key], f"scope_check.{key}")
            checked_scopes.append(scope)
        return cls(
            question_id=_nonempty(row["question_id"], "response.question_id"),
            status=row["status"],
            answer=validate_answer(row["answer"]),
            claims=tuple(claims),
            evidence=evidence,
            limitations=tuple(row["limitations"]),
            scope_checks=tuple(checked_scopes),
        )

    def as_dict(self) -> dict[str, Any]:
        result = {
            "question_id": self.question_id,
            "status": self.status,
            "answer": self.answer,
            "claims": list(self.claims),
            "evidence": [item.as_dict() for item in self.evidence],
            "limitations": list(self.limitations),
        }
        if self.scope_checks:
            result["scope_checks"] = list(self.scope_checks)
        return result


@dataclass(frozen=True, slots=True)
class Grade:
    question_id: str
    strict_success: bool
    errors: tuple[str, ...]

    def as_dict(self) -> dict[str, Any]:
        return {"question_id": self.question_id, "strict_success": self.strict_success, "errors": list(self.errors)}
