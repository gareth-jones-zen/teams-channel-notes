r"""Sync scenarios, run through the Python sync() (the oracle) against a fake Graph, in a temp folder.

    python tests/parity/scenarios.py

Writes tests/TeamsChannelNotes.Tests/Golden/scenarios.json: each scenario's settings, starting files and state,
and for each step the messages Graph returns and the options, then what Python did: the URLs it asked for, the
log lines, the report, every file's bytes and the state file's bytes. Paths are written with the temp folder
replaced by {ROOT} (and {root} in lowercase, as the state keys have it). All messages are synthetic.
"""

from __future__ import annotations

import base64
import json
import pathlib
import shutil
import sys
import tempfile

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src"))

from teams_channel_notes import config as cfg, notes  # noqa: E402

TITLE = r"(?i)ExCo Notes\s*[-–]\s*(?P<date>\d{4}-\d{2}-\d{2})"

SETTINGS = f"""state_file = "state.json"

[[source]]
name = "exco"
team = "team-1"
channel = "19:chan-1@thread.tacv2"
title = '{TITLE}'
folder = "notes"
format = "bullets"
recent = 10
"""

TWO_SOURCES = SETTINGS + f"""
[[source]]
name = "weekly"
team = "team-2"
channel = "19:chan-2@thread.tacv2"
title = '(?i)Weekly\\s+(?P<year>\\d{{4}})-(?P<week>\\d{{2}})'
folder = "weekly/notes"
filename = "{{year}}-W{{week}}.md"
format = "markdown"
recent = 5
"""


def msg(n, subject, modified, body, kind="message", deleted=None):
    return {"id": str(n), "subject": subject, "messageType": kind, "deletedDateTime": deleted,
            "lastModifiedDateTime": modified, "body": {"contentType": "html", "content": body}}


A1 = "<p>Exco notes</p><ul><li>Consumer<ul><li>Orders up</li><li>R&amp;D &ndash; ok</li></ul></li></ul>"
A2 = "<p>Exco notes</p><ul><li>Consumer<ul><li>Orders up a lot</li><li>R&amp;D &ndash; ok</li></ul></li></ul>"
A1_SPACED = "<ul><li>Consumer<ul><li>Orders   up</li><li>R&amp;D &ndash; ok</li></ul></li></ul><p>sign-off</p>"
B1 = "<ul><li>Business \U0001F60A</li><li>Caf\u00e9</li></ul>"
A1_TEXT = "- Consumer\n  - Orders up\n  - R&D \u2013 ok\n"


def text(s: str) -> str:
    """A file's bytes as Python's write_text made them: "\\n" as "\\r\\n", UTF-8."""
    return base64.b64encode(s.replace("\n", "\r\n").encode("utf-8")).decode()


def raw(b: bytes) -> str:
    return base64.b64encode(b).decode()


SCENARIOS = [
    {"name": "new-post", "settings": SETTINGS, "steps": [
        {"messages": {"team-1": [msg(1, "ExCo Notes - 2026-10-09", "2026-10-12T08:22:23.336Z", A1)]}}]},
    {"name": "edited-text-changed", "settings": SETTINGS, "steps": [
        {"messages": {"team-1": [msg(1, "ExCo Notes - 2026-10-09", "2026-10-12T08:22:23.336Z", A1)]}},
        {"messages": {"team-1": [msg(1, "ExCo Notes - 2026-10-09", "2026-10-13T09:00:00.1Z", A2)]}}]},
    {"name": "edited-text-same", "settings": SETTINGS, "steps": [
        {"messages": {"team-1": [msg(1, "ExCo Notes - 2026-10-09", "2026-10-12T08:22:23.336Z", A1)]}},
        {"messages": {"team-1": [msg(1, "ExCo Notes - 2026-10-09", "2026-10-14T10:00:00Z", A1_SPACED)]}},
        {"messages": {"team-1": [msg(1, "ExCo Notes - 2026-10-09", "2026-10-14T10:00:00Z", A2)]}}]},
    {"name": "older-modification", "settings": SETTINGS, "steps": [
        {"messages": {"team-1": [msg(1, "ExCo Notes - 2026-10-09", "2026-10-12T08:22:23.336Z", A1)]}},
        {"messages": {"team-1": [msg(1, "ExCo Notes - 2026-10-09", "2026-10-12T08:22:23.3359999Z", A2)]}},
        {"messages": {"team-1": [msg(1, "ExCo Notes - 2026-10-09", "2026-10-12T09:22:23.336+01:00", A2)]}}]},
    {"name": "untracked-same", "settings": SETTINGS,
     "files": {"notes/2026-10-09.md": text("- Consumer\n  - Orders   up\n  - R&D \u2013 ok\n\n")},
     "steps": [{"messages": {"team-1": [msg(1, "ExCo Notes - 2026-10-09", "2026-10-12T08:22:23.336Z", A1)]}},
               {"messages": {"team-1": [msg(1, "ExCo Notes - 2026-10-09", "2026-10-15T08:00:00Z", A2)]}}]},
    {"name": "untracked-different", "settings": SETTINGS,
     "files": {"notes/2026-10-09.md": text("- Pasted by hand\n")},
     "steps": [{"messages": {"team-1": [msg(1, "ExCo Notes - 2026-10-09", "2026-10-12T08:22:23.336Z", A1)]}}]},
    {"name": "untracked-bom", "settings": SETTINGS,
     "files": {"notes/2026-10-09.md": raw(b"\xef\xbb\xbf" + A1_TEXT.replace("\n", "\r\n").encode("utf-8"))},
     "steps": [{"messages": {"team-1": [msg(1, "ExCo Notes - 2026-10-09", "2026-10-12T08:22:23.336Z", A1)]}}]},
    {"name": "untracked-crlf-and-lf", "settings": SETTINGS,
     "files": {"notes/2026-10-09.md": raw(A1_TEXT.encode("utf-8")),
               "notes/2026-10-02.md": raw(b"- Old\r- mac line ends\r")},
     "steps": [{"messages": {"team-1": [msg(1, "ExCo Notes - 2026-10-09", "2026-10-12T08:22:23.336Z", A1),
                                        msg(2, "ExCo Notes - 2026-10-02", "2026-10-05T08:00:00Z",
                                            "<ul><li>Old</li><li>mac line ends</li></ul>")]}}]},
    {"name": "adopt", "settings": SETTINGS,
     "files": {"notes/2026-10-09.md": text("- Pasted by hand\n"), "notes/2026-10-02.md": text(A1_TEXT)},
     "steps": [{"args": {"adopt": True},
                "messages": {"team-1": [msg(1, "ExCo Notes - 2026-10-09", "2026-10-12T08:22:23.336Z", A1),
                                        msg(2, "ExCo Notes - 2026-10-02", "2026-10-05T08:00:00Z", A1),
                                        msg(3, "ExCo Notes - 2026-09-25", "2026-09-26T08:00:00Z", A1)]}},
               {"args": {"adopt": True},
                "messages": {"team-1": [msg(1, "ExCo Notes - 2026-10-09", "2026-10-20T08:22:23.336Z", A2)]}},
               {"messages": {"team-1": [msg(1, "ExCo Notes - 2026-10-09", "2026-10-21T08:22:23.336Z", A2)]}}]},
    {"name": "dry-run", "settings": SETTINGS, "steps": [
        {"args": {"dry_run": True}, "messages": {"team-1": [msg(1, "ExCo Notes - 2026-10-09", "2026-10-12T08:22:23.336Z", A1)]}},
        {"messages": {"team-1": [msg(1, "ExCo Notes - 2026-10-09", "2026-10-12T08:22:23.336Z", A1)]}},
        {"args": {"dry_run": True}, "messages": {"team-1": [msg(1, "ExCo Notes - 2026-10-09", "2026-10-13T08:22:23.336Z", A2)]}}]},
    {"name": "two-sources-and-only", "settings": TWO_SOURCES, "steps": [
        {"messages": {"team-1": [msg(1, "ExCo Notes \u2013 2026-10-09", "2026-10-12T08:22:23.336Z", A1)],
                      "team-2": [msg(5, "Weekly 2026-41", "2026-10-10T08:00:00Z",
                                     "<h2>Week</h2><p>Hello <b>there</b><br>again</p><ol><li>a</li><li>b</li></ol>"),
                                 msg(6, "weekly   2026-42", "2026-10-17T08:00:00Z",
                                     "<p>See <a href=\"https://e.com/?a=1&amp;b=2\">this</a></p>")]}},
        {"args": {"only": "weekly"},
         "messages": {"team-1": [msg(1, "ExCo Notes - 2026-10-09", "2026-10-20T08:22:23.336Z", A2)],
                      "team-2": [msg(5, "Weekly 2026-41", "2026-10-21T08:00:00Z", "<p>changed</p>")]}},
        {"args": {"only": "nosuch"}, "messages": {"team-1": [], "team-2": []}}]},
    {"name": "skipped-messages", "settings": SETTINGS, "steps": [
        {"messages": {"team-1": [
            msg(1, "ExCo Notes - 2026-10-09", "2026-10-12T08:22:23.336Z", A1, deleted="2026-10-13T00:00:00Z"),
            msg(2, "ExCo Notes - 2026-10-02", "2026-10-05T08:00:00Z", A1, kind="systemEventMessage"),
            msg(3, "No ExCo meeting this week", "2026-10-05T08:00:00Z", A1),
            {"id": "4", "subject": None, "messageType": "message", "lastModifiedDateTime": "2026-10-05T08:00:00Z",
             "body": None},
            {"id": "5", "subject": "Re: ExCo Notes - 2026-09-25", "messageType": "message",
             "lastModifiedDateTime": "2026-10-05T08:00:00Z"},
            msg(6, "ExCo Notes - 2026-09-18", "2026-09-19T08:00:00Z", A1, deleted=""),
            msg(7, "EXCO NOTES-2026-09-11 extra", "2026-09-12T08:00:00Z", "")]}}]},
    {"name": "state-corrupt", "settings": SETTINGS, "state": raw(b"{not json"),
     "files": {"notes/2026-10-09.md": text(A1_TEXT)},
     "steps": [{"messages": {"team-1": [msg(1, "ExCo Notes - 2026-10-09", "2026-10-12T08:22:23.336Z", A1)]}}]},
    {"name": "state-bom", "settings": SETTINGS, "state": raw(b'\xef\xbb\xbf{"x": "y"}'),
     "steps": [{"messages": {"team-1": [msg(1, "ExCo Notes - 2026-10-09", "2026-10-12T08:22:23.336Z", A1)]}}]},
    {"name": "state-other-entries", "settings": SETTINGS,
     "state": raw(json.dumps({"z:\\b.md": "2026-01-01T00:00:00Z", "c:\\caf\u00e9 & 'x'+<y>.md": "t\u00e9",
                              "a:\\\U0001F600.md": "e", "a:\\\uE000.md": "p"}, ensure_ascii=False).encode("utf-8")),
     "steps": [{"messages": {"team-1": [msg(1, "ExCo Notes - 2026-10-09", "2026-10-12T08:22:23.336Z", A1)]}}]},
    {"name": "empty-history", "settings": SETTINGS, "steps": [{"messages": {"team-1": []}}]},
    {"name": "markdown-empty-body", "settings": SETTINGS.replace('format = "bullets"', 'format = "markdown"'), "steps": [
        {"messages": {"team-1": [msg(1, "ExCo Notes - 2026-10-09", "2026-10-12T08:22:23.336Z", "")]}}]},
]


class FakeGraph:
    def __init__(self, messages, urls):
        self.messages, self.urls = messages, urls

    def get(self, url, scopes=None):
        self.urls.append(url)
        team = url.split("/")[2]
        return {"value": self.messages.get(team, [])}


def snapshot(root: pathlib.Path, state: pathlib.Path) -> dict:
    files = {}
    for path in sorted(root.rglob("*")):
        if path.is_file() and path != state and path.name != "settings.toml":
            files[path.relative_to(root).as_posix()] = raw(path.read_bytes())
    return files


def run(scenario: dict) -> dict:
    tmp = pathlib.Path(tempfile.mkdtemp(prefix="tcn-scenario-"))
    try:
        root = tmp.resolve()
        (root / "settings.toml").write_text(scenario["settings"], encoding="utf-8")
        for rel, data in scenario.get("files", {}).items():
            path = root / rel
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(base64.b64decode(data))
        state = root / "state.json"
        if "state" in scenario:
            state.write_bytes(base64.b64decode(scenario["state"]))
        steps = []
        for step in scenario["steps"]:
            config = cfg.load(root / "settings.toml")
            urls, lines = [], []
            notes.client = lambda config, interactive=None, _m=step["messages"], _u=urls: FakeGraph(_m, _u)
            args = step.get("args", {})
            report = notes.sync(config, dry_run=args.get("dry_run", False), adopt=args.get("adopt", False),
                                only=args.get("only"), log=lines.append)
            steps.append({"urls": urls, "log": lines, "report": report,
                          "files": snapshot(root, state),
                          "state": raw(state.read_bytes()) if state.exists() else None})
        return {"root": str(root), "steps": steps}
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def normalise(value, root: str):
    """The temp folder replaced by {ROOT} / {root}, in plain text, in JSON-escaped text, and in base64 file bytes."""
    if isinstance(value, dict):
        return {k: normalise(v, root) for k, v in value.items()}
    if isinstance(value, list):
        return [normalise(v, root) for v in value]
    if isinstance(value, str):
        return value.replace(root, "{ROOT}").replace(root.lower(), "{root}")
    return value


def main() -> int:
    out = {"python": sys.version.split()[0], "scenarios": []}
    for scenario in SCENARIOS:
        result = run(scenario)
        root = result["root"]
        steps = []
        for step in result["steps"]:
            state = step["state"]
            if state is not None:
                text_state = base64.b64decode(state).decode("utf-8")
                escaped = json.dumps(root)[1:-1]
                text_state = text_state.replace(escaped, "{ROOT}").replace(escaped.lower(), "{root}")
                step["state"] = text_state
            step = normalise(step, root)
            steps.append(step)
        out["scenarios"].append({**scenario, "expected": steps})
        print(f"{scenario['name']}: {len(steps)} step(s)")
    path = ROOT / "tests" / "TeamsChannelNotes.Tests" / "Golden" / "scenarios.json"
    path.write_text(json.dumps(out, indent=1, ensure_ascii=True), encoding="utf-8", newline="\n")
    print(path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
