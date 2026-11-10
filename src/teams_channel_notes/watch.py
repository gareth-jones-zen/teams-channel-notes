"""Watch mode: sync on a timer, so notes are filed soon after they're posted, not when someone runs it.

- **Every watch_every_minutes** (default 15) it syncs every source and runs the after-run hook.
  Graph has no push for channel posts without a public endpoint to receive it, so it polls; a
  sync with nothing new costs one Graph call per source.
- **One watcher per settings file.** A second one exits at once (code 3).
- **The settings file is re-read when it changes**, so a new source needs no restart.
- **It never opens a browser.** A lapsed sign-in is logged and notified once, and it keeps trying,
  so it carries on by itself once someone signs in again (`teams-channel-notes sign-in`).
- **It logs `alive` once an hour**, so a silent log means a stuck watcher, not a quiet channel.
"""

from __future__ import annotations

import pathlib
import time

from m365_graph import SignInNeeded
from win_background import logger, notify

from . import config as cfg, locks
from .run import run, summary

ALREADY_RUNNING = 3


def _logger(config: cfg.Config):
    return logger(config.log_file)


def watch(config_path: pathlib.Path | None = None) -> int:
    config = cfg.load(config_path)
    log = _logger(config)
    lock = locks.watcher(config)   # held until the process ends
    if lock is None:
        log("another watcher is already running for these settings; not starting a second")
        return ALREADY_RUNNING
    log(f"watching {len(config.sources)} source(s) every {config.watch_every_minutes} min (settings {config.path})")
    stamp = config.path.stat().st_mtime
    next_sync, beat, alerted = 0.0, time.time(), None
    while True:
        try:
            if config.path.stat().st_mtime != stamp:
                stamp = config.path.stat().st_mtime
                try:
                    config = cfg.load(config.path)
                    log("settings changed; reloaded")
                    next_sync = 0.0
                except Exception as error:
                    log(f"settings changed but didn't load, so the old ones stay in use: {error}")
            if time.time() >= next_sync:
                next_sync = time.time() + config.watch_every_minutes * 60
                report = run(config, hook=True, interactive=False, log=log)
                if report["written"] or report["untracked"]:
                    log(summary(report))
                if alerted:
                    log("working again")
                alerted = None
            if time.time() - beat >= 3600:
                beat = time.time()
                log("alive")
        except SignInNeeded as error:
            log(f"sign-in needed: {error}")
            if alerted != str(error):
                notify("Teams channel notes needs a sign-in", [str(error)])
            alerted = str(error)
        except Exception as error:
            log(f"sync failed, will try again: {str(error)[:300]}")
        time.sleep(30)
