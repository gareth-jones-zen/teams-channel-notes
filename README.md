# teams-channel-notes

Keeps a folder of markdown notes in step with a Teams channel's posts. Written for a weekly
"ExCo Notes - 2026-10-09" post, it works for any channel where the posts you want can be told
apart by their titles: weekly notes, release notes, a team digest.

- **Each post is filed by the date in its title**, never the date it went up. Posts whose titles
  don't match (notices, chatter, replies) get no file.
- **Authors edit posts after publishing.** A post edited since its file was written replaces the
  file, if the text changed.
- **It never overwrites a file it didn't write.** A file already there (copied in by hand, say) is
  taken on only if it matches the post. Otherwise it's reported, and `adopt` takes it on as it stands.

## Install

Needs Windows or anything else with Python 3.11+.

```
pipx install --preinstall git+https://github.com/gareth-jones-zen/m365-graph git+https://github.com/gareth-jones-zen/teams-channel-notes
```

`m365-graph` is the sign-in and Graph library it shares with teams-transcripts. It isn't on PyPI,
so it's installed first, from its repo.

## Getting started

```
teams-channel-notes config       # create the settings file and say where it is
teams-channel-notes sign-in      # once: a browser opens to sign in to Microsoft
teams-channel-notes ids "<link>" # a channel's team and channel ids, from Teams' Copy link
```

**Finding the ids:**
- In Teams, open the channel's **…** menu, choose **Copy link**, and pass it to `ids`. It needs no sign-in.
- `teams-channel-notes channels` lists every team and channel you're in instead. The first time, it asks you to approve reading team and channel names.

Then add a `[[source]]` to the settings file:

```toml
login_hint = "you@example.com"

[[source]]
name = "exco"
team = "<team id>"
channel = "<channel id>"
title = '(?i)ExCo Notes\s*[-–]\s*(?P<date>\d{4}-\d{2}-\d{2})'   # named groups name the file
folder = "C:/Users/you/Documents/ExCo notes"                      # or relative to the settings file
filename = "{date}.md"
format = "bullets"        # bullets | markdown
recent = 10               # how many of the newest posts to look at
```

```
teams-channel-notes sync --dry-run   # what it would write
teams-channel-notes sync             # write new and edited notes
teams-channel-notes check            # compare the recent posts with the files, line by line
teams-channel-notes adopt            # take on hand-copied files as they stand
```

`sync --json` prints what was written.

## Keeping it current: `watch` and `autostart`

```
teams-channel-notes autostart on       # watch at every logon, and start it now
teams-channel-notes autostart status
teams-channel-notes autostart off
teams-channel-notes watch              # the same loop, in a console
```

`watch` syncs every source every `watch_every_minutes` (default 15), so notes posted on a Friday
evening are filed by the time anyone looks.

- **It polls.** Graph can only push channel posts to a public web endpoint, so it asks. A sync with nothing new costs one Graph call per source.
- **One watcher per settings file.** A second one exits at once, with code 3. A sync run by hand waits for one already in progress.
- **The settings are re-read when they change**, so a new source needs no restart.
- **It never opens a browser.** A lapsed sign-in is logged and notified once, and it carries on once someone runs `sign-in`.
- **It logs `alive` hourly**, to `%LOCALAPPDATA%\teams-channel-notes\teams-channel-notes.log` by default (set `log_file`). A silent log means a stuck watcher, not a quiet channel.

**`autostart on`** registers a Task Scheduler task, `teams-channel-notes watch`, the same shape as teams-transcripts' autostart:
- it runs as you, only while you're logged on, with no admin rights;
- it catches up a missed start, restarts the watcher after a crash, and is never stopped for running too long;
- it runs `pythonw` directly, so there's no window, and stopping the task stops the watcher.

## The after-run hook

To do something with what's filed, such as commit it to git or forward it, set `after_run`:

```toml
after_run = ["python", "commit.py"]
```

It runs after any sync that wrote a file: always in `watch`, and with `--hook` for `sync`. It runs from the settings file's folder, with no window, and gets a JSON report on stdin:

```json
{"config": "C:\\…\\config.toml", "written": ["C:\\…\\exco-notes\\2026-10-23.md"],
 "failed": [], "unmatched": [], "summary": "1 written, 0 adopted, 0 untracked, 7 unchanged"}
```

It's the same shape as [doc-filer](https://github.com/gareth-jones-zen/doc-filer)'s hook, so one script can serve both. Here `written` holds full paths. Its output goes to the log.

## Formats

- **`bullets`:** only the post's lists, as plain nested bullets with a two-space indent. A header or sign-off outside the list is dropped. Entities are converted, emphasis dropped, emoji and wording kept, and links reduced to their text. This is for posts written as one nested list.
- **`markdown`:** the whole post, with paragraphs, line breaks, nested and numbered lists, bold, italic, and links as `[text](url)`.

## Sign-in and permissions

It signs in with [m365-graph](https://github.com/gareth-jones-zen/m365-graph), as Microsoft's
"Graph Command Line Tools" app.

- **Reading posts needs `ChannelMessage.Read.All`.** That usually needs an admin's approval: request it on the "Approval required" screen at sign-in. Some organisations have already granted it for this app.
- **`channels` also needs `Team.ReadBasic.All` and `Channel.ReadBasic.All`,** which users can usually approve themselves.

**Settings:**
- `msal_cache` points at another tool's token cache, to share its sign-in.
- `tenant` pins sign-in to one organisation.
- `state_file` sets where it records what it has written (default `%LOCALAPPDATA%\teams-channel-notes\`).

## What it can't do yet

- **Notes posted as a reply have no title**, so they're skipped.
