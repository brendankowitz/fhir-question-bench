"""Trusted offline child-process fixture. Never calls a model or specialist tool."""
import json
import os
from pathlib import Path
import subprocess
import sys
import time


mode = Path(__file__).stem
if "--version" in sys.argv:
    print("offline-fixture-1")
    raise SystemExit(0)
if "mcp" in sys.argv:
    for line in sys.stdin:
        request = json.loads(line)
        if request.get("method") == "initialize":
            if mode == "badprotocol":
                print("not JSON", flush=True)
                continue
            print(json.dumps({"jsonrpc": "2.0", "id": request["id"], "result": {
                "protocolVersion": "2024-11-05", "capabilities": {"tools": {}},
                "serverInfo": {"name": "offline-fixture", "version": "1"},
            }}), flush=True)
        elif request.get("method") == "tools/list":
            tools = [] if mode == "badtools" else [{"name": "search"}, {"name": "read"}]
            print(json.dumps({"jsonrpc": "2.0", "id": request["id"],
                              "result": {"tools": tools}}), flush=True)
    raise SystemExit(0)

work = Path(sys.argv[sys.argv.index("-C") + 1])
usage = Path(sys.argv[sys.argv.index("--usage-output-file") + 1])
assert Path.cwd() == work
assert os.environ["COPILOT_HOME"] != str(Path.home() / ".copilot")
assert "RUNNER_TEST_SECRET" not in os.environ
assert "COPILOT_GITHUB_TOKEN" not in os.environ
assert "PYTHONPATH" not in os.environ
assert not list(Path(os.environ["COPILOT_HOME"]).glob("*gold*"))
(work / "observed.json").write_text(json.dumps({
    "argv": sys.argv[1:], "home": os.environ["COPILOT_HOME"],
    "environment_names": sorted(os.environ),
}), encoding="utf-8")
if mode == "timeout":
    time.sleep(30)
if mode == "descendant":
    child = subprocess.Popen([sys.executable, "-c",
        "import time; from pathlib import Path; "
        "exec(\"while not Path('release-child').exists(): time.sleep(0.05)\"); "
        "Path('escaped.txt').write_text('bad')"])
    (work / "child-pid.txt").write_text(str(child.pid))
    time.sleep(30)
if mode == "overflow":
    sys.stdout.write("x" * 100000)
    sys.stdout.flush()
    time.sleep(30)
if mode == "mutation":
    with (work / "corpus" / "resources.ndjson").open("ab") as stream:
        stream.write(b"\n")
if mode == "addedsource":
    (work / "corpus" / "extra.ndjson").write_text("{}")
response = json.loads((Path(__file__).parent / "fixture-response.json").read_text(encoding="utf-8"))
if "question_id" not in response:
    question_id = json.loads((work / "question.json").read_text(encoding="utf-8"))["id"]
    response = response[question_id]
if mode == "unsupported":
    response["evidence"][0]["source_value"] = "unsupported citation"
if mode == "injected":
    print(json.dumps(response))
elif mode == "malformed":
    (work / "answer.json").write_text("{bad")
elif mode == "oversize":
    (work / "answer.json").write_text(" " * 100000)
elif mode == "link":
    try:
        (work / "answer.json").hardlink_to(Path(__file__).parent / "fixture-response.json")
    except OSError:
        raise SystemExit(7)
elif mode == "symlink":
    (work / "answer.json").symlink_to(Path(__file__).parent / "fixture-response.json")
elif mode != "missing":
    (work / "answer.json").write_text(json.dumps(response), encoding="utf-8")
usage.write_text(json.dumps({"model": "offline-fixture", "input_tokens": 12,
                             "output_tokens": 4}), encoding="utf-8")
if mode == "badusage":
    usage.write_text("not JSON")
if mode == "sessionerror":
    print(json.dumps({"type": "session.error", "data": {"message": "fixture error"}}))
if mode != "noevents":
    print(json.dumps({"type": "tool.execution_start", "data": {"toolName": "shell"}}))
if mode not in {"unterminated", "noevents"}:
    print(json.dumps({"type": "session.idle", "data": {}}))
raise SystemExit(3 if mode == "nonzero" else 0)
