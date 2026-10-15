from __future__ import annotations

from dataclasses import dataclass

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
        matches = [
            item
            for item in source.resource.get("contained", [])
            if isinstance(item, dict) and item.get("id") == contained_id
        ]
        targets = tuple(
            ResolvedResource(
                resource=item,
                identity=f"{source.identity}#{contained_id}",
                file=source.file,
                source_hash=source.source_hash,
                corpus_hash=source.corpus_hash,
                line=source.line,
                entry_index=source.entry_index,
                full_url=None,
                bundle_file=source.bundle_file,
                pointer_prefix=source.pointer_prefix + f"/contained/{source.resource['contained'].index(item)}",
                parent=source,
            )
            for item in matches
        )
        return _result(targets)
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
    matches = tuple(item for item in corpus.resources if item.identity == reference)
    return _result(matches)


def _result(targets: tuple[ResolvedResource, ...]) -> ReferenceResolution:
    if not targets:
        return ReferenceResolution("missing", ())
    if len(targets) > 1:
        return ReferenceResolution("ambiguous", targets)
    return ReferenceResolution("resolved", targets)
