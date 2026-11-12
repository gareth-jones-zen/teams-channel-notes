using M365Graph;
using WinBackground;

namespace TeamsChannelNotes;

/// <summary>Everything watch touches outside itself, so tests can run it with a fake clock and recorded Graph.</summary>
public sealed class WatchHost
{
    /// <summary>Seconds since some fixed point (Python's time.time()).</summary>
    public Func<double> Now { get; init; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    public Action<double> Sleep { get; init; } = seconds => Thread.Sleep(TimeSpan.FromSeconds(seconds));
    public Func<Config, IGraph> Graph { get; init; } = config => M365GraphClient.For(config, interactive: false);
    public Action<string, IReadOnlyList<string>> Notify { get; init; } = Toast.Notify;
    public Func<Config, Action<string>> Logger { get; init; } = config => WinBackground.Logger.Create(config.LogFile);
    public Func<string, DateTime> ModifiedTime { get; init; } = path => File.GetLastWriteTimeUtc(path) is var t && File.Exists(path)
        ? t
        : throw new FileNotFoundException($"[WinError 2] The system cannot find the file specified: '{path}'");
    public Func<IReadOnlyList<string>, string, string, TimeSpan, Completed>? HookRunner { get; init; }

    /// <summary>For tests: stop after this many ticks (null: never).</summary>
    public int? Ticks { get; init; }
}

/// <summary>Watch mode: sync on a timer, so notes are filed soon after they're posted, not when someone runs it.
///
/// - Every watch_every_minutes (default 15) it syncs every source and runs the after-run hook. Graph has no push
///   for channel posts without a public endpoint to receive it, so it polls; a sync with nothing new costs one
///   Graph call per source.
/// - One watcher per settings file. A second one exits at once (code 3).
/// - The settings file is re-read when it changes, so a new source needs no restart.
/// - It never opens a browser. A lapsed sign-in is logged and notified once, and it keeps trying, so it carries on
///   by itself once someone signs in again (`teams-channel-notes sign-in`).
/// - It logs `alive` once an hour, so a silent log means a stuck watcher, not a quiet channel.</summary>
public static class Watch
{
    public const int AlreadyRunning = 3;

    public static int Run(string? configPath, WatchHost host, Func<string, string?>? env = null)
    {
        var config = Config.Load(configPath, env);
        var log = host.Logger(config);
        using var held = WinBackground.Locks.SingleInstance(TeamsChannelNotes.Run.WatchLockName(config));
        if (held is null)
        {
            log("another watcher is already running for these settings; not starting a second");
            return AlreadyRunning;
        }
        log($"watching {config.Sources.Count} source(s) every {config.WatchEveryMinutes} min (settings {config.Path})");
        var stamp = host.ModifiedTime(config.Path);
        double nextSync = 0, beat = host.Now();
        string? alerted = null;
        IGraph? graph = null;
        Config? graphFor = null;
        for (var tick = 0; host.Ticks is not { } limit || tick < limit; tick++)
        {
            try
            {
                if (host.ModifiedTime(config.Path) != stamp)
                {
                    stamp = host.ModifiedTime(config.Path);
                    try
                    {
                        config = Config.Load(config.Path, env);
                        log("settings changed; reloaded");
                        nextSync = 0;
                    }
                    catch (Exception error)
                    {
                        log($"settings changed but didn't load, so the old ones stay in use: {error.Message}");
                    }
                }
                if (host.Now() >= nextSync)
                {
                    nextSync = host.Now() + config.WatchEveryMinutes * 60;
                    if (!ReferenceEquals(graphFor, config))
                    {
                        graph = host.Graph(config);   // one client per settings, so its token is reused between syncs
                        graphFor = config;
                    }
                    var report = TeamsChannelNotes.Run.Once(config, graph!, hook: true, log: log, runner: host.HookRunner);
                    if (report.Written.Count > 0 || report.Untracked.Count > 0)
                        log(report.Summary());
                    if (alerted is not null)
                        log("working again");
                    alerted = null;
                }
                if (host.Now() - beat >= 3600)
                {
                    beat = host.Now();
                    log("alive");
                }
            }
            catch (SignInNeededException error)
            {
                log($"sign-in needed: {error.Message}");
                if (alerted != error.Message)
                    host.Notify("Teams channel notes needs a sign-in", [error.Message]);
                alerted = error.Message;
            }
            catch (Exception error)
            {
                log($"sync failed, will try again: {PyText.TakeCodePoints(error.Message, 300)}");
            }
            host.Sleep(30);
        }
        return 0;
    }
}
