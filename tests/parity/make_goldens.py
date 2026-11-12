r"""Run the Python converter (the oracle) over HTML and save exactly what it produced, for the .NET tests.

    python tests/parity/make_goldens.py

Committed (synthetic only, nothing from a real post):
    tests/TeamsChannelNotes.Tests/Golden/convert.json   the corpus in corpus.py
    tests/TeamsChannelNotes.Tests/Golden/fuzz.json      300 seeded fuzz fragments, for CI

Local only, in %LOCALAPPDATA%\teams-channel-notes-port\goldens (never committed: real posts carry
confidential figures):
    history.json      every post body in the newest recorded fixtures, whatever its title
    fuzz-10000.json   10,000 seeded fuzz fragments

Each file is {"python": version, "cases": [{"name", "input", "bullets", "markdown"}]}, ASCII-escaped JSON.
"""

from __future__ import annotations

import json
import os
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src"))
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from corpus import CASES, fuzz  # noqa: E402
from teams_channel_notes.convert import to_bullets, to_markdown  # noqa: E402

LOCAL = pathlib.Path(os.environ["LOCALAPPDATA"]) / "teams-channel-notes-port"
VERSION = sys.version.split()[0]


def save(path: pathlib.Path, cases: list[tuple[str, str]]) -> None:
    out = {"python": VERSION, "cases": [
        {"name": name, "input": html, "bullets": to_bullets(html), "markdown": to_markdown(html)} for name, html in cases]}
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(out, indent=1, ensure_ascii=True), encoding="utf-8", newline="\n")
    print(f"{path}: {len(cases)} cases")


def history() -> list[tuple[str, str]]:
    fixtures = sorted((LOCAL / "fixtures").glob("*/history-*.json"))
    if not fixtures:
        return []
    latest = fixtures[-1]
    messages = json.loads(latest.read_text(encoding="utf-8"))["value"]
    return [(f"{m.get('id')}", ((m.get("body") or {}).get("content") or "")) for m in messages]


def main() -> int:
    golden = ROOT / "tests" / "TeamsChannelNotes.Tests" / "Golden"
    save(golden / "convert.json", CASES)
    save(golden / "fuzz.json", fuzz(300, 1))
    save(LOCAL / "goldens" / "fuzz-10000.json", fuzz(10000, 20261112))
    real = history()
    if real:
        save(LOCAL / "goldens" / "history.json", real)
    return 0


if __name__ == "__main__":
    sys.exit(main())
