"""Locks per settings file: one watcher at a time, and one sync at a time (win-background).

Two watchers would sync the same channels twice, and a sync started by hand while the watcher is
syncing would write the same files and state at once. On Windows these are named mutexes, which
Windows releases if the process dies, so a crash can't leave one stuck.
"""

from __future__ import annotations

import contextlib

from win_background import Lock, exclusive, key, single_instance

from .config import LOCAL, Config


def watcher(config: Config) -> Lock | None:
    """Held for the watcher's lifetime. None if another watcher has it."""
    return single_instance(f"teams-channel-notes-watch-{key(config.path)}", LOCAL)


@contextlib.contextmanager
def run(config: Config, timeout: float = 600):
    """One sync at a time. Raises TimeoutError if another doesn't finish within timeout seconds."""
    with exclusive(f"teams-channel-notes-run-{key(config.path)}", timeout, LOCAL, what=f"sync for {config.path}"):
        yield
