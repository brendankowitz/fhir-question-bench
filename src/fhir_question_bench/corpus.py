from __future__ import annotations

import hashlib
import json
import re
from dataclasses import dataclass
from decimal import Decimal
from pathlib import Path, PurePosixPath
from typing import Any


class CorpusError(ValueError):
    """A corpus manifest or source file is malformed or does not match its lock."""


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _resource_signature(resources: list[ResolvedResource]) -> str:
    records = [
        {
            "resource": item.resource,
            "identity": item.identity,
            "file": item.file,
            "source_hash": item.source_hash,
            "line": item.line,
            "entry_index": item.entry_index,
            "full_url": item.full_url,
            "bundle_file": item.bundle_file,
            "pointer_prefix": item.pointer_prefix,
        }
        for item in resources
    ]
    try:
        encoded = json.dumps(
            records,
            sort_keys=True,
            separators=(",", ":"),
            ensure_ascii=False,
            default=lambda value: format(value, "f") if isinstance(value, Decimal) else value,
        ).encode()
    except (TypeError, ValueError) as error:
        raise CorpusError("loaded resource objects are no longer valid JSON") from error
    return sha256(encoded)


@dataclass(slots=True)
class ResolvedResource:
    resource: dict[str, Any]
    identity: str
    file: str
    source_hash: str
    corpus_hash: str
    line: int | None = None
    entry_index: int | None = None
    full_url: str | None = None
    bundle_file: str | None = None
    pointer_prefix: str = ""
    parent: ResolvedResource | None = None

    @property
    def reference(self) -> str:
        if "/" in self.identity or "#" in self.identity:
            return self.identity
        return self.identity

    def evidence(self, evidence_id: str, pointer: str) -> dict[str, Any]:
        full_pointer = self.pointer_prefix + pointer
        return {
            "id": evidence_id,
            "corpus_hash": self.corpus_hash,
            "resource_identity": self.identity,
            "resource_reference": self.reference,
            "source_hash": self.source_hash,
            "file": self.file,
            "pointer": full_pointer,
            "source_value": source_literal(json_pointer_value(self.resource, pointer)),
            **({"line": self.line} if self.line is not None else {}),
            **({"entry_index": self.entry_index} if self.entry_index is not None else {}),
            **(
                {"version": str(self.resource.get("meta", {}).get("versionId", self.resource.get("version")))}
                if self.resource.get("meta", {}).get("versionId", self.resource.get("version")) is not None
                else {}
            ),
        }


@dataclass(slots=True)
class Corpus:
    corpus_id: str
    corpus_hash: str
    resources: list[ResolvedResource]
    manifest_path: Path
    file_hashes: dict[str, str]
    parsed_hash: str = ""

    def __post_init__(self) -> None:
        if not self.parsed_hash:
            self.parsed_hash = _resource_signature(self.resources)

    def assert_unchanged(self) -> None:
        if _resource_signature(self.resources) != self.parsed_hash:
            raise CorpusError("loaded resource objects changed after source parsing")

    def find(self, identity: str) -> list[ResolvedResource]:
        return [resource for resource in self.resources if resource.identity == identity]


def _safe_relative_file(value: Any) -> str:
    if not isinstance(value, str) or not value or "\\" in value:
        raise CorpusError("manifest file path must be a normalized relative path")
    path = PurePosixPath(value)
    if path.is_absolute() or any(part in {"", ".", ".."} for part in path.parts):
        raise CorpusError(f"manifest file path escapes corpus root: {value!r}")
    return path.as_posix()


def _identity(resource: dict[str, Any]) -> str:
    resource_type = resource.get("resourceType")
    resource_id = resource.get("id")
    if not isinstance(resource_type, str) or not isinstance(resource_id, str) or not resource_id:
        raise CorpusError("every top-level FHIR resource must have resourceType and id")
    return f"{resource_type}/{resource_id}"


def source_literal(value: Any) -> Any:
    if isinstance(value, Decimal):
        if not value.is_finite():
            raise CorpusError("non-finite JSON decimal is not supported")
        return format(value.normalize(), "f")
    if isinstance(value, list):
        return [source_literal(item) for item in value]
    if isinstance(value, dict):
        return {key: source_literal(item) for key, item in value.items()}
    return value


def load_corpus(manifest_path: Path | str) -> Corpus:
    manifest_path = Path(manifest_path).resolve()
    try:
        manifest_bytes = manifest_path.read_bytes()
        manifest = json.loads(manifest_bytes)
    except (OSError, json.JSONDecodeError) as error:
        raise CorpusError(f"cannot read corpus manifest: {error}") from error
    if not isinstance(manifest, dict) or manifest.get("version") != 1:
        raise CorpusError("corpus manifest must use version 1")
    corpus_id = manifest.get("corpus_id")
    files = manifest.get("files")
    if not isinstance(corpus_id, str) or not corpus_id or not isinstance(files, list) or not files:
        raise CorpusError("corpus manifest requires corpus_id and non-empty files")
    canonical = json.dumps(manifest, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    corpus_hash = sha256(canonical)
    resources: list[ResolvedResource] = []
    file_hashes: dict[str, str] = {}
    seen_paths: set[str] = set()
    for item in files:
        if not isinstance(item, dict) or set(item) != {"path", "sha256"}:
            raise CorpusError("each manifest file requires exactly path and sha256")
        relative = _safe_relative_file(item["path"])
        if relative in seen_paths:
            raise CorpusError(f"duplicate manifest path: {relative}")
        seen_paths.add(relative)
        expected_hash = item["sha256"]
        if not isinstance(expected_hash, str) or not re.fullmatch(r"[0-9a-f]{64}", expected_hash):
            raise CorpusError(f"invalid SHA-256 for {relative}")
        root = manifest_path.parent.resolve()
        path = root.joinpath(*relative.split("/")).resolve()
        try:
            path.relative_to(root)
        except ValueError as error:
            raise CorpusError(f"corpus source escapes root through a symlink: {relative}") from error
        try:
            raw = path.read_bytes()
        except OSError as error:
            raise CorpusError(f"cannot read corpus source {relative}: {error}") from error
        actual_hash = sha256(raw)
        if actual_hash != expected_hash:
            raise CorpusError(f"SHA-256 mismatch for {relative}")
        file_hashes[relative] = actual_hash
        try:
            text = raw.decode("utf-8-sig")
        except UnicodeDecodeError as error:
            raise CorpusError(f"source is not UTF-8: {relative}") from error
        if path.suffix.lower() in {".ndjson", ".jsonl"}:
            for line_number, line in enumerate(text.splitlines(), start=1):
                if not line.strip():
                    continue
                try:
                    resource = json.loads(line, parse_float=Decimal)
                except json.JSONDecodeError as error:
                    raise CorpusError(f"invalid JSON on {relative}:{line_number}") from error
                if not isinstance(resource, dict) or resource.get("resourceType") == "Bundle":
                    raise CorpusError(f"NDJSON entries must be individual FHIR resources: {relative}:{line_number}")
                resources.append(
                    ResolvedResource(
                        resource=resource,
                        identity=_identity(resource),
                        file=relative,
                        source_hash=actual_hash,
                        corpus_hash=corpus_hash,
                        line=line_number,
                    )
                )
            continue
        try:
            document = json.loads(text, parse_float=Decimal)
        except json.JSONDecodeError as error:
            raise CorpusError(f"invalid JSON in {relative}") from error
        if not isinstance(document, dict):
            raise CorpusError(f"JSON source must be an object: {relative}")
        if document.get("resourceType") == "Bundle":
            entries = document.get("entry")
            if not isinstance(entries, list):
                raise CorpusError(f"Bundle.entry must be an array: {relative}")
            for index, entry in enumerate(entries):
                if not isinstance(entry, dict) or not isinstance(entry.get("resource"), dict):
                    raise CorpusError(f"Bundle entry {index} has no resource: {relative}")
                resource = entry["resource"]
                resources.append(
                    ResolvedResource(
                        resource=resource,
                        identity=_identity(resource),
                        file=relative,
                        source_hash=actual_hash,
                        corpus_hash=corpus_hash,
                        entry_index=index,
                        full_url=entry.get("fullUrl"),
                        bundle_file=relative,
                        pointer_prefix=f"/entry/{index}/resource",
                    )
                )
        elif "resourceType" in document:
            resources.append(
                ResolvedResource(
                    resource=document,
                    identity=_identity(document),
                    file=relative,
                    source_hash=actual_hash,
                    corpus_hash=corpus_hash,
                )
            )
        else:
            raise CorpusError(f"JSON source is neither a FHIR resource nor Bundle: {relative}")
    return Corpus(corpus_id, corpus_hash, resources, manifest_path, file_hashes, _resource_signature(resources))


def json_pointer_value(value: Any, pointer: str) -> Any:
    if pointer == "":
        return value
    if not pointer.startswith("/"):
        raise KeyError(f"not a JSON Pointer: {pointer}")
    current = value
    for raw_part in pointer[1:].split("/"):
        part = raw_part.replace("~1", "/").replace("~0", "~")
        if isinstance(current, list):
            current = current[int(part)]
        elif isinstance(current, dict):
            current = current[part]
        else:
            raise KeyError(pointer)
    return current
