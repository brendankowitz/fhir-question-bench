"""Independent, deliberately bounded terminology mechanics over owned fixtures.

No network, tool-under-test, terminology release, or display-string inference.
Unsupported conversion dimensions and non-equivalent translations abstain.
"""
from __future__ import annotations

from decimal import Decimal
from typing import Any

from .corpus import Corpus, ResolvedResource


def _concepts(concepts: list[dict], ancestors: tuple[str, ...] = ()):
    for concept in concepts:
        yield concept, ancestors
        yield from _concepts(concept.get("concept", []), (*ancestors, concept["code"]))


def _property(concept: dict, name: str, field: str):
    values = [p[field] for p in concept.get("property", []) if p.get("code") == name and field in p]
    if len(values) != 1:
        raise ValueError(f"expected one {name} property")
    return values[0]


def derive_terminology(operation: str, params: dict, corpus: Corpus):
    """Return status, typed answer and exact decisive source pointers."""
    pointers: list[tuple[ResolvedResource, str]] = []

    def systems(system, version=None):
        return [r for r in corpus.resources if r.resource.get("resourceType") == "CodeSystem"
                and r.resource.get("url") == system
                and (version is None or r.resource.get("version") == version)]

    def cite(resource, *fields):
        pointers.extend((resource, f"/{field}") for field in fields)

    if operation == "qa-translate":
        maps = [r for r in corpus.resources if r.resource.get("resourceType") == "ConceptMap"
                and r.resource.get("url") == params["map_id"]
                and r.resource.get("version") == params["map_version"]]
        if len(maps) != 1:
            raise ValueError("expected one pinned ConceptMap")
        source = maps[0]
        cite(source, "url", "version", "group")
        targets = []
        dependency_missing = False
        for group in source.resource["group"]:
            if (group["source"] != params["system"] or group.get("sourceVersion") != params["version"]
                    or group["target"] != params["target_system"]
                    or group.get("targetVersion") != params["target_version"]):
                continue
            for element in group["element"]:
                if element["code"] != params["code"]:
                    continue
                for target in element.get("target", []):
                    dependencies = target.get("dependsOn", [])
                    context = params.get("context", {})
                    if any(d["property"] not in context for d in dependencies):
                        dependency_missing = True
                        continue
                    if any(context[d["property"]] != d["value"] for d in dependencies):
                        continue
                    targets.append({"system": group["target"], "code": target["code"],
                                    "equivalence": target["equivalence"]})
        if dependency_missing:
            return "ambiguous", None, pointers
        exact = [t for t in targets if t["equivalence"] in {"equal", "equivalent"}]
        if not exact:
            return "insufficient_evidence", None, pointers
        if len(exact) != 1:
            return "ambiguous", {"kind": "set", "value": sorted(exact, key=lambda t: (t["system"], t["code"]))}, pointers
        return "answered", {"kind": "code", "value": {"match": "equivalent", "targets": exact}}, pointers

    if operation == "qa-subsumes":
        matches = systems(params["system"], params["version"])
        if len(matches) != 1:
            raise ValueError("expected one pinned hierarchy")
        source = matches[0]
        cite(source, "url", "version", "hierarchyMeaning", "concept")
        if source.resource.get("hierarchyMeaning") != "is-a":
            return "insufficient_evidence", None, pointers
        nodes = {c["code"]: ancestors for c, ancestors in _concepts(source.resource["concept"])}
        if params["ancestor"] not in nodes or params["descendant"] not in nodes:
            return "insufficient_evidence", None, pointers
        result = params["ancestor"] == params["descendant"] or params["ancestor"] in nodes[params["descendant"]]
        return "answered", {"kind": "boolean", "value": result}, pointers

    if operation == "qa-code-resolve":
        coding = params
        if "resource" in params:
            matches = corpus.find(params["resource"])
            if len(matches) != 1:
                raise ValueError("expected one coded source resource")
            resource = matches[0]
            cite(resource, "code")
            coding = resource.resource["code"]["coding"][0]
        matches = systems(coding["system"], coding.get("version"))
        candidates = []
        for source in matches:
            cite(source, "url", "version", "concept")
            candidates.extend((source, c) for c, _ in _concepts(source.resource["concept"])
                              if c["code"] == coding["code"])
        if not candidates:
            return "insufficient_evidence", None, pointers
        if len(candidates) != 1:
            return "ambiguous", None, pointers
        source, concept = candidates[0]
        if any(p.get("code") == "inactive" and p.get("valueBoolean") is True for p in concept.get("property", [])):
            return "invalid_data", None, pointers
        return "answered", {"kind": "code", "value": {
            "system": source.resource["url"], "code": concept["code"], "display": concept.get("display", "")}}, pointers

    if operation == "qa-ucum":
        matches = corpus.find(params["observation"])
        if len(matches) != 1:
            raise ValueError("expected one quantity source")
        observation = matches[0]
        cite(observation, "valueQuantity")
        quantity = observation.resource["valueQuantity"]
        matches = systems("http://unitsofmeasure.org", params["unit_table_version"])
        if len(matches) != 1:
            raise ValueError("expected one pinned unit table")
        source = matches[0]
        cite(source, "url", "version", "concept")
        units = {c["code"]: c for c in source.resource["concept"]}
        if quantity.get("system") != "http://unitsofmeasure.org":
            return "insufficient_evidence", None, pointers
        a, b = units.get(quantity.get("code")), units.get(params["target_unit"])
        if a is None or b is None or _property(a, "dimension", "valueString") != _property(b, "dimension", "valueString"):
            return "insufficient_evidence", None, pointers
        number = Decimal(str(quantity["value"]))
        if not number.is_finite():
            raise ValueError("quantity must be finite")
        factor_a = Decimal(_property(a, "factor", "valueDecimal"))
        factor_b = Decimal(_property(b, "factor", "valueDecimal"))
        if not factor_a.is_finite() or not factor_b.is_finite() or factor_a <= 0 or factor_b <= 0:
            raise ValueError("unit factors must be finite and positive")
        converted = number * factor_a / factor_b
        return "answered", {"kind": "quantity", "value": {
            "value": format(converted, "f"), "unit": params["target_unit"]}}, pointers
    raise ValueError(f"unsupported QA terminology operation {operation!r}")
