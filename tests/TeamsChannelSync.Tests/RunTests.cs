using System.Text;
using System.Text.Json.Nodes;
using WinBackground;

namespace TeamsChannelSync.Tests;

/// <summary>The run lock, the after-run hook, and watch with a fake clock.</summary>
public class RunTests
{
    internal const string Title = "(?i)ExCo Notes\\s*[-\u2013]\\s*(?P<date>\\d{4}-\\d{2}-\\d{2})";

    internal static string Settings(string extra = "") =>
        $"state_file = \"state.json\"\nlog_file = \"watch.log\"\n{extra}\n[[source]]\nname = \"exco\"\nteam = \"team-1\"\n"
        + $"channel = \"c\"\ntitle = '{Title}'\nfolder = \"notes\"\nformat = \"bullets\"\n";

    internal static JsonArray Messages(params (string Subject, string Modified, string Body)[] posts) =>
        new(posts.Select((p, i) => (JsonNode?)new JsonObject
        {
            ["id"] = i.ToString(), ["subject"] = p.Subject, ["messageType"] = "message",
            ["lastModifiedDateTime"] = p.Modified, ["body"] = new JsonObject { ["content"] = p.Body },
        }).ToArray());

    internal static Config Setup(Golden.TempFolder temp, string extra = "")
    {
        var path = Path.Combine(temp.Path, "settings.toml");
        File.WriteAllText(path, Settings(extra), new UTF8Encoding(false));
        return Config.Load(path);
    }

    [Fact]
    public void Hook_runs_only_when_something_was_written()
    {
        using var temp = new Golden.TempFolder();
        var config = Setup(temp, "after_run = [\"python\", \"hook.py\"]");
        var calls = new List<(IReadOnlyList<string> Args, string Input, string Cwd)>();
        Completed Runner(IReadOnlyList<string> args, string input, string cwd, TimeSpan timeout)
        {
            calls.Add((args, input, cwd));
            return new Completed(3, "out one\n\n  out two  \n", "err\n");
        }
        var log = new List<string>();
        var graph = new FakeGraph(_ => Messages(("ExCo Notes - 2026-10-09", "2026-10-12T08:00:00Z", "<ul><li>a</li></ul>")));
        Run.Once(config, graph, hook: true, log: log.Add, runner: Runner);
        Assert.Single(calls);
        Assert.Equal(["python", "hook.py"], calls[0].Args);
        Assert.Equal(Path.GetDirectoryName(config.Path), calls[0].Cwd);
        Assert.Equal(Run.Payload(config, new Report { Written = { Path.Combine(config.Sources[0].Folder, "2026-10-09.md") } }), calls[0].Input);
        Assert.Equal(["exco: 2026-10-09.md new", "after-run hook: python hook.py", "  hook: out one", "  hook: out two",
            "  hook: err", "after-run hook exited 3"], log);

        Run.Once(config, graph, hook: true, log: log.Add, runner: Runner);   // nothing new: no hook
        Run.Once(config, new FakeGraph(_ => Messages(("ExCo Notes - 2026-10-16", "2026-10-19T08:00:00Z", "<ul><li>b</li></ul>"))),
            dryRun: true, hook: true, log: log.Add, runner: Runner);       // dry run: no hook
        Run.Once(config, new FakeGraph(_ => Messages(("ExCo Notes - 2026-10-16", "2026-10-19T08:00:00Z", "<ul><li>b</li></ul>"))),
            hook: false, log: log.Add, runner: Runner);                    // not asked for
        Assert.Single(calls);
    }

    [Fact]
    public void Hook_that_cant_start_is_logged_as_python_did()
    {
        using var temp = new Golden.TempFolder();
        var config = Setup(temp, "after_run = [\"no-such-program-tcn\"]");
        var log = new List<string>();
        Run.AfterRun(config, new Report(), log.Add);
        Assert.Equal("after-run hook failed: [WinError 2] The system cannot find the file specified", log[^1]);
    }

    /// <summary>A hook that runs past its timeout is logged in subprocess.TimeoutExpired's own words (WinBackground's
    /// ShellTimeoutException.PythonMessage), checked against what Python itself says for the same command.</summary>
    [Fact]
    public void Hook_timeout_is_logged_as_python_did()
    {
        using var temp = new Golden.TempFolder();
        var config = Setup(temp, "after_run = [\"hook.exe\", \"it's\", \"-x\"]");
        var python = MiscTests.PythonSays(
            "import subprocess,sys; print(str(subprocess.TimeoutExpired(sys.argv[1:], 1800)))",
            [.. config.AfterRun]);
        Assert.SkipWhen(python is null, "python isn't here");
        var log = new List<string>();
        Run.AfterRun(config, new Report(), log.Add,
            (args, _, _, timeout) => throw new ShellTimeoutException(args[0], [.. args.Skip(1)], timeout));
        Assert.Equal("after-run hook failed: " + python!.Single(), log[^1]);
    }

    /// <summary>The hook's stdin, byte for byte: Python's run.after_run and the .NET one each run a script that
    /// saves what it was given.</summary>
    [Fact]
    public void Hook_payload_bytes_match_python()
    {
        using var temp = new Golden.TempFolder();
        File.WriteAllText(Path.Combine(temp.Path, "save.py"),
            "import sys\nopen(sys.argv[1], 'wb').write(sys.stdin.buffer.read())\nsys.stdout.buffer.write('saved \\u2713\\r\\n'.encode('utf-8'))\n", new UTF8Encoding(false));
        var config = Setup(temp, "after_run = [\"python\", \"save.py\", \"OUT\"]");
        var written = new List<string> { Path.Combine(config.Sources[0].Folder, "2026-10-09.md"), Path.Combine(temp.Path, "Caf\u00e9 & 'x'.md") };
        var python = MiscTests.PythonSays(
            "import sys,pathlib,json; sys.path.insert(0, sys.argv[1]); from teams_channel_notes import config as c, run; "
            + "x = c.load(pathlib.Path(sys.argv[2])); x.after_run[2] = sys.argv[3]; lines = []; "
            + "run.after_run(x, {'written': json.loads(sys.argv[4]), 'adopted': [], 'untracked': [], 'unchanged': 2}, lines.append); "
            + "print('\\n'.join(lines))",
            Path.Combine(Golden.RepoRoot, "src"), config.Path, Path.Combine(temp.Path, "python.bin"), PyJson.StringList(written));
        Assert.SkipWhen(python is null, "python isn't here");

        var dotnetConfig = new Config
        {
            Path = config.Path, StateFile = config.StateFile, LogFile = config.LogFile,
            AfterRun = ["python", "save.py", Path.Combine(temp.Path, "dotnet.bin")],
        };
        var report = new Report { Unchanged = 2 };
        report.Written.AddRange(written);
        var log = new List<string>();
        Run.AfterRun(dotnetConfig, report, log.Add);
        Assert.Equal(File.ReadAllBytes(Path.Combine(temp.Path, "python.bin")), File.ReadAllBytes(Path.Combine(temp.Path, "dotnet.bin")));
        Assert.Equal(python![1..], log[1..]);   // the first line names the output file, which differs
        Assert.Equal("  hook: saved \u2713", log[^1]);
    }

    /// <summary>WinBackground's NamedLock is a mutex, which only the thread that took it can release. The sync is
    /// synchronous (Graph calls block on their tasks), so the lock is taken and released on one thread even when
    /// Graph awaits inside; after a run another thread can take it at once.</summary>
    [Fact]
    public void Run_lock_is_released_after_a_sync_that_awaited()
    {
        using var temp = new Golden.TempFolder();
        var config = Setup(temp);
        var graph = new AwaitingGraph();
        Run.Once(config, graph);
        Assert.True(graph.Awaited);
        var taken = false;
        var other = new Thread(() =>
        {
            using var held = new NamedLock(Run.RunLockName(config));
            taken = held.Acquire(0);
        });
        other.Start();
        other.Join();
        Assert.True(taken);
    }

    [Fact]
    public void A_held_run_lock_times_out_with_pythons_message()
    {
        using var temp = new Golden.TempFolder();
        var config = Setup(temp);
        using var held = new NamedLock(Run.RunLockName(config));
        var ready = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var mine = new NamedLock(Run.RunLockName(config));
            mine.Acquire(0);
            ready.Set();
            release.Wait();
        });
        holder.Start();
        ready.Wait(TestContext.Current.CancellationToken);
        Assert.False(held.Acquire(0.05));
        release.Set();
        holder.Join();
        Assert.Equal($"teams-channel-sync-run-{Locks.Key(config.Path)}", Run.RunLockName(config));
        Assert.Equal($"teams-channel-sync-watch-{Locks.Key(config.Path)}", Run.WatchLockName(config));
    }

    private sealed class AwaitingGraph : IGraph
    {
        public bool Awaited { get; private set; }

        public JsonObject Get(string url, IReadOnlyList<string> scopes)
        {
            var thread = Environment.CurrentManagedThreadId;
            var result = Task.Run(async () =>
            {
                await Task.Yield();
                await Task.Delay(20);
                return new JsonObject { ["value"] = Messages(("ExCo Notes - 2026-10-09", "2026-10-12T08:00:00Z", "<ul><li>a</li></ul>")) };
            }).GetAwaiter().GetResult();
            Awaited = thread == Environment.CurrentManagedThreadId;
            return result;
        }

        public IEnumerable<JsonNode?> Pages(string url, IReadOnlyList<string> scopes) => [];

        public void Token(IReadOnlyList<string> scopes)
        {
        }
    }
}
