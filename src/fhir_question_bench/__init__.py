"""Offline FHIR question benchmark contracts and evaluation interfaces."""

from .contracts import AgentResponse, EvidenceRef, GoldAnswer, Grade, Question
from .corpus import Corpus, ResolvedResource, load_corpus
from .grading import grade_response
from .oracle import audit_dataset, derive_gold, load_questions, verify_dataset_manifest, verify_gold
from .prepare import prepare_question

__all__ = [
    "AgentResponse",
    "audit_dataset",
    "Corpus",
    "EvidenceRef",
    "GoldAnswer",
    "Grade",
    "Question",
    "ResolvedResource",
    "derive_gold",
    "grade_response",
    "load_corpus",
    "load_questions",
    "prepare_question",
    "verify_gold",
    "verify_dataset_manifest",
]
