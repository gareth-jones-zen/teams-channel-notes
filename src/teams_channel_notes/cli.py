"""teams-channel-notes: keep a folder of markdown notes in step with a Teams channel's posts.

    teams-channel-notes config             create the settings file (if missing) and say where it is
    teams-channel-notes sign-in            sign in to Microsoft (once; opens a browser)
    teams-channel-notes channels           your teams and channels, with the ids a source needs
    teams-channel-notes ids "<link>"       the same ids, from a channel's Copy link (no sign-in)
    teams-channel-notes sync               write new posts, and posts edited since they were filed
    teams-channel-notes sync --dry-run     say what it would write
    teams-channel-notes sync --hook        then run the settings' after_run command, if anything was written
    teams-channel-notes check              compare the recent posts with the files, line by line
    teams-channel-notes adopt              take on files that were copied in by hand, as they stand
    teams-channel-notes watch              stay running, and sync every watch_every_minutes
    teams-channel-notes autostart on       run the watcher at every logon, and start it now (off, status)
"""

from __future__ import annotations

import argparse
import json
import pathlib
import sys

from m365_graph import SignInNeeded

from . import __version__, config as cfg, notes
from .run import run, summary


def main(argv: list | None = None) -> int:
    parser = argparse.ArgumentParser(prog="teams-channel-notes", description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--version", action="version", version=__version__)
    parser.add_argument("--config", help="settings file (default %%APPDATA%%\\teams-channel-notes\\config.toml)")
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("config", help="create the settings file if missing, and say where it is")
    sub.add_parser("sign-in", help="sign in to Microsoft (opens a browser)")
    sub.add_parser("channels", help="list your teams and channels with their ids")
    link = sub.add_parser("ids", help="the team and channel ids in a channel link (no sign-in needed)")
    link.add_argument("link", help="from the channel's ... menu in Teams: Copy link")
    for name in ("sync", "check", "adopt"):
        p = sub.add_parser(name)
        p.add_argument("--source", help="only this [[source]], by name")
        if name in ("sync", "adopt"):
            p.add_argument("--json", action="store_true", help="print the report as JSON")
        if name == "sync":
            p.add_argument("--dry-run", action="store_true")
            p.add_argument("--hook", action="store_true", help="run the after_run command if anything was written")
    sub.add_parser("watch", help="stay running, and sync on a timer")
    auto = sub.add_parser("autostart", help="run the watcher at every logon (Task Scheduler)")
    auto.add_argument("action", choices=["on", "off", "status"])
    args = parser.parse_args(argv)

    if args.command == "config":
        path = args.config or cfg.create_default()
        print(path)
        return 0
    if args.command == "ids":
        try:
            team, channel = notes.from_link(args.link)
        except ValueError as error:
            print(error, file=sys.stderr)
            return 2
        print(f'team = "{team}"\nchannel = "{channel}"')
        return 0
    if args.command == "watch":
        from .watch import watch
        return watch(pathlib.Path(args.config) if args.config else None)
    if args.command == "autostart":
        return _autostart(args)
    try:
        config = cfg.load(args.config)
        if args.command == "sign-in":
            notes.client(config, interactive=True).token()
            print("Signed in.")
            return 0
        if args.command == "channels":
            notes.channels(config)
            return 0
        if args.command == "check":
            return notes.check(config)
        log = (lambda text: print(text, file=sys.stderr)) if getattr(args, "json", False) else print
        report = run(config, dry_run=getattr(args, "dry_run", False), adopt=(args.command == "adopt"),
                     hook=getattr(args, "hook", False), only=args.source, log=log)
        print(json.dumps(report, indent=1) if args.json else summary(report))
        return 0
    except (SignInNeeded, FileNotFoundError, ValueError, TimeoutError) as error:
        print(error, file=sys.stderr)
        return 2


def _autostart(args) -> int:
    from . import autostart
    try:
        if args.action == "on":
            config = cfg.load(args.config)   # check the settings load before scheduling them
            command = autostart.enable(config.path)
            print(f"Autostart is on: '{autostart.TASK}' runs at every logon, and has started now.\n  {command}\n"
                  f"  log: {config.log_file}")
        elif args.action == "off":
            print("Autostart is off." if autostart.disable() else "Autostart was already off.")
        else:
            info = autostart.status()
            if info is None:
                print("Autostart is off.")
            else:
                print(f"Autostart is on ('{autostart.TASK}').")
                for key in ("state", "last_run", "last_result", "command"):
                    print(f"  {key.replace('_', ' '):12} {info.get(key, '')}")
        return 0
    except (RuntimeError, FileNotFoundError, ValueError) as error:
        print(error, file=sys.stderr)
        return 2
