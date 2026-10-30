"""Starting the watcher at logon, through Windows Task Scheduler.

The same shape as teams-transcripts' autostart: a task that runs as you, only while you're logged
on, with no admin rights needed. It starts at logon, catches up a missed start, restarts the watcher
if it crashes, and is never stopped for running too long. It runs `pythonw -m teams_channel_notes
--config <settings> watch` directly, so there's no console window, and stopping the task stops the
watcher itself (doc-filer learned that a wrapper in between can leave the watcher running).
"""

from __future__ import annotations

import pathlib
import subprocess
import sys

from .run import NO_WINDOW

TASK = "teams-channel-notes watch"


def command(config_path: pathlib.Path) -> tuple[str, list]:
    python = pathlib.Path(sys.executable)
    windowless = python.with_name("pythonw.exe")
    return str(windowless if windowless.exists() else python), [
        "-m", "teams_channel_notes", "--config", str(config_path.resolve()), "watch"]


def _ps(script: str) -> subprocess.CompletedProcess:
    return subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", script],
                          capture_output=True, text=True, timeout=120, creationflags=NO_WINDOW)


def _q(text: str) -> str:
    return "'" + text.replace("'", "''") + "'"


def enable(config_path: pathlib.Path) -> str:
    if sys.platform != "win32":
        raise RuntimeError("autostart needs Windows Task Scheduler.")
    exe, args = command(config_path)
    done = _ps(f"""
$ErrorActionPreference = 'Stop'
$me = "$env:USERDOMAIN\\$env:USERNAME"
$action = New-ScheduledTaskAction -Execute {_q(exe)} -Argument {_q(subprocess.list2cmdline(args))} -WorkingDirectory {_q(str(config_path.resolve().parent))}
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $me
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 5) -MultipleInstances IgnoreNew
$principal = New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited
if (Get-ScheduledTask -TaskName {_q(TASK)} -ErrorAction SilentlyContinue) {{ Stop-ScheduledTask -TaskName {_q(TASK)} }}
Register-ScheduledTask -TaskName {_q(TASK)} -Action $action -Trigger $trigger -Settings $settings -Principal $principal `
    -Description 'Files Teams channel posts as markdown notes (teams-channel-notes watch).' -Force | Out-Null
Start-ScheduledTask -TaskName {_q(TASK)}
""")
    if done.returncode:
        raise RuntimeError(f"Couldn't create the task: {(done.stderr or done.stdout).strip()[:400]}")
    return f"{exe} {subprocess.list2cmdline(args)}"


def disable() -> bool:
    done = _ps(f"""
$task = Get-ScheduledTask -TaskName {_q(TASK)} -ErrorAction SilentlyContinue
if ($task) {{ Stop-ScheduledTask -TaskName {_q(TASK)}; Unregister-ScheduledTask -TaskName {_q(TASK)} -Confirm:$false; 'removed' }}
exit 0
""")
    if done.returncode:
        raise RuntimeError(f"Couldn't remove the task: {(done.stderr or done.stdout).strip()[:400]}")
    return "removed" in done.stdout


def status() -> dict | None:
    done = _ps(f"""
$task = Get-ScheduledTask -TaskName {_q(TASK)} -ErrorAction SilentlyContinue
if ($task) {{
  $info = Get-ScheduledTaskInfo -TaskName {_q(TASK)}
  "state=$($task.State)"
  "command=$($task.Actions[0].Execute) $($task.Actions[0].Arguments)"
  "last_run=$($info.LastRunTime)"
  "last_result=$($info.LastTaskResult)"
}}
exit 0
""")
    if not done.stdout.strip():
        return None
    info = dict(line.split("=", 1) for line in done.stdout.strip().splitlines() if "=" in line)
    if info.get("last_result") == "267011":   # SCHED_S_TASK_HAS_NOT_RUN
        info["last_run"], info["last_result"] = "never", "-"
    elif info.get("last_result") == "267009":   # SCHED_S_TASK_RUNNING
        info["last_result"] = "running"
    elif info.get("last_result") == "3":
        info["last_result"] = "3 (another watcher was already running)"
    return info
