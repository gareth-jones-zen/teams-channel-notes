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

import datetime as dt
import pathlib
import subprocess
import sys
import time

from m365_graph import SignInNeeded

from . import config as cfg, locks
from .run import NO_WINDOW, run, summary

ALREADY_RUNNING = 3


def _logger(config: cfg.Config):
    def log(text: str) -> None:
        line = f"{dt.datetime.now():%Y-%m-%d %H:%M:%S}  {text}"
        try:
            print(line, flush=True)
        except Exception:
            pass   # pythonw has no console
        try:
            config.log_file.parent.mkdir(parents=True, exist_ok=True)
            with open(config.log_file, "a", encoding="utf-8") as handle:
                handle.write(line + "\n")
        except OSError:
            pass
    return log


def notify(title: str, lines: list) -> None:
    """A Windows notification, through PowerShell. Best effort."""
    if sys.platform != "win32":
        return
    text = "\n".join(lines)[:600].replace("'", "''")
    script = f"""
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
$t = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02)
$t.GetElementsByTagName('text').Item(0).AppendChild($t.CreateTextNode('{title.replace("'", "''")}')) | Out-Null
$t.GetElementsByTagName('text').Item(1).AppendChild($t.CreateTextNode('{text}')) | Out-Null
$app = '{{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}}\\WindowsPowerShell\\v1.0\\powershell.exe'
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($app).Show([Windows.UI.Notifications.ToastNotification]::new($t))
"""
    try:
        subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", script],
                       capture_output=True, timeout=30, creationflags=NO_WINDOW)
    except Exception:
        pass


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
