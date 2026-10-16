"""Independent Python derivation of gold answers, written in a different style from the C# oracles.

Reads the question files named by a question-set file, evaluates each oracle against the corpus,
and prints or writes the golds as JSON. With --compare it checks them against `admit --show` output.
Run:  python -I tools/derive-gold/derive_gold.py [question-set] [--out PATH | --compare FILE]

Output is gold: never commit it or copy it into a run workspace.
"""
from __future__ import annotations

import argparse
import glob
import json
import math
import os
import re
import sys
import unicodedata
from collections import defaultdict
from datetime import date, datetime, timedelta, timezone

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))

NOW = datetime(2026, 8, 17, tzinfo=timezone.utc)
MRN_SYSTEM = "http://hospital.smarthealthit.org"
LOINC = "http://loinc.org"
SNOMED = "http://snomed.info/sct"
RXNORM = "http://www.nlm.nih.gov/research/umls/rxnorm"
CVX = "http://hl7.org/fhir/sid/cvx"

VALID_OBS_STATUS = {"final", "amended", "corrected"}
EXCLUDED_MED_STATUS = {"entered-in-error", "cancelled", "draft"}
INACTIVE_CONDITION = {"inactive", "resolved", "remission"}
EXCLUDED_VERIFICATION = {"refuted", "entered-in-error"}

INSUFFICIENT = "insufficient_data"


# ---------------------------------------------------------------- time helpers

def when(text: str | None) -> datetime | None:
    if not text:
        return None
    if len(text) == 4:
        return datetime(int(text), 1, 1, tzinfo=timezone.utc)
    if len(text) == 7:
        return datetime(int(text[:4]), int(text[5:7]), 1, tzinfo=timezone.utc)
    if len(text) == 10:
        return datetime.fromisoformat(text).replace(tzinfo=timezone.utc)
    moment = datetime.fromisoformat(text.replace("Z", "+00:00"))
    return moment if moment.tzinfo else moment.replace(tzinfo=timezone.utc)


def day(moment: datetime) -> str:
    """Calendar date in the instant's recorded offset (not converted to UTC)."""
    return moment.date().isoformat()


def full_years(birth: str, at: datetime) -> int:
    born = date.fromisoformat(birth)
    on = at.astimezone(timezone.utc).date()
    return on.year - born.year - ((on.month, on.day) < (born.month, born.day))


def window_start(params: dict) -> datetime | None:
    days = params.get("window_days")
    return NOW - timedelta(days=int(days)) if days is not None else None


def in_window(moment: datetime, start: datetime | None) -> bool:
    return moment <= NOW and (start is None or moment > start)


def round_half_away(value: float, digits: int) -> float:
    factor = 10 ** digits
    return math.copysign(math.floor(abs(value) * factor + 0.5) / factor, value)


def as_list(value) -> list:
    return value if isinstance(value, list) else [value]


# ---------------------------------------------------------------- corpus

class Corpus:
    def __init__(self, folder: str):
        self.by_patient: dict[str, dict[str, list]] = {}
        self.full_urls: dict[str, dict] = {}
        self.patients: dict[str, dict] = {}
        for path in sorted(glob.glob(os.path.join(folder, "*.json"))):
            with open(path, encoding="utf-8") as handle:
                bundle = json.load(handle)
            entries = bundle.get("entry", [])
            patient = next((e["resource"] for e in entries if e["resource"]["resourceType"] == "Patient"), None)
            for entry in entries:
                self.full_urls[entry.get("fullUrl", "")] = entry["resource"]
            if patient is None:
                continue
            mrn = next(i["value"] for i in patient.get("identifier", []) if i.get("system") == MRN_SYSTEM)
            self.patients[mrn] = patient
            grouped = defaultdict(list)
            for entry in entries:
                grouped[entry["resource"]["resourceType"]].append(entry["resource"])
            self.by_patient[mrn] = grouped

    def of(self, mrn: str, kind: str) -> list:
        return self.by_patient[mrn].get(kind, [])

    def resolve(self, reference: str) -> dict | None:
        return self.full_urls.get(reference)

    def living(self) -> list[str]:
        alive = []
        for mrn, patient in self.patients.items():
            died = when(patient.get("deceasedDateTime"))
            if (died is not None and died <= NOW) or patient.get("deceasedBoolean") is True:
                continue
            alive.append(mrn)
        return sorted(alive)


def codes_of(concept: dict | None, system: str) -> list[str]:
    if not concept:
        return []
    return [c["code"] for c in concept.get("coding", []) if c.get("system") == system and "code" in c]


def first_coding(concept: dict | None) -> dict | None:
    for coding in (concept or {}).get("coding", []):
        if "system" in coding and "code" in coding:
            return {"system": coding["system"], "code": coding["code"]}
    return None


# ---------------------------------------------------------------- observations

UNIT_CONVERSIONS = {
    ("kg", "[lb_av]"): lambda v: v * 2.2046226218,
    ("cm", "[in_i]"): lambda v: v / 2.54,
    ("Cel", "[degF]"): lambda v: v * 9 / 5 + 32,
}
ANALYTE_CONVERSIONS = {
    ("glucose", "mg/dL", "mmol/L"): lambda v: v / 18.016,
    ("glucose", "mmol/L", "mg/dL"): lambda v: v * 18.016,
    ("creatinine", "mg/dL", "umol/L"): lambda v: v * 88.4,
}
ANALYTE_OF = {"2339-0": "glucose", "2345-7": "glucose", "38483-4": "creatinine", "2160-0": "creatinine"}
SAME_UNIT = {("33914-3", "mL/min"): "mL/min/{1.73_m2}"}


def convert(value: float, unit: str, loinc: str, wanted: str | None) -> float:
    if wanted is None or unit == wanted:
        return value
    if SAME_UNIT.get((loinc, unit)) == wanted:
        return value
    if (unit, wanted) in UNIT_CONVERSIONS:
        return UNIT_CONVERSIONS[(unit, wanted)](value)
    key = (ANALYTE_OF.get(loinc), unit, wanted)
    if key in ANALYTE_CONVERSIONS:
        return ANALYTE_CONVERSIONS[key](value)
    raise ValueError(f"no conversion {unit} -> {wanted} for {loinc}")


def observation_instant(obs: dict) -> datetime | None:
    return when(obs.get("effectiveDateTime") or (obs.get("effectivePeriod") or {}).get("start") or obs.get("issued"))


def numeric_readings(corpus: Corpus, mrn: str, loincs, unit: str | None = None) -> list[dict]:
    """Valid observations that carry one of the LOINC codes with a numeric quantity,
    either on Observation.code + valueQuantity or on a component with that code."""
    wanted = set(as_list(loincs))
    readings = []
    for obs in corpus.of(mrn, "Observation"):
        if obs.get("status") not in VALID_OBS_STATUS:
            continue
        moment = observation_instant(obs)
        if moment is None or moment > NOW:
            continue
        carriers = [(obs.get("code"), obs.get("valueQuantity"))]
        carriers += [(c.get("code"), c.get("valueQuantity")) for c in obs.get("component", [])]
        for concept, quantity in carriers:
            hit = [c for c in codes_of(concept, LOINC) if c in wanted]
            if not hit or not quantity or not isinstance(quantity.get("value"), (int, float)):
                continue
            raw_unit = quantity.get("code") or quantity.get("unit") or ""
            readings.append({
                "id": obs["id"], "when": moment, "loinc": hit[0],
                "value": convert(float(quantity["value"]), raw_unit, hit[0], unit),
                "unit": unit or raw_unit,
            })
            break
    return readings


def newest_first(items: list[dict]) -> list[dict]:
    return sorted(items, key=lambda r: (-r["when"].timestamp(), r["id"]))


def oldest_first(items: list[dict]) -> list[dict]:
    return sorted(items, key=lambda r: (r["when"].timestamp(), r["id"]))


def value_date(reading: dict, digits: int = 4) -> dict:
    return {"value": {"value": round(reading["value"], digits), "unit": reading["unit"]}, "date": day(reading["when"])}


def latest_observation(corpus, mrn, p):
    start = window_start(p)
    shape = p.get("return", "value_date")
    if shape == "code":
        coded = []
        for obs in corpus.of(mrn, "Observation"):
            moment = observation_instant(obs)
            if (obs.get("status") in VALID_OBS_STATUS and moment and in_window(moment, start)
                    and set(codes_of(obs.get("code"), LOINC)) & set(as_list(p["loinc"]))
                    and obs.get("valueCodeableConcept")):
                coded.append({"id": obs["id"], "when": moment, "code": first_coding(obs["valueCodeableConcept"])})
        return newest_first(coded)[0]["code"] if coded else None
    if shape == "components":
        panels = []
        for obs in corpus.of(mrn, "Observation"):
            moment = observation_instant(obs)
            if not (obs.get("status") in VALID_OBS_STATUS and moment and in_window(moment, start)
                    and p["loinc"] in codes_of(obs.get("code"), LOINC)):
                continue
            parts = {}
            for name, code in p["components"].items():
                for comp in obs.get("component", []):
                    quantity = comp.get("valueQuantity") or {}
                    if code in codes_of(comp.get("code"), LOINC) and isinstance(quantity.get("value"), (int, float)):
                        parts[name] = {"value": quantity["value"], "unit": quantity.get("code") or quantity.get("unit")}
            if len(parts) == len(p["components"]):
                panels.append({"id": obs["id"], "when": moment, "parts": parts})
        if not panels:
            return None
        chosen = newest_first(panels)[0]
        return {**chosen["parts"], "date": day(chosen["when"])}
    readings = [r for r in numeric_readings(corpus, mrn, p["loinc"], p.get("unit")) if in_window(r["when"], start)]
    if not readings:
        return None
    chosen = newest_first(readings)[0]
    if shape == "value":
        return {"value": round(chosen["value"], 4), "unit": chosen["unit"]}
    return value_date(chosen)


COMPARE = {
    "gt": lambda a, b: a > b, "ge": lambda a, b: a >= b,
    "lt": lambda a, b: a < b, "le": lambda a, b: a <= b,
}


def observation_window_stats(corpus, mrn, p):
    start = window_start(p)
    readings = [r for r in numeric_readings(corpus, mrn, p["loinc"], p.get("unit")) if in_window(r["when"], start)]
    if "comparator" in p:
        readings = [r for r in readings if COMPARE[p["comparator"]](r["value"], float(p["threshold"]))]
    statistic = p["statistic"]
    if statistic == "count":
        return len(readings)
    if not readings:
        return None
    values = [r["value"] for r in readings]
    if statistic == "mean":
        return round_half_away(sum(values) / len(values), 2)
    if statistic == "max":
        return round(max(values), 4)
    if statistic == "min":
        return round(min(values), 4)
    if statistic in ("change", "percent_change"):
        if len(readings) < 2:
            return None
        first, last = oldest_first(readings)[0]["value"], newest_first(readings)[0]["value"]
        change = last - first if statistic == "change" else (last - first) / first * 100
        return round_half_away(change, 2)
    raise ValueError(statistic)


# ---------------------------------------------------------------- medications

def med_codes(corpus: Corpus, request: dict) -> list[str]:
    if "medicationCodeableConcept" in request:
        return codes_of(request["medicationCodeableConcept"], RXNORM)
    target = corpus.resolve((request.get("medicationReference") or {}).get("reference", ""))
    return codes_of((target or {}).get("code"), RXNORM)


def orders(corpus, mrn, rxnorm=None, start=None, active_only=False) -> list[dict]:
    wanted = set(rxnorm) if rxnorm else None
    found = []
    for request in corpus.of(mrn, "MedicationRequest"):
        if request.get("status") in EXCLUDED_MED_STATUS:
            continue
        if active_only and request.get("status") != "active":
            continue
        moment = when(request.get("authoredOn"))
        codes = med_codes(corpus, request)
        if moment is None or not in_window(moment, start) or not codes:
            continue
        if wanted is not None and not (set(codes) & wanted):
            continue
        found.append({"id": request["id"], "when": moment, "code": codes[0], "resource": request})
    return oldest_first(found)


def ever_on_medication(corpus, mrn, p):
    found = orders(corpus, mrn, p["rxnorm"], window_start(p))
    shape = p.get("return", "ever_first")
    if shape == "ever_first":
        return {"ever": bool(found), "first": day(found[0]["when"]) if found else None}
    if shape == "first_date":
        return day(found[0]["when"]) if found else None
    if shape == "latest_date":
        return day(newest_first(found)[0]["when"]) if found else None
    if shape == "order_count":
        return len(found)
    raise ValueError(shape)


def medications_started_within(corpus, mrn, p):
    start = window_start(p)
    first_by_code = {}
    for order in orders(corpus, mrn):
        first_by_code.setdefault(order["code"], order["when"])
    return sorted(code for code, first in first_by_code.items() if first > start)


def active_medications(corpus, mrn, p):
    found = orders(corpus, mrn, p.get("rxnorm"), active_only=True)
    return [{"system": RXNORM, "code": c} for c in sorted({o["code"] for o in found})]


def medication_reason(corpus, mrn, p):
    for order in newest_first(orders(corpus, mrn, p["rxnorm"])):
        for reference in order["resource"].get("reasonReference", []):
            target = corpus.resolve(reference.get("reference", ""))
            if target and target.get("resourceType") == "Condition":
                return first_coding(target.get("code"))
    return None


def observation_before_medication(corpus, mrn, p):
    found = orders(corpus, mrn, p["rxnorm"])
    if not found:
        return None
    anchor = found[0]["when"]
    readings = [r for r in numeric_readings(corpus, mrn, p["loinc"], p.get("unit")) if r["when"] <= anchor]
    return value_date(newest_first(readings)[0]) if readings else None


# ---------------------------------------------------------------- conditions

def condition_onset(condition: dict) -> datetime | None:
    return when(condition.get("onsetDateTime") or (condition.get("onsetPeriod") or {}).get("start"))


def condition_ok(condition: dict) -> bool:
    verification = codes_of(condition.get("verificationStatus"), "http://terminology.hl7.org/CodeSystem/condition-ver-status")
    return not (set(verification) & EXCLUDED_VERIFICATION)


def condition_active(condition: dict) -> bool:
    if not condition_ok(condition):
        return False
    clinical = codes_of(condition.get("clinicalStatus"), "http://terminology.hl7.org/CodeSystem/condition-clinical")
    if set(clinical) & INACTIVE_CONDITION:
        return False
    onset = condition_onset(condition)
    if onset is not None and onset > NOW:
        return False
    if condition.get("abatementBoolean") is True:
        return False
    abated = when(condition.get("abatementDateTime") or (condition.get("abatementPeriod") or {}).get("end"))
    return not (abated is not None and abated <= NOW)


def conditions(corpus, mrn, snomed, start=None) -> list[dict]:
    wanted = set(snomed)
    found = []
    for condition in corpus.of(mrn, "Condition"):
        onset = condition_onset(condition)
        codes = set(codes_of(condition.get("code"), SNOMED)) & wanted
        if codes and condition_ok(condition) and onset is not None and in_window(onset, start):
            found.append({"id": condition["id"], "when": onset, "code": sorted(codes)[0], "resource": condition})
    return oldest_first(found)


def has_active_condition(corpus, mrn, snomed) -> bool:
    wanted = set(snomed)
    return any(set(codes_of(c.get("code"), SNOMED)) & wanted and condition_active(c) for c in corpus.of(mrn, "Condition"))


def condition_status(corpus, mrn, p):
    shape = p["return"]
    if shape == "active_count":
        excluded = set(p.get("exclude_snomed", []))
        return len({code for c in corpus.of(mrn, "Condition") if condition_active(c)
                    for code in codes_of(c.get("code"), SNOMED)[:1] if code not in excluded})
    found = conditions(corpus, mrn, p["snomed"], window_start(p))
    if shape == "count":
        return len(found)
    if not found:
        return None
    if shape == "first_onset":
        return day(found[0]["when"])
    if shape == "latest_onset":
        return day(newest_first(found)[0]["when"])
    if shape == "age_at_first_onset":
        return full_years(corpus.patients[mrn]["birthDate"], found[0]["when"])
    raise ValueError(shape)


def condition_crosswalk(corpus, mrn, p):
    if has_active_condition(corpus, mrn, p["source_snomed"]):
        return {"system": p["target_system"], "code": p["target_code"]}
    return None


# ---------------------------------------------------------------- immunizations, procedures, encounters, allergies

def dated_events(items: list[dict], start) -> list[dict]:
    return [i for i in items if in_window(i["when"], start)]


def event_answer(events: list[dict], shape: str):
    if shape == "count":
        return len(events)
    if shape == "ever":
        return bool(events)
    if not events:
        return None
    if shape == "latest_date":
        return day(newest_first(events)[0]["when"])
    if shape == "first_date":
        return day(oldest_first(events)[0]["when"])
    if shape == "days_since_latest":
        return (NOW.date() - newest_first(events)[0]["when"].date()).days
    raise ValueError(shape)


def immunization_history(corpus, mrn, p):
    wanted = set(p["cvx"])
    events = []
    for shot in corpus.of(mrn, "Immunization"):
        moment = when(shot.get("occurrenceDateTime"))
        if shot.get("status") == "completed" and moment and set(codes_of(shot.get("vaccineCode"), CVX)) & wanted:
            events.append({"id": shot["id"], "when": moment})
    return event_answer(dated_events(events, window_start(p)), p["return"])


def procedure_history(corpus, mrn, p):
    wanted = set(p["snomed"])
    events = []
    for proc in corpus.of(mrn, "Procedure"):
        moment = when(proc.get("performedDateTime") or (proc.get("performedPeriod") or {}).get("start"))
        if proc.get("status") == "completed" and moment and set(codes_of(proc.get("code"), SNOMED)) & wanted:
            events.append({"id": proc["id"], "when": moment})
    return event_answer(dated_events(events, window_start(p)), p["return"])


def encounters(corpus, mrn, p) -> list[dict]:
    classes = set(as_list(p["class"])) if "class" in p else None
    types = set(p["type_snomed"]) if "type_snomed" in p else None
    start = window_start(p)
    found = []
    for enc in corpus.of(mrn, "Encounter"):
        if enc.get("status") in ("cancelled", "entered-in-error"):
            continue
        moment = when((enc.get("period") or {}).get("start"))
        if moment is None or not in_window(moment, start):
            continue
        if classes is not None and (enc.get("class") or {}).get("code") not in classes:
            continue
        if types is not None and not any(set(codes_of(t, SNOMED)) & types for t in enc.get("type", [])):
            continue
        found.append({"id": enc["id"], "when": moment, "resource": enc})
    return found


def encounter_query(corpus, mrn, p):
    found = encounters(corpus, mrn, p)
    shape = p["return"]
    if shape in ("count", "latest_date"):
        return event_answer(found, shape)
    raise ValueError(shape)


def allergy_check(corpus, mrn, p):
    wanted = {tuple(s.split("|", 1)) for s in p.get("substances", [])}
    found = set()
    for allergy in corpus.of(mrn, "AllergyIntolerance"):
        clinical = codes_of(allergy.get("clinicalStatus"), "http://terminology.hl7.org/CodeSystem/allergyintolerance-clinical")
        verification = codes_of(allergy.get("verificationStatus"), "http://terminology.hl7.org/CodeSystem/allergyintolerance-verification")
        if (clinical and "active" not in clinical) or set(verification) & EXCLUDED_VERIFICATION:
            continue
        if "category" in p and p["category"] not in allergy.get("category", []):
            continue
        for coding in allergy.get("code", {}).get("coding", []):
            pair = (coding.get("system"), coding.get("code"))
            if not wanted or pair in wanted:
                found.add(pair)
    shape = p["return"]
    if shape == "any":
        return bool(found)
    if shape == "codes":
        return [{"system": s, "code": c} for s, c in sorted(found)]
    raise ValueError(shape)


# ---------------------------------------------------------------- calculations

CREATININE = ["38483-4", "2160-0"]
KDIGO_SNOMED = [(90, "431855005"), (60, "431856006"), (45, "700378005"), (30, "700379002"), (15, "431857002"), (0, "433146000")]


def latest_reading(corpus, mrn, loincs, unit=None):
    readings = numeric_readings(corpus, mrn, loincs, unit)
    return newest_first(readings)[0] if readings else None


def ckd_epi_2021(scr: float, age: int, female: bool) -> float:
    kappa, alpha = (0.7, -0.241) if female else (0.9, -0.302)
    egfr = 142 * min(scr / kappa, 1) ** alpha * max(scr / kappa, 1) ** -1.200 * 0.9938 ** age
    return egfr * 1.012 if female else egfr


def bp_readings(corpus, mrn) -> list[dict]:
    panels = []
    for obs in corpus.of(mrn, "Observation"):
        moment = observation_instant(obs)
        if obs.get("status") not in VALID_OBS_STATUS or moment is None or moment > NOW or "85354-9" not in codes_of(obs.get("code"), LOINC):
            continue
        parts = {}
        for comp in obs.get("component", []):
            quantity = comp.get("valueQuantity") or {}
            for code, name in (("8480-6", "sys"), ("8462-4", "dia")):
                if code in codes_of(comp.get("code"), LOINC) and isinstance(quantity.get("value"), (int, float)):
                    parts[name] = float(quantity["value"])
        if len(parts) == 2:
            panels.append({"id": obs["id"], "when": moment, **parts})
    return newest_first(panels)


def ever_condition(corpus, mrn, snomed) -> bool:
    return bool(conditions(corpus, mrn, snomed))


def clinical_calculation(corpus, mrn, p):
    patient = corpus.patients[mrn]
    female = patient.get("gender") == "female"
    formula = p["formula"]
    if formula == "bmi":
        weight = latest_reading(corpus, mrn, "29463-7", "kg")
        height = latest_reading(corpus, mrn, "8302-2", "cm")
        if not weight or not height:
            return None
        return round_half_away(weight["value"] / (height["value"] / 100) ** 2, 1)
    if formula in ("ckd-epi-2021", "kdigo-g-stage"):
        if formula == "kdigo-g-stage" and p.get("source", "ckd-epi-2021") == "observed":
            reading = latest_reading(corpus, mrn, "33914-3")
            if not reading:
                return None
            egfr = reading["value"]
        else:
            scr = latest_reading(corpus, mrn, CREATININE, "mg/dL")
            if not scr:
                return None
            egfr = ckd_epi_2021(scr["value"], full_years(patient["birthDate"], scr["when"]), female)
        if formula == "ckd-epi-2021":
            return round_half_away(egfr, 1)
        for floor, code in KDIGO_SNOMED:
            if egfr >= floor:
                return {"system": SNOMED, "code": code}
    if formula == "cockcroft-gault":
        scr = latest_reading(corpus, mrn, CREATININE, "mg/dL")
        weight = latest_reading(corpus, mrn, "29463-7", "kg")
        if not scr or not weight:
            return None
        age = full_years(patient["birthDate"], NOW)
        crcl = (140 - age) * weight["value"] / (72 * scr["value"]) * (0.85 if female else 1)
        return round_half_away(crcl, 1)
    if formula == "acc-aha-2017-bp":
        start = window_start(p)
        readings = [r for r in bp_readings(corpus, mrn) if in_window(r["when"], start)][: int(p.get("readings", 1))]
        if len(readings) < int(p.get("readings", 1)):
            return None
        sys_mean = sum(r["sys"] for r in readings) / len(readings)
        dia_mean = sum(r["dia"] for r in readings) / len(readings)
        return {"systolic": round_half_away(sys_mean, 1), "diastolic": round_half_away(dia_mean, 1),
                "stage1_or_higher": sys_mean >= 130 or dia_mean >= 80}
    if formula == "cha2ds2-vasc":
        age = full_years(patient["birthDate"], NOW)
        score = 0
        score += 1 if has_active_condition(corpus, mrn, p["chf"]) else 0
        score += 1 if has_active_condition(corpus, mrn, p["hypertension"]) else 0
        score += 2 if age >= 75 else (1 if age >= 65 else 0)
        score += 1 if has_active_condition(corpus, mrn, p["diabetes"]) else 0
        score += 2 if ever_condition(corpus, mrn, p["stroke_tia"]) else 0
        score += 1 if ever_condition(corpus, mrn, p["vascular"]) else 0
        score += 1 if female else 0
        return score
    if formula == "lab-gap":
        reading = latest_reading(corpus, mrn, p["loinc"], p.get("unit"))
        return round_half_away(reading["value"] - float(p["target"]), 2) if reading else None
    raise ValueError(formula)


# ---------------------------------------------------------------- cohorts

def latest_in_window(corpus, mrn, lab) -> dict | None:
    start = NOW - timedelta(days=int(lab["window_days"])) if "window_days" in lab else None
    readings = [r for r in numeric_readings(corpus, mrn, lab["loinc"], lab.get("unit")) if in_window(r["when"], start)]
    return newest_first(readings)[0] if readings else None


def age_ok(corpus, mrn, p) -> bool:
    age = full_years(corpus.patients[mrn]["birthDate"], NOW)
    return int(p.get("age_min", 0)) <= age <= int(p.get("age_max", 200))


def cohort_lab_above_or_missing(corpus, _mrn, p):
    count = 0
    comparator = COMPARE[p.get("comparator", "gt")]
    for mrn in corpus.living():
        if not age_ok(corpus, mrn, p) or not has_active_condition(corpus, mrn, p["snomed"]):
            continue
        latest = latest_in_window(corpus, mrn, p)
        if latest is None:
            count += 1 if p.get("missing", "count") == "count" else 0
        elif comparator(latest["value"], float(p["threshold"])):
            count += 1
    return count


def cohort_count(corpus, _mrn, p):
    members = []
    for mrn in corpus.living():
        if not age_ok(corpus, mrn, p):
            continue
        if "conditions_any" in p and not has_active_condition(corpus, mrn, p["conditions_any"]):
            continue
        if "medications_active_any" in p and not orders(corpus, mrn, p["medications_active_any"], active_only=True):
            continue
        if "immunization_any" in p:
            rule = p["immunization_any"]
            if not immunization_history(corpus, mrn, {**rule, "return": "ever"}):
                continue
        if "lab" in p:
            lab = p["lab"]
            latest = latest_in_window(corpus, mrn, lab)
            if latest is None:
                if lab.get("missing", "exclude") != "count":
                    continue
            elif not COMPARE[lab["comparator"]](latest["value"], float(lab["threshold"])):
                continue
        members.append(mrn)
    return len(members)


# ---------------------------------------------------------------- reference paths

class References:
    """Resolves the four reference forms of the oracle spec against the whole corpus."""

    def __init__(self, corpus: Corpus):
        self.corpus = corpus
        self.by_type_id: dict[tuple[str, str], dict] = {}
        self.by_identifier: dict[tuple[str, str, str], list[dict]] = {}
        for resource in corpus.full_urls.values():
            kind = resource["resourceType"]
            self.by_type_id[(kind, resource.get("id", ""))] = resource
            identifiers = resource.get("identifier")
            for identifier in identifiers if isinstance(identifiers, list) else []:
                if "system" in identifier and "value" in identifier:
                    key = (kind, identifier["system"], identifier["value"])
                    self.by_identifier.setdefault(key, []).append(resource)

    def resolve(self, container: dict, reference: str | None) -> dict | None:
        if not reference:
            return None
        if reference.startswith("#"):
            return next((c for c in container.get("contained", []) if c.get("id") == reference[1:]), None)
        if reference in self.corpus.full_urls:
            return self.corpus.full_urls[reference]
        conditional = re.fullmatch(r"([A-Za-z]+)\?identifier=([^|]+)\|(.+)", reference)
        if conditional:
            matches = self.by_identifier.get(conditional.groups(), [])
            if len(matches) > 1:
                raise ValueError(f"conditional reference {reference} matches {len(matches)} resources")
            return matches[0] if matches else None
        literal = re.fullmatch(r"([A-Za-z]+)/([A-Za-z0-9\-.]{1,64})", reference)
        if literal:
            return self.by_type_id.get(literal.groups())
        return None


def references_at(resource: dict, element_path: str) -> list[dict]:
    """Reference objects at a dot path of plain element names, arrays flattened in document order."""
    nodes = [resource]
    for name in element_path.split("."):
        next_nodes = []
        for node in nodes:
            value = node.get(name) if isinstance(node, dict) else None
            next_nodes.extend(value if isinstance(value, list) else [value] if value is not None else [])
        nodes = next_nodes
    return [node for node in nodes if isinstance(node, dict) and "reference" in node]


# ---------------------------------------------------------------- per-type selection rules

def _encounter_instant(r):
    return when((r.get("period") or {}).get("start"))


def _report_instant(r):
    return when(r.get("effectiveDateTime") or (r.get("effectivePeriod") or {}).get("start") or r.get("issued"))


def _procedure_instant(r):
    return when(r.get("performedDateTime") or (r.get("performedPeriod") or {}).get("start"))


RULES = {
    # type: (valid(resource), instant(resource), {criterion: codes(corpus, resource)})
    "MedicationRequest": (
        lambda r: r.get("status") not in EXCLUDED_MED_STATUS,
        lambda r: when(r.get("authoredOn")),
        {"rxnorm": lambda c, r: med_codes(c, r)},
    ),
    "Encounter": (
        lambda r: r.get("status") not in ("cancelled", "entered-in-error"),
        _encounter_instant,
        {"class": lambda c, r: [(r.get("class") or {}).get("code")],
         "type_snomed": lambda c, r: [code for t in r.get("type", []) for code in codes_of(t, SNOMED)]},
    ),
    "DiagnosticReport": (
        lambda r: r.get("status") in VALID_OBS_STATUS,
        _report_instant,
        {"loinc": lambda c, r: codes_of(r.get("code"), LOINC)},
    ),
    "Observation": (
        lambda r: r.get("status") in VALID_OBS_STATUS,
        observation_instant,
        {"loinc": lambda c, r: codes_of(r.get("code"), LOINC)},
    ),
    "Condition": (
        condition_ok,
        condition_onset,
        {"snomed": lambda c, r: codes_of(r.get("code"), SNOMED)},
    ),
    "Procedure": (
        lambda r: r.get("status") == "completed",
        _procedure_instant,
        {"snomed": lambda c, r: codes_of(r.get("code"), SNOMED)},
    ),
    "Immunization": (
        lambda r: r.get("status") == "completed",
        lambda r: when(r.get("occurrenceDateTime")),
        {"cvx": lambda c, r: codes_of(r.get("vaccineCode"), CVX)},
    ),
}
SELECTOR_KEYS = {"type", "pick", "window_days", "ref", "rev"}


def qualifies(corpus, resource: dict, criteria: dict, start=None) -> dict | None:
    """Apply the type's validity rule, instant <= now (and window), and every code criterion.
    Returns {id, when, resource} or None. Types without rules (Practitioner, Organization, ...) always qualify,
    and then may not carry code criteria."""
    kind = resource["resourceType"]
    filters = {k: v for k, v in criteria.items() if k not in SELECTOR_KEYS}
    if kind not in RULES:
        if filters:
            raise ValueError(f"code criteria {sorted(filters)} are not defined for {kind}")
        return {"id": resource.get("id", ""), "when": None, "resource": resource}
    valid, instant, criteria_codes = RULES[kind]
    moment = instant(resource)
    if not valid(resource) or moment is None or not in_window(moment, start):
        return None
    for name, wanted in filters.items():
        if name not in criteria_codes:
            raise ValueError(f"criterion {name} is not defined for {kind}")
        if not set(criteria_codes[name](corpus, resource)) & set(as_list(wanted)):
            return None
    return {"id": resource.get("id", ""), "when": moment, "resource": resource}


def pick(items: list[dict], which: str) -> dict | None:
    if not items:
        return None
    if which == "latest":
        return newest_first(items)[0]
    if which == "earliest":
        return oldest_first(items)[0]
    raise ValueError(f"pick must be latest or earliest, not {which}")


# ---------------------------------------------------------------- projections

def human_name(resource: dict) -> str | None:
    names = resource.get("name")
    if isinstance(names, str):
        return names
    if not names:
        return None
    chosen = next((n for n in names if n.get("use") == "official"), names[0])
    parts = list(chosen.get("given", [])) + ([chosen["family"]] if chosen.get("family") else [])
    return " ".join(parts) if parts else chosen.get("text")


def project(resource: dict, projection):
    if isinstance(projection, dict):
        return {field: project(resource, inner) for field, inner in projection.items()}
    if projection == "name":
        return human_name(resource)
    if projection == "value":
        quantity = resource.get("valueQuantity") or {}
        if not isinstance(quantity.get("value"), (int, float)):
            return None
        unit = quantity.get("code") or quantity.get("unit")
        return {"value": quantity["value"], "unit": unit} if unit else None
    if projection == "date":
        kind = resource["resourceType"]
        moment = RULES[kind][1](resource) if kind in RULES else None
        return day(moment) if moment else None
    if projection.startswith("address."):
        address = resource.get("address")
        address = address[0] if isinstance(address, list) and address else address
        return (address or {}).get(projection.split(".", 1)[1])
    if projection.startswith("identifier:"):
        system = projection.split(":", 1)[1]
        found = next((i for i in resource.get("identifier", []) if i.get("system") == system), None)
        return {"system": system, "value": found["value"]} if found else None
    if projection.startswith("code:"):
        system = projection.split(":", 1)[1]
        codes = codes_of(resource.get("code") or resource.get("vaccineCode"), system)
        return {"system": system, "code": codes[0]} if codes else None
    raise ValueError(f"unknown projection {projection}")


# ---------------------------------------------------------------- the oracle

_REFERENCES: dict[int, References] = {}


def reference_path(corpus, mrn, p):
    refs = _REFERENCES.setdefault(id(corpus), References(corpus))
    selector = p["start"]
    window = window_start(selector)
    candidates = [hit for r in corpus.of(mrn, selector["type"]) if (hit := qualifies(corpus, r, selector, window))]
    current = pick(candidates, selector["pick"])
    if current is None:
        return None
    current = current["resource"]
    for step in p.get("path", []):
        if "ref" in step:
            current = forward(corpus, refs, current, step)
        elif "rev" in step:
            current = reverse(corpus, refs, mrn, current, step)
        else:
            raise ValueError(f"path step needs ref or rev: {step}")
        if current is None:
            return None
    answer = project(current, p["project"])
    if isinstance(answer, dict) and isinstance(p["project"], dict) and any(v is None for v in answer.values()):
        return None
    return answer


def forward(corpus, refs, current, step) -> dict | None:
    for reference in references_at(current, step["ref"]):
        target = refs.resolve(current, reference.get("reference"))
        if target is None or ("type" in step and target["resourceType"] != step["type"]):
            continue
        if qualifies(corpus, target, step):
            return target
    return None


def reverse(corpus, refs, mrn, current, step) -> dict | None:
    kind, element = step["rev"].split(".", 1)
    found = []
    for candidate in corpus.of(mrn, kind):
        if not any(refs.resolve(candidate, r.get("reference")) is current for r in references_at(candidate, element)):
            continue
        if hit := qualifies(corpus, candidate, step):
            found.append(hit)
    chosen = pick(found, step["pick"])
    return chosen["resource"] if chosen else None


# ---------------------------------------------------------------- name answers (grader reference)

TITLES = {"dr", "doctor", "mr", "mrs", "ms", "miss", "mx", "prof", "professor"}
SUFFIXES = {"md", "do", "phd", "np", "pa", "rn", "dds", "dmd", "jr", "sr", "ii", "iii", "iv"}
LEGAL_FORMS = {"inc", "incorporated", "llc", "llp", "ltd", "corp", "corporation", "co", "pc", "pllc", "lp"}


def _fold(text: str) -> str:
    decomposed = unicodedata.normalize("NFKD", text)
    stripped = "".join(ch for ch in decomposed if not unicodedata.combining(ch))
    return stripped.casefold().replace("’", "'")


def person_tokens(text: str) -> list[str]:
    parts = [re.sub(r"[^\w\s'\-]", " ", part).split() for part in _fold(text).split(",")]
    while len(parts) >= 2 and all(t in SUFFIXES for t in parts[-1]):
        parts.pop()
    if len(parts) == 2:
        parts = [parts[1], parts[0]]
    tokens = [t for part in parts for t in part]
    while tokens and tokens[0] in TITLES:
        tokens.pop(0)
    while tokens and tokens[-1] in SUFFIXES:
        tokens.pop()
    tokens = [re.sub(r"\d+$", "", t) for t in tokens]
    return [t for t in tokens if t]


def text_tokens(text: str) -> list[str]:
    folded = _fold(text).replace("&", " and ").replace("+", " and ")
    folded = re.sub(r"['\"]", "", folded)
    tokens = re.sub(r"[^\w\s]", " ", folded).split()
    if tokens and tokens[0] == "the":
        tokens.pop(0)
    while tokens and tokens[-1] in LEGAL_FORMS:
        tokens.pop()
    return tokens


def names_match(kind: str, gold: str, agent: str) -> bool:
    tokens = person_tokens if kind == "person" else text_tokens
    expected = tokens(gold)
    return bool(expected) and tokens(agent) == expected


def unanswerable(_corpus, _mrn, _p):
    return INSUFFICIENT


ORACLES = {
    "latest-observation": latest_observation,
    "observation-window-stats": observation_window_stats,
    "ever-on-medication": ever_on_medication,
    "medications-started-within": medications_started_within,
    "cohort-lab-above-or-missing": cohort_lab_above_or_missing,
    "unanswerable": unanswerable,
    "condition-status": condition_status,
    "condition-crosswalk": condition_crosswalk,
    "active-medications": active_medications,
    "medication-reason": medication_reason,
    "observation-before-medication": observation_before_medication,
    "immunization-history": immunization_history,
    "procedure-history": procedure_history,
    "encounter-query": encounter_query,
    "reference-path": reference_path,
    "allergy-check": allergy_check,
    "clinical-calculation": clinical_calculation,
    "cohort-count": cohort_count,
}


# ---------------------------------------------------------------- question files

def read_question_set(path: str) -> list[str]:
    with open(path, encoding="utf-8") as handle:
        lines = [line.strip() for line in handle]
    return [line for line in lines if line and not line.startswith("#")]


def parse_flow(text: str):
    """Parse the YAML flow subset used on `oracle:` lines: {k: v}, [a, b], quoted or bare scalars."""
    pos = 0

    def skip():
        nonlocal pos
        while pos < len(text) and text[pos] in " \t":
            pos += 1

    def scalar():
        nonlocal pos
        skip()
        if text[pos] in "\"'":
            quote = text[pos]
            end = text.index(quote, pos + 1)
            value = text[pos + 1:end]
            pos = end + 1
            return value
        start = pos
        while pos < len(text) and text[pos] not in ",}]":
            pos += 1
        raw = text[start:pos].strip()
        return {"true": True, "false": False, "null": None}.get(raw, raw)

    def value():
        nonlocal pos
        skip()
        if text[pos] == "{":
            pos += 1
            result = {}
            while True:
                skip()
                if text[pos] == "}":
                    pos += 1
                    return result
                colon = text.index(":", pos)
                key = text[pos:colon].strip().strip("\"'")
                pos = colon + 1
                result[key] = value()
                skip()
                if text[pos] == ",":
                    pos += 1
        if text[pos] == "[":
            pos += 1
            result = []
            while True:
                skip()
                if text[pos] == "]":
                    pos += 1
                    return result
                result.append(value())
                skip()
                if text[pos] == ",":
                    pos += 1
        return scalar()

    return value()


def parse_block(lines: list[str]) -> dict:
    """Parse an indented block of `key: value` / `key:` lines; leaf values are YAML flow or scalars."""
    result: dict = {}
    index = 0
    while index < len(lines):
        indent = len(lines[index]) - len(lines[index].lstrip())
        key, _, rest = lines[index].strip().partition(":")
        index += 1
        if rest.strip():
            result[key] = parse_flow(rest.strip())
            continue
        start = index
        while index < len(lines) and len(lines[index]) - len(lines[index].lstrip()) > indent:
            index += 1
        result[key] = parse_block(lines[start:index])
    return result


def read_question(questions_dir: str, question_id: str) -> dict:
    """Read the id, patient mrn, oracle and answer spec from a question file (the YAML subset the repo uses)."""
    with open(os.path.join(questions_dir, question_id + ".yaml"), encoding="utf-8") as handle:
        lines = [line.rstrip() for line in handle if line.strip() and not line.lstrip().startswith("#")]
    sections: dict[str, list[str]] = {}
    current = None
    for line in lines:
        if not line[0].isspace():
            current = line.partition(":")[0]
            sections[current] = [line]
        elif current:
            sections[current].append(line)
    document = parse_block([line for key in ("patient", "answer", "oracle") for line in sections.get(key, [])])
    return {
        "id": question_id,
        "mrn": document.get("patient", {}).get("mrn"),
        "oracle": document["oracle"],
        "answer": document["answer"],
    }


def evaluate(corpus: Corpus, question: dict):
    oracle = question["oracle"]
    return ORACLES[oracle["name"]](corpus, question["mrn"], oracle.get("parameters") or {})


# ---------------------------------------------------------------- comparison with the C# golds

DEFAULT_TOLERANCE = 1e-6


def read_csharp_golds(path: str) -> dict:
    """Parse `admit --show` output: a `OK|NEW|FAIL <id>` line followed by an indented JSON gold line."""
    golds = {}
    current = None
    with open(path, encoding="utf-8") as handle:
        for line in handle:
            if line.startswith(("OK   ", "NEW  ", "FAIL ")):
                current = line.split(None, 1)[1].strip()
            elif current and line.strip().startswith("{"):
                envelope = json.loads(line)
                golds[current] = envelope["answer"] if envelope["status"] == "answered" else envelope["status"]
                current = None
    return golds


def comparable(value):
    """Treat {system, code} objects and bare code strings as the same thing."""
    if isinstance(value, dict) and set(value) == {"system", "code"}:
        return value["code"]
    return value


def same(left, right, spec: dict) -> bool:
    left, right = comparable(left), comparable(right)
    kind = spec.get("type")
    if kind == "name" and isinstance(left, str) and isinstance(right, str):
        return names_match(spec["kind"], left, right)
    if isinstance(left, bool) or isinstance(right, bool):
        return left is right
    if isinstance(left, (int, float)) and isinstance(right, (int, float)):
        return abs(left - right) <= float(spec.get("tolerance", DEFAULT_TOLERANCE)) + 1e-12
    if kind == "quantity" and isinstance(left, dict) and isinstance(right, dict):
        return left.get("unit") == right.get("unit") and same(left.get("value"), right.get("value"), spec)
    if isinstance(left, dict) and isinstance(right, dict):
        fields = spec.get("fields", {})
        return left.keys() == right.keys() and all(same(left[k], right[k], fields.get(k, {})) for k in left)
    if isinstance(left, list) and isinstance(right, list):
        if len(left) != len(right):
            return False
        if kind == "set":
            key = lambda item: json.dumps(comparable(item), sort_keys=True)
            left, right = sorted(left, key=key), sorted(right, key=key)
        return all(same(a, b, spec.get("items", {})) for a, b in zip(left, right))
    return left == right


def compare(python_golds: dict, questions: dict, csharp_path: str) -> int:
    csharp = read_csharp_golds(csharp_path)
    matches, mismatches, missing = [], [], []
    for question_id in python_golds:
        if question_id not in csharp:
            missing.append(question_id)
        elif same(python_golds[question_id], csharp[question_id], questions[question_id]["answer"]):
            matches.append(question_id)
        else:
            mismatches.append(question_id)
    print(f"compared {len(python_golds)}: {len(matches)} match, {len(mismatches)} mismatch, {len(missing)} not in C# output")
    for question_id in mismatches:
        print(f"MISMATCH {question_id}")
    for question_id in missing:
        print(f"MISSING  {question_id}")
    return 1 if mismatches or missing else 0


def main() -> int:
    parser = argparse.ArgumentParser(description="Independent Python derivation of gold answers.")
    parser.add_argument("question_set", nargs="?", default="question-sets/v1.txt", help="question-set file, relative to the repo root")
    parser.add_argument("--corpus", default="corpora/synthea-sample-v1/data", help="folder of corpus bundles, relative to the repo root")
    parser.add_argument("--questions-dir", default="questions", help="folder of question YAML files, relative to the repo root")
    parser.add_argument("--out", help="write golds as JSON to this path (omit to print to stdout); never inside the repo")
    parser.add_argument("--compare", metavar="ADMIT_OUTPUT", help="file holding `admit --show` output from the C# CLI; compare instead of printing golds")
    args = parser.parse_args()

    def repo_path(path: str) -> str:
        return path if os.path.isabs(path) else os.path.join(REPO, path)

    corpus = Corpus(repo_path(args.corpus))
    ids = read_question_set(repo_path(args.question_set))
    questions = {i: read_question(repo_path(args.questions_dir), i) for i in ids}
    supported = {i: q for i, q in questions.items() if q["oracle"]["name"] in ORACLES}
    skipped = [i for i in questions if i not in supported]
    golds = {i: evaluate(corpus, q) for i, q in supported.items()}
    print(f"derived {len(golds)} golds, skipped {len(skipped)} (oracle not implemented){': ' + ', '.join(skipped) if skipped else ''}", file=sys.stderr)

    if args.compare:
        return compare(golds, supported, args.compare)
    text = json.dumps(golds, indent=2, sort_keys=True)
    if args.out:
        with open(args.out, "w", encoding="utf-8") as handle:
            handle.write(text)
    else:
        print(text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
