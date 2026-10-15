"""Small, offline reference interpreters for the owned operations fixtures.

These are deliberately bounded subsets, not general FHIR engines. Unsupported
expressions/transforms raise rather than silently producing success. No specialist
tool, model, network, wall clock, random IDs, or private gold is used here.
"""
from __future__ import annotations

import copy
import itertools
import re
from datetime import date, datetime, timedelta
from decimal import Decimal
from typing import Any


def _split(text: str, separator: str = ".") -> list[str]:
    """Split outside quoted strings and parentheses (the fixture grammar)."""
    parts, start, depth, quoted = [], 0, 0, False
    for index, char in enumerate(text):
        if char == "'":
            quoted = not quoted
        elif not quoted:
            if char == "(":
                depth += 1
            elif char == ")":
                depth -= 1
            elif char == separator and depth == 0:
                parts.append(text[start:index])
                start = index + 1
    if depth or quoted:
        raise ValueError(f"unsupported unbalanced expression: {text}")
    return [*parts, text[start:]]


def _literal(expression: str) -> Any:
    if re.fullmatch(r"'[^']*'", expression):
        return expression[1:-1]
    if expression in {"true", "false"}:
        return expression == "true"
    if re.fullmatch(r"-?\d+", expression):
        return int(expression)
    raise ValueError(f"unsupported literal: {expression}")


def _predicate(value: Any, expression: str) -> bool:
    # Logical operators are intentionally limited to the fixture expressions.
    for operator in (" or ", " and "):
        if operator in expression:
            operands = [_predicate(value, part) for part in expression.split(operator)]
            return any(operands) if operator == " or " else all(operands)
    comparison = re.fullmatch(r"(.+?)\s+(!=|=|>=|<=|>|<)\s+('[^']*'|true|false|-?\d+)", expression)
    if comparison:
        path, operator, literal = comparison.groups()
        expected = _literal(literal)
        values = evaluate_path(value, path)
        if not values:
            return False
        def compare(actual: Any) -> bool:
            if operator == "=":
                return type(actual) is type(expected) and actual == expected
            if operator == "!=":
                return type(actual) is not type(expected) or actual != expected
            if type(actual) is not type(expected):
                return False
            if operator == ">":
                return actual > expected
            if operator == "<":
                return actual < expected
            if operator == ">=":
                return actual >= expected
            return actual <= expected
        return any(compare(actual) for actual in values)
    values = evaluate_path(value, expression)
    if len(values) == 1 and type(values[0]) is bool:
        return values[0]
    if not values:
        return False
    raise ValueError(f"unsupported non-boolean predicate: {expression}")


def evaluate_path(value: Any, expression: str) -> list[Any]:
    """FHIRPath subset: navigation, $this, literals, where, first, exists, empty, count."""
    expression = expression.strip()
    if expression in {"true", "false"} or re.fullmatch(r"'[^']*'|-?\d+", expression):
        return [_literal(expression)]
    values = [value] if value is not None else []
    for segment in _split(expression):
        if segment == "$this":
            continue
        if segment == "first()":
            values = values[:1]
        elif segment == "exists()":
            values = [bool(values)]
        elif segment == "empty()":
            values = [not values]
        elif segment == "count()":
            values = [len(values)]
        elif segment.startswith("where(") and segment.endswith(")"):
            values = [item for item in values if _predicate(item, segment[6:-1])]
        elif re.fullmatch(r"[A-Za-z][A-Za-z0-9_]*(?:\[x\])?", segment):
            next_values = []
            for item in values:
                if not isinstance(item, dict):
                    continue
                keys = [segment]
                if segment.endswith("[x]"):
                    prefix = segment[:-3]
                    keys = [key for key in item if key.startswith(prefix)
                            and len(key) > len(prefix) and key[len(prefix)].isupper()]
                for key in keys:
                    child = item.get(key)
                    if child is not None:
                        next_values.extend(child if isinstance(child, list) else [child])
            values = next_values
        else:
            raise ValueError(f"unsupported FHIRPath segment: {segment!r}")
    return values


def deidentify(source: dict, policy: dict) -> dict:
    """Apply the explicit synthetic policy, without mutating the input.

    Field-name policies apply recursively, including contained resources. Reference
    rewrites are exact, supplied mappings (never substring replacement). Patient
    shifts are selected using original references, before pseudonymization.
    """
    id_map = policy["id_map"]
    remove = set(policy["remove_fields"])
    years = set(policy.get("year_fields", []))
    shifts = set(policy.get("shift_fields", []))

    def visit(value: Any, offset: int, resource_type: str | None = None) -> Any:
        if isinstance(value, list):
            return [visit(item, offset, resource_type) for item in value]
        if not isinstance(value, dict):
            return value
        if "resourceType" in value:
            resource_type = value["resourceType"]
            patient = (f"Patient/{value['id']}" if resource_type == "Patient"
                       else value.get("subject", {}).get("reference"))
            offset = policy.get("patient_shift_days", {}).get(patient, offset)
        result = {}
        for key, child in value.items():
            if key in remove:
                continue
            if key == "id" and "resourceType" in value:
                identity = f"{resource_type}/{child}"
                result[key] = id_map.get(identity, identity).split("/")[-1]
            elif key in {"reference", "fullUrl", "url"} and isinstance(child, str):
                result[key] = id_map.get(child, child)
            elif key in years and isinstance(child, str):
                result[key] = child[:4]
            elif key in shifts and isinstance(child, str):
                if "T" in child:
                    shifted = datetime.fromisoformat(child.replace("Z", "+00:00")) + timedelta(days=offset)
                    result[key] = shifted.isoformat().replace("+00:00", "Z")
                else:
                    result[key] = (date.fromisoformat(child) + timedelta(days=offset)).isoformat()
            elif key == "postalCode" and policy.get("zip3"):
                prefix = child[:3]
                result[key] = "000" if prefix in policy.get("small_zip_prefixes", []) else prefix
            elif key == "age" and policy.get("cap_age_90") and type(child) is int:
                result[key] = min(90, child)
            else:
                result[key] = visit(child, offset, resource_type)
        return result

    return visit(source, policy.get("shift_days", 0))


def outcome(issues: list[dict]) -> dict:
    return {"resourceType": "OperationOutcome", "issue": issues}


def _issue(code: str, location: str, severity: str = "error") -> dict:
    return {"severity": severity, "code": code, "expression": [location]}


class _MapFailure(ValueError):
    def __init__(self, issue: dict):
        self.issue = issue


def map_resource(source: dict, mapping: dict) -> dict:
    """Interpret one R4 StructureMap group with copy/create/append/evaluate.

    Sources support min/max, conditions, variables and nested rules. All targets
    are built privately; a required-source failure returns ONLY an OperationOutcome.
    Repeated fields use their pinned FHIR cardinality; scalar overwrites fail.
    """
    if mapping.get("resourceType") != "StructureMap" or len(mapping["group"]) != 1:
        raise ValueError("unsupported StructureMap: exactly one group required")
    group = mapping["group"][0]
    sources = [i for i in group["input"] if i["mode"] == "source"]
    targets = [i for i in group["input"] if i["mode"] == "target"]
    if len(sources) != 1 or len(targets) != 1:
        raise ValueError("unsupported StructureMap inputs")
    if "type" in sources[0] and source.get("resourceType") != sources[0]["type"]:
        return outcome([_issue("structure", source.get("resourceType", "$this"))])
    target_structures = [s for s in mapping["structure"] if s["mode"] == "target"]
    target_type = target_structures[0]["url"].rsplit("/", 1)[-1]
    result: dict = {"resourceType": target_type}
    environment = {sources[0]["name"]: source, targets[0]["name"]: result}
    repeatable = {"name", "given", "identifier", "coding", "component", "category", "telecom", "entry"}

    def parameter(item: dict, env: dict) -> Any:
        if len(item) != 1:
            raise ValueError("unsupported StructureMap parameter")
        key, value = next(iter(item.items()))
        return env[value] if key == "valueId" else copy.deepcopy(value)

    def rules(items: list[dict], env: dict) -> None:
        for rule in items:
            environments = [dict(env)]
            for spec in rule.get("source", []):
                expanded = []
                for local in environments:
                    context = local[spec["context"]]
                    values = evaluate_path(context, spec["element"]) if "element" in spec else [context]
                    if len(values) < spec.get("min", 0):
                        raise _MapFailure(_issue("required", spec["context"] + "." + spec.get("element", "$this")))
                    maximum = spec.get("max", "*")
                    if maximum != "*" and len(values) > int(maximum):
                        raise _MapFailure(_issue("structure", spec["context"] + "." + spec.get("element", "$this")))
                    for value in values:
                        if "condition" in spec and not _predicate(value, spec["condition"]):
                            continue
                        bound = dict(local)
                        if "variable" in spec:
                            bound[spec["variable"]] = value
                        expanded.append(bound)
                environments = expanded
            for local in environments:
                for target in rule.get("target", []):
                    params = [parameter(p, local) for p in target.get("parameter", [])]
                    transform = target.get("transform", "create")
                    if transform == "copy":
                        value = copy.deepcopy(params[0])
                    elif transform == "create":
                        value = {}
                    elif transform == "append":
                        value = "".join(str(p) for p in params)
                    elif transform == "evaluate":
                        values = evaluate_path(params[0], params[1])
                        if len(values) != 1:
                            raise _MapFailure(_issue("value", rule["name"]))
                        value = copy.deepcopy(values[0])
                    else:
                        raise ValueError(f"unsupported StructureMap transform: {transform}")
                    context = local[target["context"]]
                    element = target["element"]
                    if element in repeatable:
                        context.setdefault(element, []).append(value)
                    elif element in context:
                        raise ValueError(f"unsupported repeated scalar target: {element}")
                    else:
                        context[element] = value
                    if "variable" in target:
                        local[target["variable"]] = value
                rules(rule.get("rule", []), local)
    try:
        rules(group["rule"], environment)
    except _MapFailure as error:
        return outcome([error.issue])
    return result


def view_rows(resources: list[dict], view: dict) -> dict:
    """Evaluate owned SQL-on-FHIR v2 selections; sibling selects cross-product.

    Missing scalar columns are null. A scalar multi-value is an error, not an
    arbitrary first match. forEach drops empties; forEachOrNull retains one null.
    Duplicate rows are retained.
    """
    columns: list[dict] = []
    def collect(selects: list[dict]) -> None:
        for selection in selects:
            for column in selection.get("column", []):
                descriptor = {key: column[key] for key in ("name", "type")}
                if descriptor not in columns:
                    columns.append(descriptor)
            collect(selection.get("select", []))
            collect(selection.get("unionAll", []))
    collect(view["select"])

    def selection_rows(context: Any, selection: dict) -> list[dict]:
        contexts = [context]
        if "forEach" in selection:
            contexts = evaluate_path(context, selection["forEach"])
        elif "forEachOrNull" in selection:
            contexts = evaluate_path(context, selection["forEachOrNull"]) or [None]
        result = []
        for item in contexts:
            row = {}
            for column in selection.get("column", []):
                values = evaluate_path(item, column["path"])
                if column.get("collection", False):
                    row[column["name"]] = copy.deepcopy(values)
                else:
                    if len(values) > 1:
                        raise ValueError(f"multi-valued scalar column: {column['name']}")
                    row[column["name"]] = copy.deepcopy(values[0]) if values else None
            nested = selections_rows(item, selection.get("select", []))
            union = selection.get("unionAll")
            branches = ([r for branch in union for r in selection_rows(item, branch)] if union else [{}])
            for child, branch in itertools.product(nested, branches):
                result.append({**row, **child, **branch})
        return result

    def selections_rows(context: Any, selections: list[dict]) -> list[dict]:
        rows = [{}]
        for selection in selections:
            rows = [{**left, **right} for left, right in
                    itertools.product(rows, selection_rows(context, selection))]
        return rows

    rows = []
    for resource in resources:
        if resource.get("resourceType") != view["resource"]:
            continue
        if all(_predicate(resource, clause["path"]) for clause in view.get("where", [])):
            rows.extend(selections_rows(resource, view["select"]))
    return {"columns": columns, "rows": [
        {c["name"]: row.get(c["name"]) for c in columns} for row in rows]}


def _assets(terminology: dict, resource_type: str) -> list[dict]:
    return [entry["resource"] for entry in terminology.get("entry", [])
            if entry["resource"]["resourceType"] == resource_type]


def _concepts(system: dict) -> list[dict]:
    def walk(items: list[dict]) -> list[dict]:
        return [item for concept in items for item in [concept, *walk(concept.get("concept", []))]]
    return walk(system["concept"])


def _inactive(concept: dict) -> bool:
    return any(p["code"] == "inactive" and p.get("valueBoolean") is True for p in concept.get("property", []))


def translate(request: dict, terminology: dict) -> dict:
    matches = []
    for mapping in _assets(terminology, "ConceptMap"):
        if request.get("url", mapping["url"]) != mapping["url"]:
            continue
        for group in mapping["group"]:
            if (group["source"] != request["system"]
                    or group.get("sourceVersion") != request.get("version")):
                continue
            for element in group["element"]:
                if element["code"] != request["code"]:
                    continue
                for target in element.get("target", []):
                    if target["equivalence"] in {"unmatched", "disjoint"}:
                        continue
                    if not all(request.get("context", {}).get(d["property"]) == d["value"]
                               for d in target.get("dependsOn", [])):
                        continue
                    matches.append({"name": "match", "part": [
                        {"name": "equivalence", "valueCode": target["equivalence"]},
                        {"name": "concept", "valueCoding": {"system": group["target"],
                            "version": group["targetVersion"], "code": target["code"],
                            "display": target["display"]}},
                    ]})
    return {"resourceType": "Parameters",
            "parameter": [{"name": "result", "valueBoolean": bool(matches)}, *matches]}


def _members(url: str, terminology: dict) -> list[dict]:
    values = [v for v in _assets(terminology, "ValueSet") if v["url"] == url]
    if len(values) != 1:
        raise ValueError(f"unknown or ambiguous owned ValueSet: {url}")
    result = []
    for include in values[0]["compose"]["include"]:
        systems = [s for s in _assets(terminology, "CodeSystem")
                   if s["url"] == include["system"] and s["version"] == include["version"]]
        if len(systems) != 1:
            raise ValueError("missing version-pinned owned CodeSystem")
        for concept in _concepts(systems[0]):
            if "concept" not in include or concept["code"] in {c["code"] for c in include["concept"]}:
                result.append({"system": systems[0]["url"], "version": systems[0]["version"],
                               "code": concept["code"], "display": concept["display"],
                               **({"inactive": True} if _inactive(concept) else {})})
    # Expansion membership is a set, unlike table rows.
    unique = {(c["system"], c["version"], c["code"]): c for c in result}
    return sorted(unique.values(), key=lambda c: (c["system"], c["version"], c["code"]))


def validate_code(request: dict, terminology: dict) -> dict:
    concepts = [c for s in _assets(terminology, "CodeSystem")
                if s["url"] == request["system"] and s["version"] == request.get("version")
                for c in _concepts(s) if c["code"] == request["code"]]
    valid = len(concepts) == 1
    if valid:
        valid = not (request.get("activeOnly", False) and _inactive(concepts[0]))
        if "display" in request:
            valid = valid and request["display"] == concepts[0]["display"]
        if "valueSet" in request:
            valid = valid and any(c["system"] == request["system"] and c["code"] == request["code"]
                                  and c["version"] == request["version"]
                                  for c in _members(request["valueSet"], terminology))
    return {"resourceType": "Parameters", "parameter": [{"name": "result", "valueBoolean": valid}]}


def expand(request: dict, terminology: dict) -> dict:
    members = _members(request["url"], terminology)
    if request.get("activeOnly"):
        members = [c for c in members if not c.get("inactive", False)]
    if "filter" in request:
        members = [c for c in members if request["filter"].casefold() in c["display"].casefold()]
    offset = request.get("offset", 0)
    return {"resourceType": "ValueSet", "url": request["url"], "expansion": {
        "identifier": "urn:ops:deterministic-expansion", "timestamp": "2026-01-01T00:00:00Z",
        "total": len(members), "offset": offset,
        "contains": members[offset:offset + request.get("count", len(members))]}}


def validate(resource: dict, profile: dict, terminology: dict, *,
             resources: list[dict] | None = None, check_references: bool = False) -> dict:
    """Validate only the explicit SD differential rules and pinned dependencies.

    Rule findings are exhaustive for the owned subset, not base-R4 validation.
    Locations use OperationOutcome.issue.expression. Diagnostic prose is not gold.
    """
    issues = []
    if resource.get("resourceType") != profile["type"]:
        return outcome([_issue("structure", resource.get("resourceType", "$this"))])

    def valid_type(value: Any, code: str) -> bool:
        if code in {"code", "string", "id", "uri", "date", "dateTime"}:
            if not isinstance(value, str):
                return False
            if code == "date":
                return bool(re.fullmatch(r"\d{4}(?:-\d{2}(?:-\d{2})?)?", value))
            return True
        if code == "boolean":
            return type(value) is bool
        if code == "integer":
            return type(value) is int
        if code == "decimal":
            return type(value) in {int, float, Decimal}
        if code in {"Quantity", "CodeableConcept", "Reference", "HumanName", "Coding"}:
            return isinstance(value, dict)
        raise ValueError(f"unsupported profile type: {code}")

    def pattern_matches(value: Any, pattern: Any) -> bool:
        if isinstance(pattern, dict):
            return isinstance(value, dict) and all(k in value and pattern_matches(value[k], v)
                                                    for k, v in pattern.items())
        if isinstance(pattern, list):
            return isinstance(value, list) and all(any(pattern_matches(v, p) for v in value) for p in pattern)
        return type(value) is type(pattern) and value == pattern

    for element in profile["differential"]["element"]:
        location = element["path"]
        path = location.split(".", 1)[1] if "." in location else "$this"
        values = evaluate_path(resource, path)
        if len(values) < element.get("min", 0):
            issues.append(_issue("required", location))
        if element.get("max", "*") != "*" and len(values) > int(element["max"]):
            issues.append(_issue("structure", location))
        types = element.get("type", [])
        for value in values:
            if types and not any(valid_type(value, t["code"]) for t in types):
                issues.append(_issue("structure", location))
                continue
            for key, fixed in element.items():
                if key.startswith("fixed") and (type(value) is not type(fixed) or value != fixed):
                    issues.append(_issue("value", location))
                if key.startswith("pattern") and not pattern_matches(value, fixed):
                    issues.append(_issue("value", location))
            binding = element.get("binding")
            if binding:
                if binding["strength"] not in {"required", "extensible"}:
                    raise ValueError("unsupported binding strength")
                codings = value.get("coding", []) if isinstance(value, dict) else []
                members = _members(binding["valueSet"], terminology)
                if not any(c.get("system") == m["system"] and c.get("code") == m["code"]
                           and c.get("version", m["version"]) == m["version"]
                           and not m.get("inactive", False) for c in codings for m in members):
                    issues.append(_issue("code-invalid", location,
                                         "error" if binding["strength"] == "required" else "warning"))
            for constraint in element.get("constraint", []):
                if not _predicate(value, constraint["expression"]):
                    issues.append({**_issue("invariant", location, constraint["severity"]),
                                   "details": {"coding": [{"system": "urn:ops:constraint",
                                                           "code": constraint["key"]}]}})
    if check_references:
        identities = {f"{r['resourceType']}/{r['id']}" for r in (resources or [])}
        def references(value: Any, path: str) -> None:
            if isinstance(value, dict):
                for key, child in value.items():
                    if key == "reference" and child not in identities:
                        issues.append(_issue("not-found", path + ".reference"))
                    else:
                        references(child, path + "." + key)
            elif isinstance(value, list):
                for index, child in enumerate(value):
                    references(child, path + f"[{index}]")
        references(resource, resource["resourceType"])
    if not issues:
        issues = [_issue("informational", profile["type"], "information")]
    return outcome(issues)
