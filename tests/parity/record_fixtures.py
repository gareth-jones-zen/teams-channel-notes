r"""Record a channel's posts as fixtures for the .NET port's parity tests. Read-only: Graph GETs only.

    python tests/parity/record_fixtures.py --config <settings.toml> [--source exco]

It never signs in interactively (interactive=False), never touches the notes folder, the state file or the
hook, and writes only to %LOCALAPPDATA%\teams-channel-notes-port\fixtures\YYYY-MM-DD\:

    recent-<source>.json     the exact $top=<recent> response the tool sees
    history-<source>.json    every post in the channel, paged with $top=50 and @odata.nextLink

The posts can carry confidential figures, so the fixtures stay out of every repository.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / "src"))

from teams_channel_notes import config as cfg  # noqa: E402
from teams_channel_notes.config import SCOPES  # noqa: E402
from teams_channel_notes.notes import client  # noqa: E402

FIXTURES = pathlib.Path(os.environ["LOCALAPPDATA"]) / "teams-channel-notes-port" / "fixtures"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    parser.add_argument("--source")
    args = parser.parse_args()
    config = cfg.load(pathlib.Path(args.config))
    graph = client(config, interactive=False)
    folder = FIXTURES / dt.date.today().isoformat()
    folder.mkdir(parents=True, exist_ok=True)
    for source in config.sources:
        if args.source and source.name != args.source:
            continue
        base = f"/teams/{source.team}/channels/{source.channel}/messages"
        recent = graph.get(f"{base}?$top={source.recent}", scopes=SCOPES)
        (folder / f"recent-{source.name}.json").write_text(json.dumps(recent, indent=1, ensure_ascii=False),
                                                           encoding="utf-8")
        history = list(graph.pages(f"{base}?$top=50", scopes=SCOPES))
        (folder / f"history-{source.name}.json").write_text(
            json.dumps({"value": history}, indent=1, ensure_ascii=False), encoding="utf-8")
        print(f"{source.name}: {len(recent.get('value', []))} recent, {len(history)} in the history")
    print(folder)
    return 0


if __name__ == "__main__":
    sys.exit(main())
