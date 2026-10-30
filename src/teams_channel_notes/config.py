"""The settings file: who signs in, and which channels become which folders of notes.

    login_hint = "you@example.com"          # optional
    tenant = "organizations"                # optional: a tenant id pins sign-in to one organisation
    msal_cache = "..."                      # optional: share another tool's sign-in
    state_file = "..."                      # optional

    [[source]]
    name = "exco"
    team = "<team id>"                      # `teams-channel-notes channels` lists them
    channel = "<channel id>"
    title = '(?i)ExCo Notes\\s*[-–]\\s*(?P<date>\\d{4}-\\d{2}-\\d{2})'
    folder = "exco-notes"                   # relative to this file
    filename = "{date}.md"                  # named groups from `title`
    format = "bullets"                      # bullets | markdown
    recent = 10                             # how many of the newest posts to look at

Paths may use ~ and %VARIABLES%; relative paths are relative to the settings file.
"""

from __future__ import annotations

import hashlib
import os
import pathlib
import re
import tomllib
from dataclasses import dataclass, field

from .convert import FORMATS

APPDATA = pathlib.Path(os.environ.get("APPDATA", pathlib.Path.home()))
LOCAL = pathlib.Path(os.environ.get("LOCALAPPDATA", pathlib.Path.home())) / "teams-channel-notes"
DEFAULT_CONFIG = APPDATA / "teams-channel-notes" / "config.toml"
SCOPES = ("ChannelMessage.Read.All",)

TEMPLATE = """\
# teams-channel-notes settings. See the README.
# login_hint = "you@example.com"

# One [[source]] per channel. `teams-channel-notes channels` lists your teams and channels with their ids.
# [[source]]
# name = "weekly-notes"
# team = "<team id>"
# channel = "<channel id>"
# title = '(?i)Weekly notes\\s*[-–]\\s*(?P<date>\\d{4}-\\d{2}-\\d{2})'
# folder = "weekly-notes"
# filename = "{date}.md"
# format = "markdown"
"""


@dataclass
class Source:
    name: str
    team: str
    channel: str
    title: re.Pattern
    folder: pathlib.Path
    filename: str = "{date}.md"
    format: str = "markdown"
    recent: int = 10


@dataclass
class Config:
    path: pathlib.Path
    sources: list[Source] = field(default_factory=list)
    login_hint: str | None = None
    tenant: str = "organizations"
    msal_cache: pathlib.Path | None = None
    state_file: pathlib.Path = LOCAL / "state.json"


def _path(value: str, base: pathlib.Path) -> pathlib.Path:
    path = pathlib.Path(os.path.expandvars(os.path.expanduser(value)))
    return path if path.is_absolute() else (base / path).resolve()


def load(path: pathlib.Path | None = None) -> Config:
    path = pathlib.Path(path or os.environ.get("TEAMS_CHANNEL_NOTES_CONFIG") or DEFAULT_CONFIG).resolve()
    if not path.exists():
        raise FileNotFoundError(f"No settings file at {path}. Run `teams-channel-notes config` to create one.")
    raw = tomllib.loads(path.read_text(encoding="utf-8"))
    base = path.parent
    key = hashlib.sha256(str(path).lower().encode()).hexdigest()[:12]
    config = Config(path=path, login_hint=raw.get("login_hint"), tenant=raw.get("tenant", "organizations"),
                    msal_cache=_path(raw["msal_cache"], base) if raw.get("msal_cache") else None,
                    state_file=_path(raw["state_file"], base) if raw.get("state_file") else LOCAL / f"state-{key}.json")
    for i, item in enumerate(raw.get("source", []), 1):
        missing = [k for k in ("team", "channel", "title", "folder") if not item.get(k)]
        if missing:
            raise ValueError(f"[[source]] {item.get('name', i)} in {path} has no {', '.join(missing)}.")
        fmt = item.get("format", "markdown")
        if fmt not in FORMATS:
            raise ValueError(f"[[source]] {item.get('name', i)}: format must be one of {', '.join(FORMATS)}.")
        config.sources.append(Source(
            name=item.get("name", f"source{i}"), team=item["team"], channel=item["channel"],
            title=re.compile(item["title"]), folder=_path(item["folder"], base),
            filename=item.get("filename", "{date}.md"), format=fmt, recent=int(item.get("recent", 10))))
    return config


def create_default() -> pathlib.Path:
    if not DEFAULT_CONFIG.exists():
        DEFAULT_CONFIG.parent.mkdir(parents=True, exist_ok=True)
        DEFAULT_CONFIG.write_text(TEMPLATE, encoding="utf-8")
    return DEFAULT_CONFIG
