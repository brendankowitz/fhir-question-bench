"""Rebuild owned QA fixtures, metadata and hash locks without running inference.

Run from the repository root. The first 20 legacy records are preserved except
for additive metadata. No operations assets or private gold are generated.
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path


ROOT = Path("src/fhir_question_bench/data")
AGENT = ROOT / "agent"
LAB = "urn:bench:qa:lab"
LOCAL = "urn:bench:qa:source"
TARGET = "urn:bench:qa:target"
MAP = "urn:bench:qa:map"
SNOMED = "http://snomed.info/sct"
LOINC = "http://loinc.org"
RXNORM = "http://www.nlm.nih.gov/research/umls/rxnorm"
ICD10 = "http://hl7.org/fhir/sid/icd-10-cm"
UCUM = "http://unitsofmeasure.org"


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")


def write_rows(path, rows):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("".join(json.dumps(r, separators=(",", ":"), ensure_ascii=False) + "\n" for r in rows), encoding="utf-8", newline="\n")


def coding(system, code, display=None, version=None):
    return {"coding": [{"system": system, "code": code,
                        **({"display": display} if display else {}),
                        **({"version": version} if version else {})}]}


def resource(kind, identifier, **fields):
    return {"resourceType": kind, "id": identifier, **fields}


def ref(kind, identifier):
    return {"reference": f"{kind}/{identifier}"}


def build():
    questions = [json.loads(line) for line in (ROOT / "questions.jsonl").read_text().splitlines()][:20]
    reviews = [json.loads(line) for line in (ROOT / "review/review-ledger.jsonl").read_text().splitlines()][:20]
    for q, review in zip(questions, reviews):
        q["tier"] = 4 if q["primary_family"] == "cohort_missing_evidence" else int(q["difficulty"][1])
        q["verification_status"] = "provisional"
        q["capabilities_advantaged"] = [{
            "direct_facts": "Exact resource/field retrieval avoids file-level counting and identity errors.",
            "temporal_status_trends": "Structured patient, status and inclusive-time filtering exposes distractors.",
            "cross_resource_joins": "Source-scoped reference resolution preserves patient and encounter identity.",
            "owned_local_terminology": "Pinned lookup/translation exposes map strength and missing targets.",
            "cohort_missing_evidence": "Complete scoped searches distinguish missing evidence from a negative clinical fact.",
        }[q["primary_family"]]]
        review["verification_status"] = "provisional"
    protocols_path = AGENT / "protocols/protocols.json"
    protocols = json.loads(protocols_path.read_text())["protocols"][:20]
    policies_path = AGENT / "selection-policies.json"
    policies_document = json.loads(policies_path.read_text())
    policies = policies_document["policies"]

    def protocol(pid, operation, answer_type, definition, scope=None):
        protocols.append({"id": pid, "rule_id": pid + "-v1", "selection_policy": pid + "-v1",
                          "answer_type": answer_type, "operation": operation, "definition": definition})
        policies[pid + "-v1"] = {
            "operation": operation,
            "criteria": {"definition": definition},
            "completeness": {"scan": "all resources in the pinned corpus relevant to the explicit parameters",
                             "failure": "Unresolved potentially decisive references or unsupported versions never become non-matches."},
            "scope_template": scope or {"required": False}}

    protocol("p-qa-report", "qa-report-results", "set",
             "Resolve DiagnosticReport subject/specimen/results and each result's subject/specimen. Require identical source locations. Return unique result Observation identities; inconsistent or unresolved references stop derivation.")
    protocol("p-qa-repeat", "qa-repeat-cohort", "set",
             "Unique patients aged minimum_age on cutoff with at least two final coded observations, each >= minimum_value in exact unit, within inclusive start/cutoff and linked to the specified encounter class and same patient. Require separation >= minimum_days * 86400 seconds; missing dataAbsentReason values do not qualify. No diagnosis is inferred.",
             {"required": True, "resource_type": "Observation", "patient_reference": "*", "cutoff": "question.cutoff",
              "criteria": {k: {"parameter": k} for k in ("system", "code", "minimum_age", "minimum_value", "minimum_days", "start", "unit", "encounter_class")}})
    protocol("p-qa-translate", "qa-translate", "code",
             "Pinned synthetic ConceptMap, exact source system/version/code and target system/version; all dependsOn values must match context. Missing dependencies => ambiguous/null. Exactly one equal/equivalent target => code/mapping. No equivalent (including wider/narrower/relatedto/unmapped) => insufficient_evidence/null. These owned relationships do not establish clinical cross-system equivalence.")
    protocol("p-qa-translate-many", "qa-translate", "set",
             "Same pinned translation selection as p-qa-translate; multiple equivalent targets => ambiguous/set of all system/code/equivalence objects, sorted by system/code. Never arbitrarily choose one.")
    protocol("p-qa-subsumes", "qa-subsumes", "boolean",
             "In the exact owned CodeSystem version with hierarchyMeaning is-a, ancestor subsumes descendant if identical or a transitive parent. Reverse direction and unrelated branches are false; unknown concepts/hierarchy => insufficient_evidence/null.")
    protocol("p-qa-resolve", "qa-code-resolve", "code",
             "Resolve supplied system/code/version or the first Coding on the named resource against owned CodeSystem fixtures. Return authoritative owned display, not Coding.display. Missing version with multiple matching releases => ambiguous/null; inactive => invalid_data/null; absent concept => insufficient_evidence/null. No external terminology lookup or clinical assertion.")
    protocol("p-qa-ucum", "qa-ucum", "quantity",
             "Use valueQuantity.system/code, never display unit text. Pinned owned UCUM factor table: number * source factor / target factor, only within identical dimensions. Missing/incompatible units => insufficient_evidence/null; no analyte-specific mass-to-molar inference. Cite quantity and the complete unit table.")
    by_protocol = {p["id"]: p for p in protocols}

    def add(qid, tier, family, pid, persona, prompt, params, capability, cutoff="2026-01-15T00:00:00Z", task=None):
        p = by_protocol[pid]
        questions.append({"id": qid, "primary_family": family, "task_family_id": task or p["operation"],
                          "difficulty": f"L{tier}", "tier": tier, "verification_status": "unverified",
                          "capabilities_advantaged": [capability], "persona": persona, "cutoff": cutoff,
                          "corpus_id": questions[0]["corpus_id"], "protocol_id": pid,
                          "selection_policy": p["selection_policy"], "answer_type": p["answer_type"],
                          "review_state": "provisional", "prompt": prompt, "parameters": params})
        reviews.append({"question_id": qid, "review_state": "provisional", "verification_status": "unverified",
                        "generator": "Copilot deterministic owned QA expansion; no inference API",
                        "clinical_review": "not performed",
                        "terminology_review": "not performed" if tier == 5 else "not applicable",
                        "deterministic_checks": "source-derived gold and exact evidence recomputed by audit; not expert verification"})

    resources = []
    names = ["Avery", "Blair", "Casey", "Drew", "Ellis", "Finley", "Gray", "Harper"]
    births = ["1970-02-01", "1985-07-03", "2012-04-09", "1990-11-12",
              "1960-05-08", "1975-06-20", "1940-08-01", "1980-03-14"]
    for i, (name, birth) in enumerate(zip(names, births), 1):
        prefix = f"qa-{i:02}"
        patient = ref("Patient", prefix)
        encounter = ref("Encounter", prefix + "-visit")
        resources.append(resource("Patient", prefix, active=True, birthDate=birth,
                                  name=[{"use": "official", "family": "Synthetic", "given": [name]}]))
        resources.append(resource("Encounter", prefix + "-visit", status="finished", subject=patient,
                                  **{"class": {"system": "urn:bench:encounter-class", "code": "inpatient" if i == 6 else "outpatient"}},
                                  period={"start": "2026-01-01T08:00:00Z", "end": "2026-01-10T12:00:00Z"},
                                  diagnosis=[{"condition": ref("Condition", prefix + "-problem"), "rank": 1}]))
        for suffix, status, day, value in [
            ("a", "final", 1, 3 if i == 4 else 4),
            ("b", "preliminary" if i == 5 else "final", 2 if i == 8 else 10, 3 if i == 4 else 5),
            ("prelim", "preliminary", 12, 99),
            ("error", "entered-in-error", 13, 88),
            ("future", "final", 20, 77),
        ]:
            resources.append(resource("Observation", prefix + "-marker-" + suffix, status=status,
                                      code=coding(LAB, "renal", "Owned renal marker", "1"), subject=patient,
                                      encounter=encounter, specimen=ref("Specimen", prefix + "-specimen"),
                                      effectiveDateTime=f"2026-01-{day:02}T09:00:00Z",
                                      valueQuantity={"value": value, "unit": "mmol/L", "system": UCUM, "code": "mmol/L"}))
        resources.append(resource("Observation", prefix + "-weight", status="final", subject=patient,
                                  code=coding(LAB, "weight", "Owned weight", "1"),
                                  effectiveDateTime="2026-01-08T09:00:00Z",
                                  valueQuantity={"value": 60 + i, "unit": "kg", "system": UCUM, "code": "kg"}))
        for suffix, status, onset in [("problem", "active", "2025-12-01"), ("resolved", "resolved", "2025-11-01"),
                                       ("future", "active", "2026-02-01")]:
            resources.append(resource("Condition", prefix + "-" + suffix, subject=patient,
                                      clinicalStatus=coding("urn:bench:condition-status", status),
                                      onsetDateTime=onset + "T00:00:00Z",
                                      code=coding("urn:bench:condition", "owned-" + suffix, "Owned record " + suffix)))
        medication = resource("Medication", prefix + "-med",
                              code=coding("urn:bench:qa:medication", f"product-{i}", f"Owned product {i}"))
        resources.append(medication)
        resources.append(resource("MedicationRequest", prefix + "-order", status="active", intent="order",
                                  subject=patient, encounter=encounter, medicationReference=ref("Medication", prefix + "-med")))
        contained = i % 2 == 0
        resources.append(resource("MedicationAdministration", prefix + "-dose", status="completed", subject=patient,
                                  context=encounter, effectiveDateTime="2026-01-05T09:00:00Z",
                                  medicationReference={"reference": "#dose-product"} if contained else ref("Medication", prefix + "-med"),
                                  **({"contained": [{**medication, "id": "dose-product"}]} if contained else {})))
        resources.append(resource("Specimen", prefix + "-specimen", subject=patient,
                                  type={"text": "Owned serum context, not clinical specimen validation"}))
        resources.append(resource("DiagnosticReport", prefix + "-report", status="final", subject=patient,
                                  code={"text": "Owned renal follow-up panel"},
                                  specimen=[ref("Specimen", prefix + "-specimen")],
                                  result=[ref("Observation", prefix + "-marker-a"), ref("Observation", prefix + "-marker-b")]))
    write_rows(AGENT / "corpus/qa-expansion.ndjson", resources)

    direct = [
        ("p-direct-001", {"patient": "Patient/qa-01"}, "nursing", "At Avery's intake, what birth date is documented for Patient/qa-01? I need the recorded date, not an inferred age."),
        ("p-direct-001", {"patient": "Patient/qa-03"}, "specialist", "Before reviewing Casey's pediatric referral, retrieve the birth date on Patient/qa-03."),
        ("p-direct-001", {"patient": "Patient/qa-07"}, "researcher", "For Gray's research enrollment form, copy the exact birth date from Patient/qa-07 with its source."),
        ("p-direct-004", {"observation": "Observation/qa-02-weight"}, "nursing", "For Blair's handover, what weight and unit were actually charted in Observation/qa-02-weight? Do not convert it."),
        ("p-direct-004", {"observation": "Observation/qa-06-weight"}, "specialist", "In Finley's nutrition chart, retrieve the recorded quantity from Observation/qa-06-weight without rounding."),
        ("p-direct-003", {"condition": "Condition/qa-04-problem"}, "specialist", "For Drew's problem-list reconciliation, which first coding is recorded on Condition/qa-04-problem? This is retrieval, not diagnosis."),
        ("p-direct-003", {"condition": "Condition/qa-08-resolved"}, "researcher", "For the data dictionary check, return the stored system, code and display on Condition/qa-08-resolved; do not substitute a clinical label."),
        ("p-temporal-004", {"encounter": "Encounter/qa-01-visit"}, "nursing", "At Avery's handover, what encounter status is stored on Encounter/qa-01-visit?"),
        ("p-temporal-004", {"encounter": "Encounter/qa-05-visit"}, "researcher", "For Ellis's visit export, retrieve Encounter/qa-05-visit.status rather than infer discharge from its dates."),
        ("p-temporal-004", {"encounter": "Encounter/qa-08-visit"}, "specialist", "Before reviewing Harper's follow-up, what is the exact recorded status of Encounter/qa-08-visit?"),
    ]
    for i, (pid, params, persona, prompt) in enumerate(direct, 1):
        add(f"qa-direct-{i:03}", 1, "direct_facts", pid, persona, prompt, params,
            "Exact typed field retrieval distinguishes stored data from inferred clinical meaning.")

    for i in range(1, 9):
        p = f"qa-{i:02}"
        cutoff = "2026-01-09T23:59:59Z" if i in (2, 6) else "2026-01-15T00:00:00Z"
        add(f"qa-temporal-{i:03}", 2, "temporal_status_trends", "p-temporal-001", "specialist" if i % 2 else "nursing",
            f"For {names[i-1]}'s renal follow-up (Patient/{p}), retrieve the latest final {LAB}|renal quantity through {cutoff}. Exclude preliminary, entered-in-error, later and other-patient records; report the stored unit, not a diagnosis.",
            {"patient": f"Patient/{p}", "system": LAB, "code": "renal"},
            "Patient/code/status/time filtering exposes newer nonfinal and post-cutoff distractors.", cutoff)
    for i in range(1, 5):
        p = f"qa-{i+3:02}"
        add(f"qa-temporal-{i+8:03}", 2, "temporal_status_trends", "p-temporal-002", "researcher",
            f"For {names[i+2]}'s longitudinal quality check (Patient/{p}), list every final {LAB}|renal result through the cutoff in clinical-time order. Keep repeated values and exclude nonfinal and future results.",
            {"patient": f"Patient/{p}", "system": LAB, "code": "renal"},
            "Complete time-ordered retrieval preserves repeated values and excludes status distractors.")
    for i in range(1, 4):
        p = f"qa-{i+4:02}"
        add(f"qa-temporal-{i+12:03}", 2, "temporal_status_trends", "p-temporal-003", "specialist",
            f"In {names[i+3]}'s chart (Patient/{p}), which Condition identities are active with onset by the cutoff? Do not include resolved entries or future-onset problems.",
            {"patient": f"Patient/{p}"}, "Clinical-status and onset predicates prevent future/resolved entries entering the current problem set.")

    for i in range(1, 7):
        p = f"qa-{i:02}"
        add(f"qa-join-{i:03}", 3, "cross_resource_joins", "p-join-002", "specialist",
            f"For the medication administration review of {names[i-1]}, resolve MedicationAdministration/{p}-dose.medicationReference and return its product coding. Follow contained references when present; do not use a similarly named order.",
            {"administration": f"MedicationAdministration/{p}-dose"},
            "Reference resolution joins the actual administration to its medication, including contained products.",
            task="contained-medication-product" if i % 2 == 0 else "referenced-medication-product")
    for i in range(7, 11):
        p = f"qa-{i-6:02}"
        add(f"qa-join-{i:03}", 3, "cross_resource_joins", "p-join-001", "nursing",
            f"For {names[i-7]}'s reconciliation at Encounter/{p}-visit, list all linked MedicationRequest and MedicationAdministration identities belonging to the same patient. Keep orders distinct from completed doses.",
            {"encounter": f"Encounter/{p}-visit"}, "Encounter/subject joins avoid treating an order as evidence of administration.")
    for i in range(11, 15):
        p = f"qa-{i-10:02}"
        add(f"qa-join-{i:03}", 3, "cross_resource_joins", "p-qa-report", "researcher",
            f"For {names[i-11]}'s panel provenance check, resolve DiagnosticReport/{p}-report and return its result Observation identities only after confirming the report, specimen and each result share the same patient and specimen.",
            {"report": f"DiagnosticReport/{p}-report"}, "Report/specimen/result joins establish collection provenance instead of matching display strings.")

    cohort_settings = [(18, 4, 7), (40, 4, 7), (18, 5, 7), (18, 3, 7),
                       (18, 4, 10), (70, 4, 7), (18, 4, 1), (18, 4, 9)]
    for i, (age, threshold, gap) in enumerate(cohort_settings, 1):
        add(f"qa-cohort-{i:03}", 4, "cohort_missing_evidence", "p-qa-repeat", "researcher",
            f"For the synthetic renal follow-up feasibility study, return unique patients aged at least {age} on the cutoff with two final {LAB}|renal results >= {threshold} mmol/L in the inclusive 2026-01-01 to cutoff window, at least {gap} days apart, each linked to an outpatient encounter for that patient. This is a record-based research screen, not a disease diagnosis. Cite the complete candidate scope.",
            {"system": LAB, "code": "renal", "minimum_age": age, "minimum_value": threshold,
             "minimum_days": gap, "start": "2026-01-01T00:00:00Z", "unit": "mmol/L",
             "encounter_class": {"system": "urn:bench:encounter-class", "code": "outpatient"}},
            "Deduplicated cohort aggregation composes age, repeated measurements, interval, status and encounter predicates.",
            task="repeated-outpatient-research-screen")
    for i in range(9, 13):
        p = f"qa-{i-8:02}"
        add(f"qa-cohort-{i:03}", 4, "cohort_missing_evidence", "p-cohort-003", "nursing",
            f"For {names[i-9]}'s early handover (Patient/{p}), is a {LAB}|weight quantity available by 2026-01-07? Use only the exported pre-cutoff scope; a later weight must not fill this gap. Return insufficient evidence when absent, not a normal weight.",
            {"patient": f"Patient/{p}", "system": LAB, "code": "weight"},
            "Scoped missingness checks prevent a later charted weight from becoming a baseline.",
            cutoff="2026-01-07T23:59:59Z", task="pre-baseline-weight-missingness")
    for i in range(13, 17):
        p = f"qa-{i-8:02}"
        add(f"qa-cohort-{i:03}", 4, "cohort_missing_evidence", "p-cohort-004", "researcher",
            f"For {names[i-9]}'s export completeness review (Patient/{p}), list any {LAB}|uncollected Observation identities through the cutoff. An empty result means no matching exported evidence, not that the test was never performed.",
            {"patient": f"Patient/{p}", "system": LAB, "code": "uncollected"},
            "Complete patient/code/time scanning supports an export-bounded absence statement only.",
            task="uncollected-result-export-scope")

    terms = []

    def cs(identifier, url, version, concepts, **extra):
        return resource("CodeSystem", identifier, url=url, version=version, status="active", content="complete",
                        description="Benchmark-owned synthetic mechanics; displays are authored labels, not licensed terminology descriptions.",
                        concept=concepts, **extra)

    terms.append(cs("qa-lab", LAB, "1", [
        {"code": "renal", "display": "Owned renal marker"},
        {"code": "retired", "display": "Owned retired marker", "property": [{"code": "inactive", "valueBoolean": True}]},
        {"code": "weight", "display": "Owned weight"}],
        property=[{"code": "inactive", "type": "boolean"}]))
    terms.append(cs("qa-hierarchy", "urn:bench:qa:hierarchy", "1", [
        {"code": "panel", "display": "Owned panel", "concept": [
            {"code": "chemistry", "display": "Owned chemistry branch", "concept": [
                {"code": "renal", "display": "Owned renal leaf"}]}]},
        {"code": "hematology", "display": "Owned hematology branch"}], hierarchyMeaning="is-a"))
    for version, display in [("1", "Owned specimen marker"), ("2", "Owned medication marker")]:
        terms.append(cs("qa-ambiguous-" + version, "urn:bench:qa:ambiguous", version, [{"code": "X", "display": display}]))
    terms.append(cs("qa-other", "urn:bench:qa:other", "1", [{"code": "X", "display": "Owned alternate-system marker"}]))
    terms.append(cs("qa-source", LOCAL, "1", [
        {"code": c, "display": "Owned mapping input " + c}
        for c in ("sct-proxy", "loinc-proxy", "rx-proxy", "icd-proxy", "broad", "narrow", "many", "unmapped", "contextual")]))
    terms.append(cs("qa-target", TARGET, "1", [{"code": c, "display": "Owned target " + c}
                                            for c in ("a", "b", "serum", "urine")]))
    groups = []

    def group(source, version, target, code, targets):
        groups.append({"source": source, "sourceVersion": version, "target": target,
                       "targetVersion": "synthea-source-identifiers" if target in (SNOMED, LOINC, RXNORM, ICD10) else "1",
                       "element": [{"code": code, "target": targets}]})

    for local, system, code in [("sct-proxy", SNOMED, "390906007"), ("loinc-proxy", LOINC, "34117-2"), ("rx-proxy", RXNORM, "310325")]:
        group(LOCAL, "1", system, local, [{"code": code, "equivalence": "equivalent"}])
    # No ICD-10 codes were observed in the pinned source: deliberately no fabricated map.
    group(LOCAL, "1", ICD10, "icd-proxy", [])
    group(LOINC, "synthea-source-identifiers", LOCAL, "34117-2", [{"code": "loinc-proxy", "equivalence": "equivalent"}])
    group(RXNORM, "synthea-source-identifiers", LOCAL, "310325", [{"code": "rx-proxy", "equivalence": "equivalent"}])
    group(LOCAL, "1", TARGET, "broad", [{"code": "a", "equivalence": "wider"}])
    group(LOCAL, "1", TARGET, "narrow", [{"code": "a", "equivalence": "narrower"}])
    group(LOCAL, "1", TARGET, "many", [{"code": "a", "equivalence": "equivalent"}, {"code": "b", "equivalence": "equivalent"}])
    group(LOCAL, "1", TARGET, "unmapped", [])
    group(LOCAL, "1", TARGET, "contextual", [
        {"code": "serum", "equivalence": "equivalent", "dependsOn": [{"property": "specimen", "value": "serum"}]},
        {"code": "urine", "equivalence": "equivalent", "dependsOn": [{"property": "specimen", "value": "urine"}]}])
    terms.append(resource("ConceptMap", "qa-map", url=MAP, version="1", status="active",
                          description="Owned synthetic relationships using three Synthea-observed identifiers only; NOT clinical equivalence, standard releases, or SNOMED/LOINC/RxNorm/ICD-10 mapping coverage.",
                          group=groups))
    terms.append(resource("Patient", "qa-term-patient", active=True, birthDate="1980-01-01"))
    for identifier, code, display in [("qa-display-renal", "renal", "Owned weight"),
                                       ("qa-display-retired", "retired", "Owned renal marker")]:
        terms.append(resource("Observation", identifier, status="final", subject=ref("Patient", "qa-term-patient"),
                              effectiveDateTime="2025-01-01T00:00:00Z",
                              valueQuantity={"value": 1, "unit": "mmol/L"},
                              code=coding(LAB, code, display, "1")))
    terms.append(resource("Condition", "qa-display-condition", subject=ref("Patient", "qa-term-patient"),
                          clinicalStatus=coding("urn:bench:condition-status", "resolved"),
                          onsetDateTime="2025-01-01T00:00:00Z",
                          code=coding("urn:bench:qa:other", "X", "Owned specimen marker", "1")))
    terms.append(cs("qa-units", UCUM, "owned-linear-v1", [
        {"code": code, "display": "Owned unit factor " + code, "property": [
            {"code": "dimension", "valueString": dimension}, {"code": "factor", "valueDecimal": factor}]}
        for code, dimension, factor in [("g", "mass", 1), ("kg", "mass", 1000),
                                       ("g/dL", "mass/volume", 10), ("g/L", "mass/volume", 1),
                                       ("mmol/L", "amount/volume", 1)]],
        property=[{"code": "dimension", "type": "string"}, {"code": "factor", "type": "decimal"}]))
    terms[-1]["content"] = "fragment"
    for identifier, value, code, display in [("qa-unit-weight", 72500, "g", "kg"), ("qa-unit-hgb", 12.8, "g/dL", "g/L")]:
        terms.append(resource("Observation", identifier, status="final", subject=ref("Patient", "qa-term-patient"),
                              effectiveDateTime="2025-01-01T00:00:00Z",
                              code=coding(LAB, "weight" if code == "g" else "renal"),
                              valueQuantity={"value": value, "system": UCUM, "code": code, "unit": display}))
    write_json(AGENT / "terminology/qa-fixtures.json",
               resource("Bundle", "qa-terminology-fixtures", type="collection", entry=[{"resource": r} for r in terms]))

    def translate(code, target=TARGET, system=LOCAL, version="1", **extra):
        return {"map_id": MAP, "map_version": "1", "system": system, "version": version,
                "code": code, "target_system": target,
                "target_version": "synthea-source-identifiers" if target in (SNOMED, LOINC, RXNORM, ICD10) else "1",
                **extra}

    hard = [
        ("p-qa-translate", translate("sct-proxy", SNOMED), "synthetic-snomed-translation", "For the specialist's terminology audit, translate the owned sct-proxy into the SNOMED-identifier target using only the pinned synthetic map. Report its explicit relation; do not claim clinical equivalence."),
        ("p-qa-translate", translate("loinc-proxy", LOINC), "synthetic-loinc-translation", "For the laboratory research extract, translate loinc-proxy into the LOINC-identifier target using the owned ConceptMap, not the local display. This tests synthetic mechanics only."),
        ("p-qa-translate", translate("rx-proxy", RXNORM), "synthetic-rxnorm-translation", "For medication export reconciliation, translate rx-proxy to the RxNorm-identifier target in the owned map. Do not infer ingredient, dose or actual clinical interchangeability."),
        ("p-qa-translate", translate("icd-proxy", ICD10), "unmapped-icd10-abstention", "For discharge research coding, is an exact ICD-10-CM translation supplied for icd-proxy? Use only the owned map and abstain if no target exists; never invent a diagnosis code."),
        ("p-qa-translate", translate("34117-2", LOCAL, LOINC, "synthea-source-identifiers"), "reverse-loinc-mapping", "For a cross-site laboratory import, reverse-lookup the Synthea-observed LOINC identifier 34117-2 into the owned local system using the explicitly supplied reverse group. Do not reverse a forward group implicitly."),
        ("p-qa-translate", translate("310325", LOCAL, RXNORM, "synthea-source-identifiers"), "reverse-rxnorm-mapping", "For the pharmacy data pipeline, map Synthea-observed RxNorm identifier 310325 into the owned local system via its pinned reverse group; use no remembered product description."),
        ("p-qa-translate", translate("broad"), "broader-is-not-equivalent", "For the cohort harmonization review, can broad be replaced by an exact target equivalent? Inspect relation strength and abstain if the only target is wider."),
        ("p-qa-translate", translate("narrow"), "narrower-is-not-equivalent", "For the specialist's crosswalk review, can narrow be treated as exactly equivalent? A narrower target is not an exact replacement under this protocol."),
        ("p-qa-translate-many", translate("many"), "multiple-equivalent-targets", "For the research import queue, return every exact target candidate for many and mark the translation ambiguous rather than silently choose one."),
        ("p-qa-translate", translate("unmapped"), "unmapped-local-abstention", "For the laboratory analyst, does unmapped have a supplied exact target? Return insufficient evidence when the map has no target; do not match by display."),
        ("p-qa-subsumes", {"system": "urn:bench:qa:hierarchy", "version": "1", "ancestor": "panel", "descendant": "renal"}, "transitive-local-subsumption", "For the research panel grouping, does panel subsume renal through the owned is-a hierarchy? Follow all parent levels, not label similarity."),
        ("p-qa-subsumes", {"system": "urn:bench:qa:hierarchy", "version": "1", "ancestor": "renal", "descendant": "panel"}, "directional-local-subsumption", "For the specialist's grouping audit, does renal subsume panel in the pinned local hierarchy? Preserve direction; being a descendant does not make it a parent."),
        ("p-qa-subsumes", {"system": "urn:bench:qa:hierarchy", "version": "1", "ancestor": "hematology", "descendant": "renal"}, "unrelated-local-branches", "For the laboratory extract, does hematology subsume renal in the owned hierarchy? Do not bridge separate branches based on a broad clinical association."),
        ("p-qa-resolve", {"resource": "Observation/qa-display-renal"}, "display-code-conflict", "For the nurse's chart-quality query, Observation/qa-display-renal has conflicting code and display text. Return the authoritative owned release coding/display, not the resource's misleading display."),
        ("p-qa-resolve", {"resource": "Condition/qa-display-condition"}, "system-display-conflict", "For the research code dictionary, Condition/qa-display-condition uses X with another system's display. Resolve its actual system/version/code and return the owned authoritative display."),
        ("p-qa-resolve", {"resource": "Observation/qa-display-retired"}, "inactive-code-rejection", "For the specialist's active-code export, is the coding on Observation/qa-display-retired valid in the pinned owned release? A familiar display must not hide its inactive property; return invalid_data if inactive."),
        ("p-qa-resolve", {"system": "urn:bench:qa:ambiguous", "version": "1", "code": "X"}, "local-version-one", "For the archived specimen extract, resolve X specifically in version 1 of the owned ambiguous system. The current release reuses the code, so do not silently upgrade."),
        ("p-qa-resolve", {"system": "urn:bench:qa:ambiguous", "version": "2", "code": "X"}, "local-version-two", "For the new medication extract, resolve X specifically in version 2 of the owned ambiguous system. Do not reuse the older specimen meaning."),
        ("p-qa-resolve", {"system": "urn:bench:qa:ambiguous", "code": "X"}, "missing-version-ambiguity", "For a researcher merging an unversioned local feed, can X be resolved uniquely in the owned ambiguous system? If releases disagree, return ambiguous rather than guess a version."),
        ("p-qa-resolve", {"system": "urn:bench:qa:other", "version": "1", "code": "X"}, "same-code-other-system", "For the nurse's imported code check, resolve X in urn:bench:qa:other version 1. The same character in the ambiguous system is not evidence of equivalence."),
        ("p-qa-translate", translate("contextual"), "missing-specimen-dependency", "For the laboratory harmonization queue, translate contextual when specimen context was not supplied. Report ambiguity if dependsOn cannot be evaluated; do not pick serum by default."),
        ("p-qa-translate", translate("contextual", context={"specimen": "serum"}), "specimen-dependent-translation", "For the serum research extract, translate contextual using the supplied specimen=serum dependency. Exclude the urine-only target even though its source code is identical."),
        ("p-qa-ucum", {"observation": "Observation/qa-unit-weight", "target_unit": "kg", "unit_table_version": "owned-linear-v1"}, "ucum-mass-normalization", "For the nurse's weight reconciliation, normalize Observation/qa-unit-weight to kg using UCUM system/code and the pinned factor table. Its unit display disagrees with its code; do not trust the label."),
        ("p-qa-ucum", {"observation": "Observation/qa-unit-hgb", "target_unit": "g/L", "unit_table_version": "owned-linear-v1"}, "ucum-concentration-normalization", "For the laboratory researcher, normalize Observation/qa-unit-hgb to g/L using its UCUM code, not its conflicting display unit. Preserve exact decimal arithmetic."),
        ("p-qa-ucum", {"observation": "Observation/qa-unit-hgb", "target_unit": "mmol/L", "unit_table_version": "owned-linear-v1"}, "unsupported-molar-conversion", "For the specialist's harmonized extract, can Observation/qa-unit-hgb be converted to mmol/L using only the supplied unit table? Without an analyte-specific factor, abstain rather than apply a generic mass-to-molar conversion."),
    ]
    for i, (pid, params, task, prompt) in enumerate(hard, 1):
        add(f"hard-{i:03}", 5, "owned_local_terminology", pid, ["specialist", "researcher", "nursing"][(i-1) % 3],
            prompt, params, {
                "p-qa-subsumes": "Versioned local hierarchy traversal establishes direction/transitivity without clinical inference.",
                "p-qa-resolve": "System/version-aware lookup exposes display disagreement, inactive codes and code reuse.",
                "p-qa-ucum": "UCUM code/dimension/factor lookup makes conversions auditable and rejects unsupported dimensions.",
            }.get(pid, "Pinned ConceptMap translation exposes equivalence, direction, multiplicity and context dependencies."), task=task)

    assert len(questions) == 100
    assert len({q["id"] for q in questions}) == 100
    write_rows(ROOT / "questions.jsonl", questions)
    write_rows(ROOT / "review/review-ledger.jsonl", reviews)
    write_json(protocols_path, {"protocols": protocols})
    write_json(policies_path, policies_document)
    provenance = {
        "version": 1, "kind": "QA generation provenance", "synthetic_only": True,
        "recipe": "agent-working/2026-10-15-fhir-question-benchmark/build_qa_expansion.py",
        "source_archive": {"filename": "synthea_sample_data_fhir_latest.zip",
                           "sha256": "56cb9e49f7ba6ad4e61c40aa80999f8c10a710823fed1becdf2502053777a521",
                           "use": "Read-only identifier observation; no patients or standard descriptions copied.",
                           "member": "Nilda678_Wiza601_e503e9b3-d149-653b-92df-ca4a8cb68e16.json",
                           "identifiers": {SNOMED: ["390906007"], LOINC: ["34117-2"], RXNORM: ["310325"]}},
        "owned_assets": ["corpus/qa-expansion.ndjson", "terminology/qa-fixtures.json"],
        "terminology_limits": "Owned labels, hierarchy, maps and linear factors only. Referenced identifiers are not imported standard releases. No ICD-10 identifiers observed in archive, so ICD-10 translation deliberately abstains. No clinical equivalence across standard vocabularies is asserted.",
        "verification": "80 new unverified machine-authored items; 20 original provisional. Audit is oracle/self-check, not clinician/terminologist acceptance.",
        "lineage": "Eight deterministic owned patient scenarios; threshold/interval variants share a cohort world and are correlated development material, not independent sealed samples.",
    }
    write_json(ROOT / "review/qa-provenance.json", provenance)
    manifest_path = AGENT / "corpus-manifest.json"
    manifest = json.loads(manifest_path.read_text())
    owned = ["corpus/qa-expansion.ndjson", "terminology/qa-fixtures.json"]
    for relative in owned:
        if not any(e["path"] == relative for e in manifest["files"]):
            manifest["files"].append({"path": relative, "sha256": ""})
    for entry in manifest["files"]:
        entry["sha256"] = hashlib.sha256((AGENT / entry["path"]).read_bytes()).hexdigest()
    write_json(manifest_path, manifest)
    dataset_path = ROOT / "dataset-manifest.json"
    dataset = json.loads(dataset_path.read_text())
    if not any(e["path"] == "review/qa-provenance.json" for e in dataset["files"]):
        dataset["files"].append({"path": "review/qa-provenance.json", "sha256": ""})
    for entry in dataset["files"]:
        entry["sha256"] = hashlib.sha256((ROOT / entry["path"]).read_bytes()).hexdigest()
    write_json(dataset_path, dataset)
    print(f"Rebuilt {len(questions)} QA questions and {len(reviews)} review rows; no gold exported.")


if __name__ == "__main__":
    build()
