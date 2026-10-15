"""Reproducible authoring of benchmark-owned synthetic operational assets.

Run with ``python -m fhir_question_bench.operations_authoring`` only when
intentionally revising the operations release. It overwrites only this module's
owned operations tree, never the QA corpus, manifest, or review ledger.
"""
from __future__ import annotations

import copy
import hashlib
from pathlib import Path
from typing import Any

from .operations import OPERATIONS_ROOT, canonical, recompute_task


def _patient(identity: str = "a", **changes: Any) -> dict:
    resource = {"resourceType": "Patient", "id": identity, "active": True,
            "name": [{"family": "Synthetic", "given": ["Person", identity.upper()]}],
            "identifier": [{"system": "urn:ops:record-number", "value": f"SYN-{identity}"}],
            "telecom": [{"system": "email", "value": f"synthetic-{identity}@example.invalid"}],
            "address": [{"line": ["100 Imaginary Street"], "city": "Fictional",
                         "state": "ZZ", "postalCode": "12345", "country": "US"}],
            "birthDate": "1930-02-28" if identity == "a" else "1988-07-04", **changes}
    return {k: v for k, v in resource.items() if v is not None and v != []}


def _observation(identity: str = "o1", **changes: Any) -> dict:
    resource = {"resourceType": "Observation", "id": identity, "status": "final",
            "code": {"coding": [{"system": "urn:ops:source", "version": "1", "code": "a",
                                  "display": "Owned signal A"}]},
            "subject": {"reference": "Patient/a"}, "effectiveDateTime": "2024-02-28T00:00:00Z",
            "valueQuantity": {"value": 0, "system": "http://unitsofmeasure.org", "code": "mg",
                              "unit": "mg"}, **changes}
    return {k: v for k, v in resource.items() if v is not None and v != []}


def _bundle(resources: list[dict], *, aliases: bool = False) -> dict:
    return {"resourceType": "Bundle", "id": "synthetic-folder", "type": "collection", "entry": [
        {**({"fullUrl": f"urn:uuid:{r['id']}"} if aliases else {}), "resource": r} for r in resources]}


def _terminology() -> dict:
    source = {"resourceType": "CodeSystem", "id": "owned-source", "url": "urn:ops:source",
              "version": "1", "status": "active", "content": "complete", "caseSensitive": True,
              "property": [{"code": "inactive", "type": "boolean"}],
              "concept": [{"code": "a", "display": "Owned signal A"},
                          {"code": "b", "display": "Owned signal B"},
                          {"code": "many", "display": "Owned multiple-match signal"},
                          {"code": "context", "display": "Owned context-sensitive signal"},
                          {"code": "retired", "display": "Owned retired signal",
                           "property": [{"code": "inactive", "valueBoolean": True}]}]}
    target = {"resourceType": "CodeSystem", "id": "owned-target", "url": "urn:ops:target",
              "version": "1", "status": "active", "content": "complete", "caseSensitive": True,
              "concept": [{"code": "x", "display": "Owned destination X"},
                          {"code": "y", "display": "Owned destination Y"},
                          {"code": "z", "display": "Owned destination Z"}]}
    def dest(code: str, equivalence: str = "equivalent", **kwargs: Any) -> dict:
        return {"code": code, "display": f"Owned destination {code.upper()}",
                "equivalence": equivalence, **kwargs}
    mapping = {"resourceType": "ConceptMap", "id": "owned-map", "url": "urn:ops:map", "version": "1",
               "status": "active", "group": [{
                   "source": "urn:ops:source", "sourceVersion": "1", "target": "urn:ops:target",
                   "targetVersion": "1", "element": [
                       {"code": "a", "target": [dest("x")]},
                       {"code": "many", "target": [dest("x", "wider"), dest("y", "relatedto")]},
                       {"code": "context", "target": [
                           dest("x", dependsOn=[{"property": "urn:ops:specimen", "value": "serum"}]),
                           dest("z", dependsOn=[{"property": "urn:ops:specimen", "value": "urine"}])]},
                       {"code": "retired", "target": [dest("z", "disjoint")]},
                   ]}]}
    values = {"resourceType": "ValueSet", "id": "owned-values", "url": "urn:ops:values",
              "version": "1", "status": "active", "compose": {"include": [{
                  "system": "urn:ops:source", "version": "1",
                  "concept": [{"code": code} for code in ["a", "b", "many", "retired"]]}]}}
    return _bundle([source, target, mapping, values])


def _policy(**changes: Any) -> dict:
    return {"policy_version": 1,
            "scope": "All nested resource objects and their fields; exact-key recursive rules only.",
            "remove_fields": ["name", "identifier", "telecom", "line", "city", "text", "note",
                              "photo", "contact", "communication"],
            "year_fields": ["birthDate"], "shift_fields": [], "shift_days": 0,
            "zip3": True, "small_zip_prefixes": ["123"], "cap_age_90": False,
            "id_map": {}, **changes}


def _copy_rule(field: str, *, context: str = "src", target: str = "tgt",
               output: str | None = None, required: bool = False) -> dict:
    return {"name": f"{context}-{field}-to-{output or field}",
            "source": [{"context": context, "element": field, "variable": "v",
                        **({"min": 1, "max": "1"} if required else {})}],
            "target": [{"context": target, "element": output or field, "transform": "copy",
                        "parameter": [{"valueId": "v"}]}]}


def _constant(field: str, value: Any, *, target: str = "tgt") -> dict:
    kind = "valueBoolean" if type(value) is bool else "valueString"
    return {"name": f"constant-{field}", "source": [{"context": "src"}],
            "target": [{"context": target, "element": field, "transform": "copy",
                        "parameter": [{kind: value}]}]}


def _derived_code_rule(code: str, display: str) -> dict:
    return {"name": "derived-code", "source": [{"context": "src"}],
            "target": [{"context": "tgt", "element": "code", "transform": "create",
                        "parameter": [{"valueString": "CodeableConcept"}], "variable": "outcode"},
                       {"context": "outcode", "element": "coding", "transform": "create",
                        "parameter": [{"valueString": "Coding"}], "variable": "outcoding"}],
            "rule": [_constant("system", "urn:ops:derived", target="outcoding"),
                     _constant("code", code, target="outcoding"),
                     _constant("display", display, target="outcoding")]}


def _map(rules: list[dict], source_type: str = "Patient", target_type: str = "Patient") -> dict:
    return {"resourceType": "StructureMap", "id": "owned-transform", "url": "urn:ops:transform",
            "version": "1", "name": "OwnedTransform", "status": "active",
            "structure": [{"url": f"http://hl7.org/fhir/StructureDefinition/{kind}",
                           "mode": mode, "alias": kind}
                          for mode, kind in [("source", source_type), ("target", target_type)]],
            "group": [{"name": "main", "typeMode": "none",
                       "input": [{"name": "src", "type": source_type, "mode": "source"},
                                 {"name": "tgt", "type": target_type, "mode": "target"}],
                       "rule": rules}]}


def _profile(elements: list[dict], resource_type: str = "Observation", identity: str = "owned-profile") -> dict:
    return {"resourceType": "StructureDefinition", "id": identity, "url": f"urn:ops:profile:{identity}",
            "version": "1", "name": identity.replace("-", ""), "status": "active",
            "kind": "resource", "abstract": False, "type": resource_type,
            "baseDefinition": f"http://hl7.org/fhir/StructureDefinition/{resource_type}",
            "derivation": "constraint", "fhirVersion": "4.0.1",
            "differential": {"element": [{"id": resource_type, "path": resource_type},
                                         *[{"id": f"{resource_type}.{e['field']}",
                                            "path": f"{resource_type}.{e['field']}",
                                            **{k: v for k, v in e.items() if k != "field"}}
                                           for e in elements]]}}


def _column(name: str, path: str, kind: str = "string", **kwargs: Any) -> dict:
    return {"name": name, "path": path, "type": kind, **kwargs}


def _view(resource: str, select: list[dict], **kwargs: Any) -> dict:
    # ViewDefinition is a SQL-on-FHIR logical model, not an R4 base resource.
    return {"resourceType": "ViewDefinition", "id": "owned-view", "url": "urn:ops:view",
            "version": "1", "name": "OwnedView", "status": "active", "resource": resource,
            "select": select, **kwargs}


def _contract(files: dict[str, str]) -> dict:
    return {
        "version": 1, "inventory": files,
        "schema": "Each file is one UTF-8 JSON artifact; exact full expected structure and typed values.",
        "input_policy": "All declared inputs immutable; no writes, deletes, or in-place edits.",
        "auxiliary_artifacts": "None. Logs, copies, intermediate files and empty directories are forbidden.",
        "permitted_extras": [], "id_policy": "exact", "timestamp_tolerance_seconds": 0,
        "timestamp_paths": ["/effectiveDateTime", "/period/start", "/period/end",
                            "/entry/*/resource/effectiveDateTime",
                            "/entry/*/resource/period/start", "/entry/*/resource/period/end",
                            "/expansion/timestamp"],
        "unordered_arrays": ["/entry", "/rows", "/issue", "/parameter", "/expansion/contains"],
        "diagnostic_text_paths": ["/issue/*/diagnostics", "/issue/*/details/text"],
        "ordering": "Declared arrays are multisets (duplicates count); all other arrays retain order.",
        "numeric_policy": "JSON numbers compare numerically, not to booleans or numeric strings.",
        "protected_content": "De-identification removal is scoped ONLY by the supplied policy. "
                             "No raw originals or auxiliary output copies may be retained.",
        "conservation": "No omitted/extra fields, resources, rows, matches or findings relative to "
                        "the requested transform; unmodified fields and relationships are conserved "
                        "when the policy calls for preservation. Maps project only their declared targets.",
        "failure_semantics": "Required map-source failures produce only the specified OperationOutcome. "
                             "Unmapped terminology returns result=false without invented targets. "
                             "Valid validation produces one information/informational issue.",
        "partial_success": False,
        "validation_policy": {
            "scope": "Only explicit differential rules; baseDefinition identifies R4 4.0.1 but inherited "
                     "base rules, slicing and unlisted constraints are outside this exercise.",
            "location": "issue.expression is a one-element array containing the differential element.path; "
                        "reference-policy issues use the complete referring FHIRPath, including list indexes.",
            "missing_min": {"severity": "error", "code": "required"},
            "exceeds_max_or_wrong_type": {"severity": "error", "code": "structure"},
            "fixed_or_pattern_mismatch": {"severity": "error", "code": "value"},
            "constraint": "code=invariant; severity from the constraint; details.coding=[{system:"
                          "'urn:ops:constraint',code:constraint.key}]. No details are required for other findings.",
            "binding": "CodeableConcept succeeds if any coding is an active member of the version-pinned "
                       "ValueSet. An omitted coding.version uses the ValueSet version; an explicit mismatch "
                       "fails. Required failure is error/code-invalid; extensible failure is warning/code-invalid.",
            "references": "Only when the task step requests it: every reference must exactly match a "
                          "ResourceType/id in the declared folder, else error/not-found.",
            "conformant": "Exactly one information/informational issue at the profile's resource type.",
            "completeness": "Report every independent finding, preserving multiplicity. An absent value "
                            "gets only a minimum-cardinality issue, not a spurious type/fixed-value issue.",
        },
        "mapping_policy": {
            "representation": "Owned R4 StructureMap JSON; not textual FML.",
            "repetition": "Repeated FHIR target fields append in source order; scalar overwrites are errors.",
            "required_source": "Only OperationOutcome with error/required and expression=[context.element].",
            "source_max": "Only OperationOutcome with error/structure and expression=[context.element].",
            "source_type": "Only OperationOutcome with error/structure and expression=[actual resourceType].",
        },
        "table_policy": {
            "schema": "{columns:[{name,type}],rows:[{columnName:typedValue}]}",
            "columns": "Exactly the ViewDefinition's declared names/types in depth-first declaration order.",
            "missing": "Missing scalar = null; missing collection column = []; never drop a row for a "
                       "missing scalar. forEach drops empty contexts; forEachOrNull emits one null context.",
            "combination": "Sibling selects cross-product per resource; unionAll concatenates, never deduplicates.",
        },
        "terminology_policy": {
            "version": "Exact system and version. No external lookups, cross-system display matching or version fallback.",
            "translate": "Parameters.parameter contains result/valueBoolean plus every match, each with "
                         "part equivalence/valueCode and concept/valueCoding(system,version,code,display). "
                         "Exclude disjoint/unmatched and unmet dependsOn targets. No match => false, no match parts.",
            "validate_code": "Parameters.parameter=[{name:'result',valueBoolean:bool}]. Case-sensitive codes; "
                             "supplied display must exactly match; activeOnly rejects inactive concepts; "
                             "valueSet restricts membership if supplied.",
            "expand": "ValueSet with request url and expansion {identifier,timestamp,total,offset,contains}. "
                      "Identifier=urn:ops:deterministic-expansion; timestamp=2026-01-01T00:00:00Z. "
                      "Sort unique members by system/version/code before paging. Apply activeOnly and "
                      "case-insensitive display-substring filter before total/offset/count. Members contain "
                      "system/version/code/display and inactive:true only for inactive members.",
        },
    }


def build_release(root: Path = OPERATIONS_ROOT) -> list[dict]:
    """Author the exact 10-per-category release; reference functions derive gold."""
    root = Path(root)
    tasks: list[dict] = []

    def write(name: str, value: Any) -> None:
        path = root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(canonical(value) + b"\n")

    terminology = _terminology()
    write("agent/terminology.json", terminology)
    folder = _bundle([
        _patient(), _patient("b", active=False, name=[]),
        _observation(),
        _observation("o2", status="preliminary", valueQuantity={"value": 7.5, "unit": "mg"},
                     effectiveDateTime="2024-02-29T00:00:00Z"),
        _observation("o3", subject={"reference": "Patient/b"}, valueQuantity={"value": 3, "unit": "g"},
                     component=[{"code": {"text": "panel-a"}, "valueQuantity": {"value": 2, "unit": "mg"}},
                                {"code": {"text": "panel-b"}, "valueQuantity": {"value": 4, "unit": "mg"}}]),
        _observation("o4", valueQuantity=None, status="cancelled",
                     component=[{"code": {"text": "panel-a"}, "valueQuantity": {"value": 2, "unit": "mg"}}]),
        {"resourceType": "Encounter", "id": "e1", "status": "finished", "class": {
            "system": "urn:ops:encounter-class", "code": "visit"}, "subject": {"reference": "Patient/a"},
         "period": {"start": "2024-02-28T22:00:00Z", "end": "2024-03-01T01:00:00Z"}},
    ])
    write("agent/folder.json", folder)

    def task(category: str, number: int, title: str, instructions: str, fixtures: dict,
             steps: list[dict], *, outputs: dict[str, str] | None = None,
             shared: list[str] | None = None, capabilities: list[str]) -> None:
        identity = f"{category}-{number:02d}"
        prefix = f"inputs/{identity}/"
        names = []
        for name, value in fixtures.items():
            write("agent/" + prefix + name, value)
            names.append(prefix + name)
        names += shared or []
        # Local asset names in steps become public relative paths; output names stay output names.
        resolved_steps = []
        for step in steps:
            resolved_steps.append({k: prefix + v if isinstance(v, str) and v in fixtures else v
                                   for k, v in step.items()})
        inventory = outputs or {"result.json": "FHIR resource"}
        prompt = (f"{instructions} Read only the declared inputs listed in task.json. "
                  f"Write exactly {', '.join(inventory)} to a fresh output folder; do not retain logs, "
                  "raw copies, or intermediate files. Follow the versioned whole-output contract. "
                  "Use the pinned owned fixtures, not an external terminology service. "
                  "The source folder is synthetic and immutable.")
        record = {"id": identity, "version": 1, "category": category, "tier": (number + 1) // 2,
                  "title": title, "prompt": prompt, "verification_status": "unverified",
                  "capabilities_advantaged": capabilities, "inputs": names, "steps": resolved_steps,
                  "contract": _contract(inventory), "provenance": {
                      "owner": "fhir-question-bench contributors", "license": "MIT",
                      "source": "Original synthetic fixtures; independently computed offline reference gold.",
                      "expert_review": "not performed", "exposure": "adaptive development material"}}
        record["contract"]["input_sha256"] = {
            name: hashlib.sha256((root / "agent" / name).read_bytes()).hexdigest() for name in names}
        tasks.append(record)

    deid_cap = ["policy-driven field suppression", "date consistency", "reference-preserving pseudonymization"]
    mapping_cap = ["StructureMap execution", "typed target construction", "cardinality diagnostics"]
    view_cap = ["SQL-on-FHIR v2 ViewDefinition", "FHIRPath collection semantics", "complete folder scan"]
    validation_cap = ["pinned profile validation", "complete OperationOutcome findings", "owned terminology binding"]
    mixed_cap = ["version-pinned owned terminology", "multi-artifact operations", "explicit unmatched semantics"]

    # De-identification: ten distinct policies/cases, not name-only paraphrases.
    deid_specs = [
        ("Safe-Harbor-style demographic minimization", _patient(),
         _policy(), "Prepare a demographic-only research extract. Remove the configured direct identifiers "
         "and sub-state address fields, reduce birthDate to year, and suppress the low-population ZIP prefix."),
        ("Preserve nonidentifier clinical codes", _observation(note=[{"text": "Synthetic Person A"}],
             identifier=[{"value": "SYN-O"}], text={"status": "generated", "div": "<div>synthetic</div>"}),
         _policy(), "Sanitize this observation, dropping identifiers, narrative and notes. Preserve the "
         "code display, quantity, subject reference and clinical timestamp; the display is not a name."),
        ("Uniform leap-day shift", _bundle([_patient(), _observation(),
             {"resourceType": "Encounter", "id": "e", "subject": {"reference": "Patient/a"},
              "period": {"start": "2024-02-28T23:00:00Z", "end": "2024-03-01T01:00:00Z"}}]),
         _policy(shift_fields=["effectiveDateTime", "start", "end"], shift_days=2),
         "Create a shifted research Bundle with a +2-day clinical-date offset. Preserve encounter duration "
         "across leap day; birth dates are year-only, not shifted."),
        ("Consistent negative date shifts", _bundle([_observation(),
             _observation("o2", effectiveDateTime="2024-03-01T00:00:00Z")]),
         _policy(shift_fields=["effectiveDateTime"], shift_days=-1),
         "Shift both clinical instants back one day, including February 29. Do not change quantities or codes."),
        ("Bundle aliases and request references", {"resourceType": "Bundle", "type": "transaction", "entry": [
            {"fullUrl": "urn:uuid:a", "resource": _patient(),
             "request": {"method": "PUT", "url": "Patient/a"}},
            {"fullUrl": "urn:uuid:o1", "resource": _observation(subject={"reference": "urn:uuid:a"}),
             "request": {"method": "PUT", "url": "Observation/o1"}}]},
         _policy(id_map={"Patient/a": "Patient/p100", "Observation/o1": "Observation/l100",
                         "urn:uuid:a": "urn:uuid:p100", "urn:uuid:o1": "urn:uuid:l100"}),
         "Pseudonymize IDs, Bundle fullUrl aliases, request URLs and references using the exact supplied "
         "mapping. Preserve the observation-to-patient edge and transaction methods."),
        ("Two-patient offset isolation", _bundle([_patient(), _patient("b"),
             _observation(), _observation("o2", subject={"reference": "Patient/b"})]),
         _policy(shift_fields=["effectiveDateTime"], patient_shift_days={"Patient/a": 3, "Patient/b": -4}),
         "Apply the patient-specific offsets to each patient's clinical records. Select the shift using "
         "the original subject; never carry one patient's shift into the next Bundle entry."),
        ("Contained resources and local references", _observation(
             contained=[_patient("inside")], subject={"reference": "#inside"}),
         _policy(id_map={"Patient/inside": "Patient/anon", "#inside": "#anon",
                         "Observation/o1": "Observation/lab-anon"}),
         "Sanitize the contained Patient and rewrite its local # reference consistently. Preserve the "
         "containment boundary; do not invent a top-level patient."),
        ("ZIP threshold and 90-plus topcoding", _bundle([_patient(age=96),
             _patient("b", age=89, address=[{"postalCode": "98765", "city": "Imaginary"}])]),
         _policy(cap_age_90=True), "Apply the configured ZIP3 rule and topcode integer age at 90. "
         "The 123 prefix is suppressed to 000, 987 is retained, and ages below 90 stay exact. "
         "The integer age is an owned auxiliary fixture field, not an R4 Patient element."),
        ("Reference conservation with unlisted external links", _bundle([
             _patient(), _observation(performer=[{"reference": "https://example.invalid/Practitioner/pub"}]),
             _observation("o2", subject={"reference": "Patient/a"}, effectiveDateTime="2024-02-29T05:00:00+05:00")]),
         _policy(id_map={"Patient/a": "Patient/p9", "Observation/o1": "Observation/l9",
                         "Observation/o2": "Observation/l10"},
                 shift_fields=["effectiveDateTime"], patient_shift_days={"Patient/a": 7}),
         "Combine per-patient clinical shifts and pseudonymization, keeping repeated patient edges "
         "consistent. Preserve the unlisted public external performer reference unchanged; policy "
         "does not permit blanket reference deletion."),
        ("Nested privacy policy without clinical loss", _bundle([_patient(),
             _observation(component=[{"code": {"text": "owned panel label"}, "valueQuantity": {"value": 5, "unit": "mg"}}],
                          note=[{"text": "Synthetic location and contact narrative"}]),
             {"resourceType": "Encounter", "id": "e", "subject": {"reference": "Patient/a"},
              "period": {"start": "2023-12-31T23:00:00Z", "end": "2024-01-01T01:00:00Z"},
              "participant": [{"individual": {"reference": "#pr"}}],
              "contained": [{"resourceType": "Practitioner", "id": "pr", "name": [{"text": "Synthetic"}]}]}]),
         _policy(shift_fields=["effectiveDateTime", "start", "end"], shift_days=1,
                 remove_fields=["name", "identifier", "telecom", "line", "city", "note", "photo", "contact"],
                 id_map={"Patient/a": "Patient/research", "Observation/o1": "Observation/research-lab",
                         "Practitioner/pr": "Practitioner/staff", "#pr": "#staff"}),
         "Apply the recursive research policy across a year boundary and contained staff. Unlike other "
         "policies, code.text is retained here; remove only the named fields. Preserve all entries, "
         "component measurements and encounter duration."),
    ]
    for number, (title, source, policy, instructions) in enumerate(deid_specs, 1):
        task("deidentification", number, title, instructions, {"input.json": source, "policy.json": policy},
             [{"kind": "deidentify", "input": "input.json", "policy": "policy.json", "output": "result.json"}],
             capabilities=deid_cap)

    # Maps are actual owned R4 StructureMap resources. They are intentionally small.
    name_rule = {"name": "name-projection",
                 "source": [{"context": "src", "element": "name", "variable": "n"}],
                 "target": [{"context": "tgt", "element": "name", "transform": "create",
                             "parameter": [{"valueString": "HumanName"}], "variable": "outname"}],
                 "rule": [_copy_rule("family", context="n", target="outname"),
                          _copy_rule("given", context="n", target="outname")]}
    component_rule = {"name": "components",
                      "source": [{"context": "src", "element": "component", "variable": "c"}],
                      "target": [{"context": "tgt", "element": "component", "transform": "create",
                                  "variable": "outcomponent", "parameter": [{"valueString": "BackboneElement"}]}],
                      "rule": [_copy_rule("code", context="c", target="outcomponent"),
                               _copy_rule("valueQuantity", context="c", target="outcomponent")]}
    map_specs = [
        ("Patient active projection", _patient(), _map([_copy_rule("id"), _copy_rule("active")]),
         "Project only resourceType, id and active from the Patient using the supplied StructureMap."),
        ("Observation status normalization", _observation(status="preliminary"),
         _map([_copy_rule("id"), _constant("status", "final"), _copy_rule("code"),
               _copy_rule("valueQuantity")], "Observation", "Observation"),
         "Apply the map's fixed final status and copied code/quantity. This is a synthetic transform, "
         "not an instruction to promote real clinical preliminary results."),
        ("Component selection into scalar value", _observation(component=[
            {"code": {"text": "panel-a"}, "valueQuantity": {"value": 2, "unit": "mg"}},
            {"code": {"text": "panel-b"}, "valueQuantity": {"value": 9, "unit": "mg"}}]),
         _map([_copy_rule("id"), _copy_rule("status"), _copy_rule("code"),
               {"name": "select-a", "source": [{"context": "src", "element": "component", "variable": "c",
                                              "condition": "code.text = 'panel-a'"}],
                "rule": [_copy_rule("valueQuantity", context="c")]}], "Observation", "Observation"),
         "Project the panel-a component quantity into the target observation's scalar valueQuantity; "
         "exclude panel-b. The supplied map declares which fields survive."),
        ("Nested HumanName projection", _patient(name=[
            {"use": "official", "family": "Synthetic", "given": ["First", "Second"]},
            {"use": "nickname", "given": ["Alias"]}]),
         _map([_copy_rule("id"), name_rule]),
         "Construct each target HumanName with only family and given. Keep given-name multiplicity and "
         "order, preserve both names, and drop the source use fields."),
        ("Identifier synthesis", _patient(),
         _map([_copy_rule("id"), {"name": "identifier", "source": [{"context": "src"}],
               "target": [{"context": "tgt", "element": "identifier", "transform": "create",
                           "parameter": [{"valueString": "Identifier"}], "variable": "ident"}],
               "rule": [_constant("system", "urn:ops:export-id", target="ident"),
                        _copy_rule("id", target="ident", output="value")]}]),
         "Create an export identifier from source id and the map's fixed export system. Do not copy "
         "the original record-number identifier."),
        ("Patient-to-Observation reference construction", _patient(),
         _map([_copy_rule("id"), _constant("status", "final"),
               _derived_code_rule("active-marker", "Owned synthetic active marker"),
               {"name": "subject", "source": [{"context": "src", "element": "id", "variable": "pid"}],
                "target": [{"context": "tgt", "element": "subject", "transform": "create",
                            "parameter": [{"valueString": "Reference"}], "variable": "ref"},
                           {"context": "ref", "element": "reference", "transform": "append",
                            "parameter": [{"valueString": "Patient/"}, {"valueId": "pid"}]}]},
               {"name": "active-only", "source": [{"context": "src", "condition": "active = true"}],
                "target": [{"context": "tgt", "element": "valueBoolean", "transform": "copy",
                            "parameter": [{"valueBoolean": True}]}]}], "Patient", "Observation"),
         "Build the mapped observation with its subject Reference and conditional boolean. Preserve "
         "the exact constructed Patient/id link; the map is a fixture projection, not a clinical event."),
        ("Cross-type Condition projection", {"resourceType": "Condition", "id": "c",
             "subject": {"reference": "Patient/a"}, "code": {"text": "Owned finding"},
             "clinicalStatus": {"coding": [{"code": "active"}]}},
         _map([_copy_rule("id"), _constant("status", "final"), _copy_rule("subject"),
               _copy_rule("code")], "Condition", "Observation"),
         "Transform the owned Condition into the map's Observation projection. Exclude unmapped "
         "clinicalStatus and preserve the source code and subject verbatim."),
        ("Atomic required-source failure", _observation(subject=None),
         _map([_copy_rule("id"), _copy_rule("subject", required=True), _copy_rule("status")],
              "Observation", "Observation"),
         "Attempt the map with a required single subject. Return only an OperationOutcome with the "
         "required-source error at src.subject, not the partially constructed target."),
        ("Repeated nested component transform", _observation(component=[
            {"code": {"text": "A"}, "valueQuantity": {"value": 1, "unit": "mg"}, "extra": "drop"},
            {"code": {"text": "B"}, "valueQuantity": {"value": 1, "unit": "mg"}}]),
         _map([_copy_rule("id"), _copy_rule("status"), _copy_rule("code"), component_rule],
              "Observation", "Observation"),
         "Create both nested target components via the map. Preserve duplicate measurements and "
         "component order, and omit the unmapped owned auxiliary source field extra."),
        ("Evaluate typed collection count", _patient(name=[
            {"given": ["One", "Two"]}, {"given": ["Three"]}]),
         _map([_copy_rule("id"), _constant("status", "final"),
               _derived_code_rule("given-name-count", "Owned synthetic given-name count"),
               {"name": "given-count", "source": [{"context": "src", "variable": "p"}],
                "target": [{"context": "tgt", "element": "valueInteger", "transform": "evaluate",
                            "parameter": [{"valueId": "p"}, {"valueString": "name.given.count()"}]}]},
               {"name": "subject", "source": [{"context": "src", "element": "id", "variable": "pid"}],
                "target": [{"context": "tgt", "element": "subject", "transform": "create",
                            "parameter": [{"valueString": "Reference"}], "variable": "ref"},
                           {"context": "ref", "element": "reference", "transform": "append",
                            "parameter": [{"valueString": "Patient/"}, {"valueId": "pid"}]}]}],
              "Patient", "Observation"),
         "Evaluate name.given.count() across all names, emit an integer (not a string), and construct "
         "the subject reference. Return only the map's target fields."),
    ]
    for number, (title, source, mapping, instructions) in enumerate(map_specs, 1):
        task("mapping", number, title, instructions, {"input.json": source, "map.json": mapping},
             [{"kind": "map", "input": "input.json", "map": "map.json", "output": "result.json"}],
             outputs={"result.json": "FHIR resource or atomic OperationOutcome"}, capabilities=mapping_cap)

    view_specs = [
        ("Patient identifiers and booleans", _view("Patient", [{"column": [
            _column("patient_id", "id", "id"), _column("active", "active", "boolean")]}]),
         "Materialize the Patient view over the entire folder. Retain boolean false and both patients."),
        ("Final-observation filter", _view("Observation", [{"column": [
            _column("id", "id", "id"), _column("value", "valueQuantity.value", "decimal")]}],
            where=[{"path": "status = 'final'"}]),
         "Materialize only final observations. A zero quantity is a number, not missing data."),
        ("Typed missing values", _view("Observation", [{"column": [
            _column("id", "id", "id"), _column("value", "valueQuantity.value", "decimal"),
            _column("unit", "valueQuantity.unit")]}]),
         "Include every observation, including cancelled and missing quantities. Represent missing scalar "
         "values as JSON null rather than omitting the row or using an empty string."),
        ("Inner name unnest", _view("Patient", [{"column": [_column("id", "id", "id")]},
            {"forEach": "name.given", "column": [_column("given", "$this")]}]),
         "Unnest each given name with forEach. The patient with no names contributes no rows."),
        ("Outer name unnest", _view("Patient", [{"column": [_column("id", "id", "id")]},
            {"forEachOrNull": "name.given", "column": [_column("given", "$this")]}]),
         "Use forEachOrNull to retain the nameless patient with a null given-name column."),
        ("Nested panel projections", _view("Observation", [{"column": [_column("id", "id", "id")]},
            {"forEach": "component", "column": [_column("panel", "code.text"),
                                               _column("value", "valueQuantity.value", "decimal")]}]),
         "Unnest all observation components, including those on nonfinal observations. Preserve all "
         "panel rows and numeric types."),
        ("Collection-valued columns", _view("Patient", [{"column": [
            _column("id", "id", "id"), _column("given_names", "name.given", collection=True)]}]),
         "Keep given names as an ordered collection column. The missing name collection is [], not null. "
         "Do not explode rows or sort the given names."),
        ("Union of scalar and component measurements", _view("Observation", [
            {"column": [_column("id", "id", "id")]},
            {"unionAll": [
                {"column": [_column("value", "valueQuantity.value", "decimal"),
                            _column("origin", "'scalar'")]},
                {"forEach": "component", "column": [_column("value", "valueQuantity.value", "decimal"),
                                                  _column("origin", "'component'")]}]}]),
         "Materialize unionAll branches for scalar and component measurements. Keep the null scalar "
         "row and every component row; UNION-style deduplication is forbidden."),
        ("Filtered nested panel multiset", _view("Observation", [
            {"column": [_column("subject", "subject.reference")]},
            {"forEach": "component.where(code.text = 'panel-a')", "column": [
                _column("value", "valueQuantity.value", "decimal"), _column("unit", "valueQuantity.unit")]}]),
         "Project subject/value/unit for panel-a only. Two distinct observations produce identical "
         "rows where applicable; retain duplicate rows rather than using a set."),
        ("Sibling-selection cross product", _view("Patient", [
            {"column": [_column("id", "id", "id")]},
            {"forEachOrNull": "name.given", "column": [_column("given", "$this")]},
            {"forEachOrNull": "identifier", "column": [_column("system", "system"),
                                                      _column("identifier", "value")]}]),
         "Evaluate sibling outer selections as a per-patient cross product, not a zip. Include "
         "patient b with a null given name and its own identifier."),
    ]
    for number, (title, view, instructions) in enumerate(view_specs, 1):
        # Special cases add owned rows to make duplicate and cross-product traps observable.
        source = copy.deepcopy(folder)
        if number == 9:
            source["entry"].append({"resource": _observation("o5", subject={"reference": "Patient/b"},
                component=[{"code": {"text": "panel-a"}, "valueQuantity": {"value": 2, "unit": "mg"}}])})
        if number == 10:
            source["entry"][0]["resource"]["identifier"].append({"system": "urn:ops:secondary", "value": "ALT-a"})
        task("view", number, title, instructions, {"folder.json": source, "view.json": view},
             [{"kind": "view", "input": "folder.json", "view": "view.json", "output": "result.json"}],
             outputs={"result.json": "typed table {columns:[{name,type}],rows:[objects]}"},
             capabilities=view_cap)

    basic = [{"field": "status", "min": 1, "max": "1", "fixedCode": "final"}]
    binding = {"field": "code", "min": 1, "max": "1",
               "binding": {"strength": "required", "valueSet": "urn:ops:values"}}
    validation_specs = [
        ("Conformant projected Observation", _observation(), _profile(basic),
         "Validate the final observation against only the supplied differential. With no violations, "
         "emit one information/informational issue at Observation."),
        ("Required status absent", _observation(status=None), _profile(basic),
         "Validate the missing status. Report required/error at Observation.status; do not fabricate "
         "a second fixed-value issue for an absent value."),
        ("Fixed-code violation", _observation(status="preliminary"), _profile(basic),
         "Find the final-only profile violation as error/value at Observation.status."),
        ("Maximum cardinality violation", _patient(name=[{"family": "One"}, {"family": "Two"}]),
         _profile([{"field": "name", "min": 1, "max": "1"}], "Patient"),
         "Validate the one-name profile. Report a structure/error at Patient.name for two names."),
        ("Multiple independent profile failures", _observation(status="preliminary", valueQuantity=None),
         _profile([*basic, {"field": "valueQuantity", "min": 1, "max": "1", "type": [{"code": "Quantity"}]}]),
         "Report both the status fixed-value violation and the missing required quantity. Do not stop "
         "after the first issue; issue order and diagnostic prose do not matter."),
        ("Typed value and unit pattern", _observation(valueQuantity={"value": "5", "unit": "g"}),
         _profile([{"field": "valueQuantity", "min": 1, "max": "1", "patternQuantity": {"unit": "mg"}},
                   {"field": "valueQuantity.value", "min": 1, "max": "1", "type": [{"code": "decimal"}]}]),
         "Report the quantity unit-pattern error/value and the numeric-string type error/structure. "
         "A string containing digits is not a decimal."),
        ("Choice cardinality conflict", _observation(valueString="five"),
         _profile([{"field": "value[x]", "min": 1, "max": "1"}]),
         "Validate choice cardinality. Simultaneous valueString and valueQuantity violate max=1 at "
         "Observation.value[x], even though each alternative could individually be valid."),
        ("Required terminology binding failure", _observation(code={"coding": [{
             "system": "urn:ops:source", "version": "1", "code": "context", "display": "Owned signal A"}]}),
         _profile([binding]), "Check the required owned ValueSet binding. The display is a distractor: "
         "context is outside the value set. Report error/code-invalid at Observation.code."),
        ("Warning invariant plus required binding", _observation(subject=None, code={"coding": [{
             "system": "urn:ops:source", "version": "1", "code": "retired"}]}),
         _profile([{"field": "$this", "constraint": [{"key": "subject-present", "severity": "warning",
                     "human": "Owned warning: subject expected", "expression": "subject.exists()"}]}, binding]),
         "Report the subject-present warning/invariant (including constraint key) and the inactive "
         "code's error/code-invalid. The owned policy excludes inactive codes from required bindings."),
        ("Unresolved reference with conformant binding", _observation(subject={"reference": "Patient/absent"}),
         _profile([*basic, binding]), "Validate the supplied profile plus the explicit local-reference "
         "policy. The owned folder lacks Patient/absent: report error/not-found at "
         "Observation.subject.reference; no terminology violation is present."),
    ]
    for number, (title, source, profile, instructions) in enumerate(validation_specs, 1):
        if number == 9:
            # Root constraints use a true root path, not an invented differential element.
            root_rule = profile["differential"]["element"].pop(1)
            profile["differential"]["element"][0]["constraint"] = root_rule["constraint"]
        step = {"kind": "validate", "input": "input.json", "profile": "profile.json", "output": "result.json"}
        shared = []
        if number >= 8:
            step["terminology"] = "terminology.json"
            shared.append("terminology.json")
        if number == 10:
            step.update({"folder": "folder.json", "check_references": True})
            shared.append("folder.json")
        task("validation", number, title, instructions, {"input.json": source, "profile.json": profile},
             [step], outputs={"result.json": "OperationOutcome with exact severity/code/expression tuples"},
             shared=shared, capabilities=validation_cap)

    requests = [
        ("Translate a single equivalent code", "translate", {"system": "urn:ops:source", "version": "1", "code": "a"},
         "Perform the owned $translate-style request. Return Parameters with result and all matched target concepts."),
        ("Validate display and version", "validate-code", {"system": "urn:ops:source", "version": "1",
             "code": "a", "display": "Owned signal A"},
         "Perform the $validate-code-style request against the exact owned CodeSystem version, including display."),
        ("Expand active membership", "expand", {"url": "urn:ops:values", "activeOnly": True},
         "Expand the owned ValueSet excluding inactive codes. Use the fixed expansion identifier/timestamp "
         "in the contract fixture policy: urn:ops:deterministic-expansion and 2026-01-01T00:00:00Z."),
        ("Preserve multi-target translation", "translate", {"system": "urn:ops:source", "version": "1", "code": "many"},
         "Translate the one-to-many code. Retain both wider and relatedto matches and their distinct "
         "equivalence values; never choose an arbitrary preferred match."),
        ("Context-dependent translation", "translate", {"system": "urn:ops:source", "version": "1", "code": "context",
             "context": {"urn:ops:specimen": "urine"}},
         "Resolve the mapping's dependsOn using the supplied specimen context. Return only the urine match."),
        ("Missing-context abstention", "translate", {"system": "urn:ops:source", "version": "1", "code": "context"},
         "Translate without specimen context. Return result=false and no matches; do not guess serum or urine."),
        ("Inactive-code rejection and paged expansion", "validate-code", {"system": "urn:ops:source", "version": "1",
             "code": "retired", "activeOnly": True},
         "Reject the inactive code using $validate-code, then produce the separate active expansion page "
         "requested in page.json. Expansion total describes the whole filtered set, not page length. "
         "Use fixed expansion identifier urn:ops:deterministic-expansion and timestamp 2026-01-01T00:00:00Z."),
    ]
    for number, (title, kind, request, instructions) in enumerate(requests, 1):
        fixtures = {"request.json": request}
        steps = [{"kind": kind, "input": "request.json", "terminology": "terminology.json", "output": "result.json"}]
        outputs = {"result.json": "Parameters" if kind != "expand" else "ValueSet expansion"}
        if number == 7:
            fixtures["page.json"] = {"url": "urn:ops:values", "activeOnly": True, "offset": 1, "count": 1}
            steps.append({"kind": "expand", "input": "page.json", "terminology": "terminology.json", "output": "page.json"})
            # Output uses a different name than the input artifact.
            steps[-1]["output"] = "expansion.json"
            outputs["expansion.json"] = "paged ValueSet expansion"
        task("mixed", number, title, instructions, fixtures, steps, outputs=outputs,
             shared=["terminology.json"], capabilities=mixed_cap)

    task("mixed", 8, "Sanitize then materialize a research view",
         "De-identify the supplied Bundle with its exact ID mapping and +2-day clinical shift, then "
         "materialize the ViewDefinition over the sanitized resources (not the originals). Return both "
         "sanitized.json and result.json; the view's subject IDs must reflect rewritten references.",
         {"folder.json": _bundle([_patient(), _observation()]),
          "policy.json": _policy(id_map={"Patient/a": "Patient/anon", "Observation/o1": "Observation/anon-lab"},
                                  shift_fields=["effectiveDateTime"], shift_days=2),
          "view.json": _view("Observation", [{"column": [_column("id", "id", "id"),
                                                        _column("subject", "subject.reference"),
                                                        _column("value", "valueQuantity.value", "decimal")]}])},
         [{"kind": "deidentify", "input": "folder.json", "policy": "policy.json", "output": "sanitized.json"},
          {"kind": "view", "input": "sanitized.json", "view": "view.json", "output": "result.json"}],
         outputs={"sanitized.json": "sanitized Bundle", "result.json": "typed table"}, capabilities=deid_cap + view_cap)
    task("mixed", 9, "Map then validate using a pinned owned binding",
         "Execute the StructureMap projection, then validate the mapped Observation against the supplied "
         "profile and owned terminology. Return mapped.json and result.json. The source's context code "
         "is intentionally outside the required ValueSet; successful transformation is not validation success.",
         {"input.json": _observation(code={"coding": [{"system": "urn:ops:source", "version": "1", "code": "context"}]}),
          "map.json": _map([_copy_rule("id"), _copy_rule("status"), _copy_rule("code"), _copy_rule("subject")],
                            "Observation", "Observation"),
          "profile.json": _profile([*basic, binding])},
         [{"kind": "map", "input": "input.json", "map": "map.json", "output": "mapped.json"},
          {"kind": "validate", "input": "mapped.json", "profile": "profile.json",
           "terminology": "terminology.json", "output": "result.json"}],
         outputs={"mapped.json": "mapped Observation", "result.json": "OperationOutcome"},
         shared=["terminology.json"], capabilities=mapping_cap + validation_cap)
    task("mixed", 10, "Wrong-version translation plus deidentification and validation",
         "Produce three independent artifacts: result.json for the wrong-version $translate request "
         "(no version fallback), sanitized.json for the policy-governed source Bundle, and validation.json "
         "for the original separate observation under the owned profile/binding. Do not treat a false "
         "translation result as successful mapping or suppress other diagnostics. No intermediate files.",
         {"request.json": {"system": "urn:ops:source", "version": "99", "code": "a"},
          "folder.json": _bundle([_patient(), _observation(note=[{"text": "Synthetic sensitive narrative"}])]),
          "policy.json": _policy(shift_fields=["effectiveDateTime"], shift_days=-30,
                                  id_map={"Patient/a": "Patient/z", "Observation/o1": "Observation/z-lab"}),
          "invalid.json": _observation(status="preliminary", code={"coding": [{
              "system": "urn:ops:source", "version": "1", "code": "retired"}]}),
          "profile.json": _profile([*basic, binding])},
         [{"kind": "translate", "input": "request.json", "terminology": "terminology.json", "output": "result.json"},
          {"kind": "deidentify", "input": "folder.json", "policy": "policy.json", "output": "sanitized.json"},
          {"kind": "validate", "input": "invalid.json", "profile": "profile.json",
           "terminology": "terminology.json", "output": "validation.json"}],
         outputs={"result.json": "Parameters", "sanitized.json": "sanitized Bundle",
                  "validation.json": "OperationOutcome"}, shared=["terminology.json"],
         capabilities=mixed_cap + deid_cap + validation_cap)

    write("agent/tasks.json", tasks)
    for record in tasks:
        for name, value in recompute_task(record, root).items():
            write(f"private/expected/{record['id']}/{name}", value)
    manifest = {"version": 1, "release": "operations-50-v1", "task_count": len(tasks),
                "ownership": "Original synthetic content, MIT; no external terminology descriptions.",
                "reference_sha256": hashlib.sha256(Path(__file__).with_name("operations_reference.py").read_bytes()).hexdigest(),
                "grader_sha256": hashlib.sha256(Path(__file__).with_name("operations.py").read_bytes()).hexdigest(),
                "files": [{"path": p.relative_to(root).as_posix(), "sha256": hashlib.sha256(p.read_bytes()).hexdigest()}
                          for p in sorted(root.rglob("*")) if p.is_file() and p.name != "operations-manifest.json"]}
    write("operations-manifest.json", manifest)
    return tasks


if __name__ == "__main__":
    generated = build_release()
    print(f"Authored {len(generated)} unverified operational tasks; no inference or tool execution.")
