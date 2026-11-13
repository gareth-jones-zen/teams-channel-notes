using System.ComponentModel;
using System.Text;
using PyCompat;
using WinBackground;

namespace TeamsChannelSync;

/// <summary>One locked sync, then the after-run hook if it wrote anything.
///
/// The hook is how a repo commits what was filed: the tool knows nothing about git. It gets a JSON report on
/// stdin, the same shape doc-filer gives its hook, so one script can serve both:
///
///     {"config": "...", "written": ["C:\\...\\2026-10-23.md"], "failed": [], "unmatched": [], "summary": "1 written"}
///
/// It runs from the settings file's folder, with no window.</summary>
public static class Run
{
    public static readonly TimeSpan HookTimeout = TimeSpan.FromSeconds(1800);
    public const double LockTimeoutSeconds = 600;

    /// <summary>The locks per settings file, named exactly as the Python version named them, so a .NET sync and a
    /// Python watcher exclude each other.</summary>
    public static string WatchLockName(Config config) => $"teams-channel-sync-watch-{Locks.Key(config.Path)}";

    public static string RunLockName(Config config) => $"teams-channel-sync-run-{Locks.Key(config.Path)}";

    /// <summary>The hook's stdin: json.dumps({...}) on one line.</summary>
    public static string Payload(Config config, Report report)
    {
        var b = new StringBuilder("{\"config\": ");
        PyJson.Quote(b, config.Path);
        b.Append(", \"written\": ").Append(PyJson.StringList(report.Written));
        b.Append(", \"failed\": [], \"unmatched\": [], \"summary\": ");
        PyJson.Quote(b, report.Summary());
        return b.Append('}').ToString();
    }

    public static void AfterRun(Config config, Report report, Action<string> log, Func<IReadOnlyList<string>, string, string, TimeSpan, Completed>? runner = null)
    {
        runner ??= (args, input, cwd, timeout) => args.Count == 0
            ? throw new ArgumentException("no program to run")
            : Shell.Run(args[0], args.Skip(1), timeout, stdin: input, workingDirectory: cwd, utf8: true);
        var payload = Payload(config, report);
        log($"after-run hook: {string.Join(' ', config.AfterRun)}");
        try
        {
            var done = runner(config.AfterRun, payload, Path.GetDirectoryName(config.Path)!, HookTimeout);
            foreach (var line in PyStr.SplitLines(done.StandardOutput + done.StandardError))
            {
                var text = PyStr.Strip(line);
                if (text.Length > 0)
                    log($"  hook: {text}");
            }
            if (done.ExitCode != 0)
                log($"after-run hook exited {done.ExitCode}");
        }
        catch (Win32Exception error)
        {
            log($"after-run hook failed: {Shell.WinErrorText(error)}");
        }
        catch (ShellTimeoutException error)
        {
            log($"after-run hook failed: {error.PythonMessage}");   // subprocess.TimeoutExpired's own wording
        }
        catch (Exception error)
        {
            log($"after-run hook failed: {error.Message}");
        }
    }

    /// <summary>run(): the sync under the run lock (TimeoutException if another doesn't finish within ten minutes),
    /// then the hook if wanted, not a dry run, something was written and after_run is set.</summary>
    public static Report Once(Config config, IGraph graph, bool dryRun = false, bool adopt = false, bool hook = false,
        string? only = null, Action<string>? log = null, Func<IReadOnlyList<string>, string, string, TimeSpan, Completed>? runner = null)
    {
        log ??= _ => { };
        Report report;
        using (var held = new NamedLock(RunLockName(config)))
        {
            if (!held.Acquire(LockTimeoutSeconds))
                throw new TimeoutException($"Another sync for {config.Path} didn't finish within {LockTimeoutSeconds / 60:0} minutes.");
            report = Notes.Sync(config, graph, dryRun, adopt, only, log);
        }
        if (hook && !dryRun && report.Written.Count > 0 && config.AfterRun.Count > 0)
            AfterRun(config, report, log, runner);
        return report;
    }
}
