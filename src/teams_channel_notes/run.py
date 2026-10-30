"""One locked sync, then the after-run hook if it wrote anything.

The hook is how a repo commits what was filed: the tool knows nothing about git. It gets a JSON
report on stdin, the same shape doc-filer gives its hook, so one script can serve both:

    {"config": "...", "written": ["C:\\\\...\\\\2026-10-23.md"], "failed": [], "unmatched": [], "summary": "1 written"}

It runs from the settings file's folder, with no window.
"""

from __future__ import annotations

import json
import subprocess
import sys

from . import locks, notes
from .config import Config

NO_WINDOW = 0x08000000 if sys.platform == "win32" else 0


def summary(report: dict) -> str:
    return (f"{len(report['written'])} written, {len(report['adopted'])} adopted, "
            f"{len(report['untracked'])} untracked, {report['unchanged']} unchanged")


def after_run(config: Config, report: dict, log=print) -> None:
    payload = json.dumps({"config": str(config.path), "written": report["written"], "failed": [],
                          "unmatched": [], "summary": summary(report)})
    log(f"after-run hook: {' '.join(config.after_run)}")
    try:
        done = subprocess.run(config.after_run, input=payload, cwd=config.path.parent, capture_output=True,
                              text=True, encoding="utf-8", errors="replace", timeout=1800, creationflags=NO_WINDOW)
        for line in (done.stdout + done.stderr).splitlines():
            if line.strip():
                log(f"  hook: {line.strip()}")
        if done.returncode:
            log(f"after-run hook exited {done.returncode}")
    except Exception as error:
        log(f"after-run hook failed: {error}")


def run(config: Config, dry_run: bool = False, adopt: bool = False, hook: bool = False, only: str | None = None,
        interactive: bool | None = None, log=print) -> dict:
    with locks.run(config):
        report = notes.sync(config, dry_run=dry_run, adopt=adopt, only=only, interactive=interactive, log=log)
    if hook and not dry_run and report["written"] and config.after_run:
        after_run(config, report, log)
    return report
