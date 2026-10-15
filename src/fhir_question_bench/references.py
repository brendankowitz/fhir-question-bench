from __future__ import annotations

from dataclasses import dataclass
from urllib.parse import urlparse, urlunparse

from .corpus import Corpus, ResolvedResource


@dataclass(frozen=True, slots=True)
class ReferenceResolution:
    status: str
    targets: tuple[ResolvedResource, ...]


def resolve_reference(
    source: ResolvedResource,
    reference: str,
    corpus: Corpus,
) -> ReferenceResolution:
    """Resolve exact Bundle-local aliases, contained references, or unique Type/id refs."""
    corpus.assert_unchanged()
    return _resolve_reference(source, reference, corpus)


def _resolve_reference(
    source: ResolvedResource,
    reference: str,
    corpus: Corpus,
) -> ReferenceResolution:
    if not isinstance(reference, str) or not reference:
        return ReferenceResolution("missing", ())
    if reference.startswith("#"):
        contained_id = reference[1:]
        owner = source
        while owner.parent is not None:
            owner = owner.parent
        owner_id = owner.resource.get("id")
        owner_targets = (owner,) if contained_id == owner_id else ()
        matches = [
            item
            for item in owner.resource.get("contained", [])
            if isinstance(item, dict) and item.get("id") == contained_id
        ]
        targets = tuple(
            ResolvedResource(
                resource=item,
                identity=f"{owner.identity}#{contained_id}",
                file=owner.file,
                source_hash=owner.source_hash,
                corpus_hash=owner.corpus_hash,
                line=owner.line,
                entry_index=owner.entry_index,
                full_url=None,
                bundle_file=owner.bundle_file,
                pointer_prefix=owner.pointer_prefix + f"/contained/{owner.resource['contained'].index(item)}",
                parent=owner,
            )
            for item in matches
        )
        return _result((*owner_targets, *targets))
    if ":" in reference and not reference.startswith("http://") and not reference.startswith("https://"):
        targets = tuple(
            item
            for item in corpus.resources
            if item.bundle_file == source.bundle_file and item.full_url == reference
        )
        return _result(targets)
    if reference.startswith(("http://", "https://")):
        targets = tuple(item for item in corpus.resources if item.full_url == reference)
        return _result(targets)
    if source.full_url and source.full_url.startswith(("http://", "https://")):
        parsed = urlparse(source.full_url)
        source_suffix = f"/{source.identity}"
        if parsed.path.endswith(source_suffix):
            base_path = parsed.path[: -len(source_suffix)].rstrip("/")
            target_url = urlunparse(parsed._replace(path=f"{base_path}/{reference}"))
            matches = tuple(item for item in corpus.resources if item.full_url == target_url)
            if matches:
                return _result(matches)
        if source.bundle_file:
            unlocated = tuple(
                item for item in corpus.resources
                if item.bundle_file == source.bundle_file
                and item.full_url is None
                and item.identity == reference
            )
            return _result(unlocated)
        return ReferenceResolution("missing", ())
    if source.bundle_file:
        matches = tuple(
            item for item in corpus.resources
            if item.bundle_file == source.bundle_file and item.identity == reference
        )
    else:
        matches = tuple(item for item in corpus.resources if item.identity == reference)
    return _result(matches)


def _result(targets: tuple[ResolvedResource, ...]) -> ReferenceResolution:
    if not targets:
        return ReferenceResolution("missing", ())
    if len(targets) > 1:
        return ReferenceResolution("ambiguous", targets)
    return ReferenceResolution("resolved", targets)
