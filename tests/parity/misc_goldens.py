r"""Small goldens from the Python version: links, lock and state keys, title patterns, timestamps, the hook's
payload and the report's JSON.

    python tests/parity/misc_goldens.py      -> tests/TeamsChannelNotes.Tests/Golden/misc.json (all synthetic)
"""

from __future__ import annotations

import datetime as dt
import hashlib
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src"))

from win_background import key  # noqa: E402

from teams_channel_notes import notes  # noqa: E402

LINKS = [
    "https://teams.microsoft.com/l/channel/19%3aAbCdEf0123456789_xyz%40thread.tacv2/ExCo%20notes?groupId=11111111-2222-3333-4444-555555555555&tenantId=66666666-7777-8888-9999-000000000000",
    "  https://teams.microsoft.com/l/channel/19%3aabc%40thread.tacv2/Name?tenantId=t&groupId=g1&groupId=g2  ",
    "https://teams.microsoft.com/l/channel/19%3aabc%40thread.tacv2/Name;params?groupId=g",
    "https://teams.microsoft.com/l/channel/19%3aabc%40thread.tacv2/Name?groupId=",
    "https://teams.microsoft.com/l/channel/19%3a%E2%9C%93%ZZ%e2%40thread.tacv2/N?groupId=a+b%20c",
    "https://teams.microsoft.com/l/message/19%3aabc/123?groupId=g",
    "https://teams.microsoft.com/l/channel/19%3aabc%40thread.tacv2/Name#frag?groupId=g",
    "https://teams.microsoft.com/l/channel/19:abc@thread.tacv2/Name?groupId=g&groupId",
    "teams.microsoft.com/l/channel/x/y?groupId=g",
    "",
]

KEYS = [
    r"C:\Users\gareth.jones\OneDrive - Zen Internet\claude-executive-assistant\tools\teams-channel-notes.toml",
    r"C:\Users\Someone\AppData\Roaming\teams-channel-notes\config.toml",
    "C:\\Caf\u00e9\\Settings.TOML",
]

TITLES = [
    (r"(?i)ExCo Notes\s*[-–]\s*(?P<date>\d{4}-\d{2}-\d{2})",
     ["ExCo Notes - 2026-10-09", "exco notes–2026-10-09", "ExCo Notes\x1c-\x1f2026-10-09", "ExCo Notes - 2026-1-09",
      "Re: ExCo Notes - 2026-10-09 (updated)", "ExCo Notes Update.", "", "EXCO NOTES \u2003-\u3000 2026-10-09"]),
    (r"Weekly (?P<y>\d{4})-(?P<w>\d{2})\Z", ["Weekly 2026-41", "Weekly 2026-41\n", "Weekly 2026-412"]),
    (r"(?P<a>x)(?P=a)-(?P<b>[\s\d]+)", ["xx-1 2", "xy-1", "xx-\x1d3"]),
    (r"[^\S]+(?P<n>\w+)", [" hello", "\x1cworld", "none"]),
    (r"(?x) Notes \s+ (?P<d>\d+)", ["Notes 12", "Notes12"]),
    (r"Notes\S+(?P<d>\d{,2})$", ["Notes:1234", "Notes:1"]),
]

STAMPS = [
    ("2026-10-12T08:22:23.336Z", "2026-10-12T08:22:23.336Z"),
    ("2026-10-12T08:22:23.3369Z", "2026-10-12T08:22:23.336Z"),
    ("2026-10-12T08:22:23.3361Z", "2026-10-12T08:22:23.336Z"),
    ("2026-10-12T09:22:23.336+01:00", "2026-10-12T08:22:23.336Z"),
    ("2026-10-12T08:22:24Z", "2026-10-12T08:22:23.999999Z"),
    ("2026-10-12T08:22Z", "2026-10-12T08:21:59Z"),
    ("2026-10-12 08:22:23Z", "2026-10-12T08:22:23Z"),
]


def link(text: str):
    try:
        team, channel = notes.from_link(text)
        return {"team": team, "channel": channel}
    except ValueError as error:
        return {"error": str(error)}
    except IndexError as error:
        return {"crash": type(error).__name__}


def title(pattern: str, subject: str):
    match = re.compile(pattern).search(subject)
    return None if not match else {k: v for k, v in match.groupdict().items()}


def when(stamp: str) -> dt.datetime:
    return dt.datetime.fromisoformat(stamp.replace("Z", "+00:00"))


def main() -> int:
    report = {"written": ["C:\\a\\2026-10-09.md", "C:\\Caf\u00e9 & 'x'\\b.md"], "adopted": [], "untracked": ["C:\\u.md"],
              "unchanged": 7}
    config_path = "C:\\Users\\x\\OneDrive - Zen\\tools\\caf\u00e9.toml"
    summary = (f"{len(report['written'])} written, {len(report['adopted'])} adopted, "
               f"{len(report['untracked'])} untracked, {report['unchanged']} unchanged")
    out = {
        "python": sys.version.split()[0],
        "links": [{"link": text, **link(text)} for text in LINKS],
        "keys": [{"path": p, "lock": key(p), "state": hashlib.sha256(p.lower().encode()).hexdigest()[:12]} for p in KEYS],
        "titles": [{"pattern": p, "subject": s, "groups": title(p, s)} for p, subjects in TITLES for s in subjects],
        "stamps": [{"a": a, "b": b, "compare": (when(a) > when(b)) - (when(a) < when(b))} for a, b in STAMPS],
        "report": {"report": report, "json": json.dumps(report, indent=1), "summary": summary,
                   "config": config_path,
                   "payload": json.dumps({"config": config_path, "written": report["written"], "failed": [],
                                          "unmatched": [], "summary": summary})},
    }
    path = ROOT / "tests" / "TeamsChannelNotes.Tests" / "Golden" / "misc.json"
    path.write_text(json.dumps(out, indent=1, ensure_ascii=True), encoding="utf-8", newline="\n")
    print(path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
