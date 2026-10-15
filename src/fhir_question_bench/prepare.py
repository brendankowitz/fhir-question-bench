from __future__ import annotations

import json
import shutil
import hashlib
from pathlib import Path
from typing import Any

from .contracts import Question
from .corpus import Corpus
from .corpus import load_corpus
from .oracle import load_questions, verify_dataset_manifest


def prepare_question(
    question: Question,
    corpus: Corpus,
    data_root: Path | str,
    output_dir: Path | str,
) -> Path:
    """Export only the selected question and its shared agent-visible assets."""
    root = Path(data_root)
    output = Path(output_dir)
    verify_dataset_manifest(root)
    corpus.assert_unchanged()
    locked_corpus = load_corpus(root / "agent" / "corpus-manifest.json")
    if locked_corpus.corpus_id != corpus.corpus_id or locked_corpus.corpus_hash != corpus.corpus_hash:
        raise ValueError("provided corpus does not match the locked preparation assets")
    registered = next((item for item in load_questions(root / "questions.jsonl") if item.id == question.id), None)
    if registered is None or registered.as_dict() != question.as_dict():
        raise ValueError("provided question is not the exact locked question record")
    if output.exists() and any(output.iterdir()):
        raise FileExistsError(f"refusing to overwrite non-empty preparation directory: {output}")
    output.mkdir(parents=True, exist_ok=True)
    manifest_path = root / "agent" / "corpus-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    for entry in manifest["files"]:
        source = root / "agent" / Path(*entry["path"].split("/"))
        target = output / Path(*entry["path"].split("/"))
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
    copied_manifest = output / "corpus-manifest.json"
    copied_manifest.write_text(
        json.dumps(manifest, sort_keys=True, indent=2) + "\n",
        encoding="utf-8",
    )
    protocol_path = root / "agent" / "protocols" / "protocols.json"
    protocol_target = output / "protocols" / "protocols.json"
    protocol_target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(protocol_path, protocol_target)
    public_asset_hashes = {}
    for name in ("response-contract.json", "selection-policies.json"):
        asset = root / "agent" / name
        shutil.copyfile(asset, output / name)
        public_asset_hashes[name] = hashlib.sha256(asset.read_bytes()).hexdigest()
    question_target = output / "question.json"
    question_target.write_text(
        json.dumps(question.as_dict(), sort_keys=True, indent=2) + "\n",
        encoding="utf-8",
    )
    metadata: dict[str, Any] = {
        "question_id": question.id,
        "corpus_id": corpus.corpus_id,
        "corpus_hash": corpus.corpus_hash,
        "protocol_id": question.protocol_id,
        "question_sha256": hashlib.sha256(
            json.dumps(question.as_dict(), sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()
        ).hexdigest(),
        "protocol_sha256": hashlib.sha256(protocol_path.read_bytes()).hexdigest(),
        "public_asset_sha256": public_asset_hashes,
        "export_kind": "agent-visible question package",
    }
    (output / "prepared-manifest.json").write_text(
        json.dumps(metadata, sort_keys=True, indent=2) + "\n",
        encoding="utf-8",
    )
    return output
