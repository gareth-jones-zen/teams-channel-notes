using System.Text.Json.Nodes;
using M365Graph;
using WinBackground;

namespace TeamsChannelNotes.Tests;

/// <summary>watch, with a fake clock: one tick is 30 seconds.</summary>
public class WatchTests
{
    private sealed class Clock
    {
        public double Now = 1_000_000;
    }

    private sealed class ScriptedGraph(Func<int, JsonArray> reply) : IGraph
    {
        public int Calls;

        public JsonObject Get(string url, IReadOnlyList<string> scopes) => new() { ["value"] = reply(++Calls) };

        public IEnumerable<JsonNode?> Pages(string url, IReadOnlyList<string> scopes) => [];

        public void Token(IReadOnlyList<string> scopes)
        {
        }
    }

    private static (WatchHost Host, List<string> Log, List<string> Notified) Host(Clock clock, IGraph graph, int ticks,
        Func<string, DateTime>? modified = null)
    {
        var log = new List<string>();
        var notified = new List<string>();
        var host = new WatchHost
        {
            Now = () => clock.Now,
            Sleep = seconds => clock.Now += seconds,
            Graph = _ => graph,
            Notify = (title, lines) => notified.Add(title + ": " + string.Join(" ", lines)),
            Logger = _ => log.Add,
            ModifiedTime = modified ?? (_ => new DateTime(2026, 11, 12)),
            Ticks = ticks,
        };
        return (host, log, notified);
    }

    [Fact]
    public void Syncs_every_n_minutes_and_logs_alive_hourly()
    {
        using var temp = new Golden.TempFolder();
        var config = RunTests.Setup(temp);
        var graph = new ScriptedGraph(n => n == 1
            ? RunTests.Messages(("ExCo Notes - 2026-10-09", "2026-10-12T08:00:00Z", "<ul><li>a</li></ul>"))
            : RunTests.Messages(("ExCo Notes - 2026-10-09", "2026-10-12T08:00:00Z", "<ul><li>a</li></ul>"),
                                ("ExCo Notes - 2026-10-16", "2026-10-19T08:00:00Z", "<ul><li>b</li></ul>")));
        var clock = new Clock();
        var (host, log, notified) = Host(clock, graph, ticks: 125);   // 62.5 minutes
        Assert.Equal(0, Watch.Run(config.Path, host));
        Assert.Equal(5, graph.Calls);   // at 0, 15, 30, 45 and 60 minutes
        Assert.Equal($"watching 1 source(s) every 15 min (settings {config.Path})", log[0]);
        Assert.Equal(["exco: 2026-10-09.md new", "1 written, 0 adopted, 0 untracked, 0 unchanged",
            "exco: 2026-10-16.md new", "1 written, 0 adopted, 0 untracked, 1 unchanged", "alive"], log[1..]);
        Assert.Empty(notified);
    }

    [Fact]
    public void A_lapsed_sign_in_is_notified_once_per_message()
    {
        using var temp = new Golden.TempFolder();
        var config = RunTests.Setup(temp);
        var graph = new ScriptedGraph(n => n switch
        {
            1 or 2 => throw new SignInNeededException("No cached sign-in. At a console, run: teams-channel-notes sign-in"),
            3 => throw new SignInNeededException("Sign-in failed: invalid_grant"),
            4 => throw new HttpRequestException(new string('x', 400)),
            _ => [],
        });
        var (host, log, notified) = Host(new Clock(), graph, ticks: 30 * 4 + 1);
        Watch.Run(config.Path, host);
        Assert.Equal(5, graph.Calls);
        Assert.Equal(2, notified.Count);
        Assert.StartsWith("Teams channel notes needs a sign-in: No cached sign-in", notified[0]);
        Assert.Equal(["sign-in needed: No cached sign-in. At a console, run: teams-channel-notes sign-in",
            "sign-in needed: No cached sign-in. At a console, run: teams-channel-notes sign-in",
            "sign-in needed: Sign-in failed: invalid_grant",
            "sync failed, will try again: " + new string('x', 300),
            "working again", "alive"], log[1..]);
    }

    [Fact]
    public void Settings_are_reloaded_when_they_change()
    {
        using var temp = new Golden.TempFolder();
        var config = RunTests.Setup(temp);
        var stamp = new DateTime(2026, 11, 12);
        var graph = new ScriptedGraph(_ => []);
        var clock = new Clock();
        var (host, log, _) = Host(clock, graph, ticks: 4, modified: _ => clock.Now > 1_000_040 ? stamp.AddDays(1) : stamp);
        Watch.Run(config.Path, host);
        Assert.Equal(["settings changed; reloaded"], log[1..]);
        Assert.Equal(2, graph.Calls);   // and it synced again at once
    }

    [Fact]
    public void A_second_watcher_exits_with_3()
    {
        using var temp = new Golden.TempFolder();
        var config = RunTests.Setup(temp);
        var ready = new ManualResetEventSlim();
        var done = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var held = Locks.SingleInstance(Run.WatchLockName(config));
            ready.Set();
            done.Wait();
        });
        holder.Start();
        ready.Wait(TestContext.Current.CancellationToken);
        var (host, log, _) = Host(new Clock(), new ScriptedGraph(_ => []), ticks: 1);
        Assert.Equal(3, Watch.Run(config.Path, host));
        Assert.Equal(["another watcher is already running for these settings; not starting a second"], log);
        done.Set();
        holder.Join();
    }
}
