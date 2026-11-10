"""Starting the watcher at logon, through Windows Task Scheduler (win-background).

A task that runs as you, only while you're logged on, with no admin rights needed. It starts at
logon, catches up a missed start, restarts the watcher if it crashes, and is never stopped for
running too long. It runs `pythonw -m teams_channel_notes --config <settings> watch` directly, so
there's no console window, and stopping the task stops the watcher itself.
"""

from __future__ import annotations

import pathlib

from win_background import Task, python_command

TASK = "teams-channel-notes watch"
_task = Task(TASK, "Files Teams channel posts as markdown notes (teams-channel-notes watch).",
             results={"3": "another watcher was already running"})


def command(config_path: pathlib.Path) -> tuple[str, list]:
    return python_command("teams_channel_notes", "--config", str(config_path.resolve()), "watch")


def enable(config_path: pathlib.Path) -> str:
    return _task.enable(*command(config_path), working_dir=config_path.resolve().parent)


def disable() -> bool:
    return _task.disable()


def status() -> dict | None:
    return _task.status()
