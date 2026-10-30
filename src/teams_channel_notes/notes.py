"""Fetch a channel's recent posts and keep a folder of notes in step with them.

The rules, from filing a weekly ExCo notes post by hand and then automatically:
- **A post is named by the date in its title** ("ExCo Notes - 2026-10-09"), never the date it went up.
  A post whose title doesn't match (a notice such as "No meeting this week", or a reply, which has no
  title) gets no file.
- **Authors edit posts after publishing.** Each file's state records the post's lastModifiedDateTime
  when it was written; a post modified since, whose text now differs, replaces the file.
- **Files the tool didn't write are never overwritten.** A file that exists with no state (copied in by
  hand, say) is taken on only if it matches the post, ignoring whitespace. Otherwise it's reported as
  untracked, and `adopt` takes it on as it stands, so later edits to the post replace it.
"""

from __future__ import annotations

import datetime as dt
import json
import pathlib
from dataclasses import dataclass

from m365_graph import Client

from .config import SCOPES, Config, Source
from .convert import FORMATS


@dataclass
class Post:
    source: Source
    path: pathlib.Path
    modified: str
    text: str


def client(config: Config, interactive: bool | None = None) -> Client:
    kwargs = {"cache_path": config.msal_cache} if config.msal_cache else {}
    return Client(tenant=config.tenant, login_hint=config.login_hint, scopes=list(SCOPES), interactive=interactive,
                  sign_in_hint="At a console, run: teams-channel-notes sign-in", **kwargs)


def posts(config: Config, graph: Client, source: Source) -> list[Post]:
    url = f"/teams/{source.team}/channels/{source.channel}/messages?$top={source.recent}"
    found = []
    for message in graph.get(url, scopes=SCOPES).get("value", []):
        match = source.title.search(message.get("subject") or "")
        if message.get("messageType") != "message" or message.get("deletedDateTime") or not match:
            continue
        name = source.filename.format(**match.groupdict())
        text = FORMATS[source.format]((message.get("body") or {}).get("content") or "")
        found.append(Post(source, source.folder / name, message.get("lastModifiedDateTime") or "", text))
    return found


def _same(a: str, b: str) -> bool:
    return " ".join(a.split()) == " ".join(b.split())


def _when(stamp: str) -> dt.datetime:
    return dt.datetime.fromisoformat(stamp.replace("Z", "+00:00"))


class State:
    def __init__(self, path: pathlib.Path):
        self.path = path
        try:
            self.data = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            self.data = {}

    def get(self, file: pathlib.Path) -> str | None:
        return self.data.get(str(file).lower())

    def set(self, file: pathlib.Path, modified: str):
        self.data[str(file).lower()] = modified

    def save(self):
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.path.write_text(json.dumps(self.data, indent=1, sort_keys=True), encoding="utf-8")


def sync(config: Config, dry_run: bool = False, adopt: bool = False, only: str | None = None,
         interactive: bool | None = None, log=print) -> dict:
    """Write new and edited notes. adopt=True takes on untracked files as they stand, writing nothing.
    Returns {"written": [...], "adopted": [...], "untracked": [...], "unchanged": n}."""
    graph = client(config, interactive)
    state = State(config.state_file)
    report = {"written": [], "adopted": [], "untracked": [], "unchanged": 0}
    for source in config.sources:
        if only and source.name != only:
            continue
        for post in posts(config, graph, source):
            path, recorded = post.path, state.get(post.path)
            if not path.exists():
                if adopt:
                    continue
                log(f"{source.name}: {path.name} new" + (" (dry run)" if dry_run else ""))
                if not dry_run:
                    path.parent.mkdir(parents=True, exist_ok=True)
                    path.write_text(post.text, encoding="utf-8")
                    state.set(path, post.modified)
                report["written"].append(str(path))
                continue
            old = path.read_text(encoding="utf-8")
            if recorded is None:
                if _same(old, post.text) or adopt:
                    state.set(path, post.modified)
                    report["adopted"].append(str(path))
                    if adopt and not _same(old, post.text):
                        log(f"{source.name}: {path.name} adopted as it stands (differs from the post)")
                else:
                    log(f"{source.name}: {path.name} exists but wasn't written by this tool and differs from the "
                        "post; left alone (`adopt` takes it on)")
                    report["untracked"].append(str(path))
                continue
            if adopt or _when(post.modified) <= _when(recorded) or _same(old, post.text):
                if not adopt and _when(post.modified) > _when(recorded):
                    state.set(path, post.modified)    # edited, but the text we keep didn't change
                report["unchanged"] += 1
                continue
            log(f"{source.name}: {path.name} edited since filed (post modified {post.modified})"
                + (" (dry run)" if dry_run else ""))
            if not dry_run:
                path.write_text(post.text, encoding="utf-8")
                state.set(path, post.modified)
            report["written"].append(str(path))
    if not dry_run:
        state.save()
    return report


def check(config: Config, log=print) -> int:
    """Convert the recent posts and compare them with the files, line by line. 1 if any differ."""
    import difflib
    graph = client(config)
    differ = 0
    for source in config.sources:
        for post in posts(config, graph, source):
            if not post.path.exists():
                log(f"{source.name}: {post.path.name}: no file")
                continue
            old = post.path.read_text(encoding="utf-8").replace("\r\n", "\n")
            if old == post.text:
                log(f"{source.name}: {post.path.name}: identical")
                continue
            differ += 1
            log(f"{source.name}: {post.path.name}: DIFFERS")
            log("".join(difflib.unified_diff(old.splitlines(True), post.text.splitlines(True), "file", "post", n=0)))
    return 1 if differ else 0


def from_link(link: str) -> tuple[str, str]:
    """(team id, channel id) from a channel's "Copy link" in Teams, with no sign-in needed:
    https://teams.microsoft.com/l/channel/19%3a...%40thread.tacv2/Name?groupId=<team id>&tenantId=..."""
    import urllib.parse
    parsed = urllib.parse.urlparse(link.strip())
    parts = parsed.path.split("/")
    if "channel" not in parts:
        raise ValueError("That isn't a Teams channel link. In Teams, use the channel's ... menu, then Copy link.")
    channel = urllib.parse.unquote(parts[parts.index("channel") + 1])
    team = urllib.parse.parse_qs(parsed.query).get("groupId", [""])[0]
    if not team:
        raise ValueError("The link has no groupId; copy the channel's link, not a message's.")
    return team, channel


def channels(config: Config, log=print) -> None:
    """Your teams and their channels, with the ids a [[source]] needs. Run by hand, so it may open a
    browser to approve Team.ReadBasic.All and Channel.ReadBasic.All (users can usually approve these)."""
    graph = client(config, interactive=True)
    scopes = ["Team.ReadBasic.All", "Channel.ReadBasic.All"]
    for team in graph.pages("/me/joinedTeams?$select=id,displayName", scopes=scopes):
        log(f"{team['displayName']}\n  team = \"{team['id']}\"")
        for channel in graph.pages(f"/teams/{team['id']}/channels?$select=id,displayName", scopes=scopes):
            log(f"    {channel['displayName']}\n      channel = \"{channel['id']}\"")
