using PyCompat;
using WinBackground;

namespace TeamsChannelNotes;

/// <summary>Starting the watcher at logon, through Windows Task Scheduler (WinBackground).
///
/// A task that runs as you, only while you're logged on, with no admin rights needed. It starts at logon, catches
/// up a missed start, restarts the watcher if it crashes, and is never stopped for running too long. It runs
/// teams-channel-notes-background.exe --config SETTINGS watch directly, so there's no window, and stopping the task
/// stops the watcher itself. The same task name as the Python version, so the EA's scripts need no change.</summary>
public static class Autostart
{
    public const string TaskName = "teams-channel-notes watch";
    public const string ConsoleExe = "teams-channel-notes.exe";
    public const string BackgroundExe = "teams-channel-notes-background.exe";

    private static readonly Dictionary<string, string> Results = new() { ["3"] = "another watcher was already running" };

    public static ITask Create(string name) =>
        new ScheduledTask(name, "Files Teams channel posts as markdown notes (teams-channel-notes watch).", Results);

    /// <summary>The program the task runs (the windowless one beside the running program, if it's there) and its
    /// arguments.</summary>
    public static (string Exe, List<string> Args) Command(string programDir, string configPath)
    {
        var background = Path.Combine(programDir, BackgroundExe);
        var exe = File.Exists(background) ? background : Path.Combine(programDir, ConsoleExe);
        return (exe, ["--config", PyPath.Resolve(configPath), "watch"]);
    }
}
