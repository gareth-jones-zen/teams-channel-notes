using System.Text;
using System.Text.Json.Nodes;
using M365Graph;
using WinBackground;

namespace TeamsChannelNotes.Tests;

/// <summary>The command line: commands, output and exit codes, with Graph and Task Scheduler faked.</summary>
public class CliTests
{
    private sealed class FakeTask : ITask
    {
        public string Name { get; init; } = Autostart.TaskName;
        public List<string> Calls { get; } = [];
        public IReadOnlyDictionary<string, string>? State { get; set; }

        public string Enable(string exe, IReadOnlyList<string> args, string? workingDir)
        {
            Calls.Add($"enable {exe} {string.Join(' ', args)} in {workingDir}");
            return $"{exe} {Shell.ListToCommandLine(args)}";
        }

        public bool Disable()
        {
            Calls.Add("disable");
            return State is not null;
        }

        public string Enable(string exe, IReadOnlyList<string> args, string? workingDir, TaskSchedule schedule) =>
            throw new NotSupportedException("the watcher is enabled with the logon schedule only");

        public bool Stop() => false;

        public bool Start() => false;

        public IReadOnlyDictionary<string, string>? Status() => State;
    }

    private static (int Code, string Out, string Err) Cli(string[] args, IGraph? graph = null, FakeTask? task = null,
        Func<string, string?>? env = null, string? programDir = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var host = new CliHost
        {
            Out = output, Err = error,
            Env = env ?? Config.Environment,
            Graph = (_, _) => graph ?? new FakeGraph(_ => []),
            Tasks = _ => task ?? new FakeTask(),
            ProgramDir = programDir ?? AppContext.BaseDirectory,
        };
        var code = TeamsChannelNotes.Cli.Run(args, host);
        return (code, output.ToString(), error.ToString());
    }

    private static FakeGraph Posts() => new(_ => RunTests.Messages(
        ("ExCo Notes - 2026-10-09", "2026-10-12T08:00:00Z", "<ul><li>a</li></ul>"),
        ("ExCo Notes - 2026-10-02", "2026-10-05T08:00:00Z", "<ul><li>b</li></ul>")));

    [Fact]
    public void Sync_prints_its_log_and_summary_with_windows_newlines()
    {
        using var temp = new Golden.TempFolder();
        var config = RunTests.Setup(temp);
        var (code, output, error) = Cli(["--config", config.Path, "sync", "--dry"], Posts());
        Assert.Equal(0, code);
        Assert.Equal("exco: 2026-10-09.md new (dry run)\r\nexco: 2026-10-02.md new (dry run)\r\n2 written, 0 adopted, 0 untracked, 0 unchanged\r\n", output);
        Assert.Equal("", error);
        Assert.False(File.Exists(config.StateFile));
    }

    [Fact]
    public void Json_report_goes_to_stdout_and_the_log_to_stderr()
    {
        using var temp = new Golden.TempFolder();
        var config = RunTests.Setup(temp);
        var (code, output, error) = Cli(["--config=" + config.Path, "sync", "--json", "--source", "exco"], Posts());
        Assert.Equal(0, code);
        var report = new Report();
        report.Written.Add(Path.Combine(config.Sources[0].Folder, "2026-10-09.md"));
        report.Written.Add(Path.Combine(config.Sources[0].Folder, "2026-10-02.md"));
        Assert.Equal(report.ToJson().Replace("\n", "\r\n") + "\r\n", output);
        Assert.Equal("exco: 2026-10-09.md new\r\nexco: 2026-10-02.md new\r\n", error);
        Assert.Equal(0, Cli(["--config", config.Path, "adopt", "--json"], Posts()).Code);
    }

    [Fact]
    public void Exit_codes()
    {
        using var temp = new Golden.TempFolder();
        var missing = Path.Combine(temp.Path, "none.toml");
        var (code, _, error) = Cli(["--config", missing, "sync"]);
        Assert.Equal(2, code);
        Assert.Equal($"No settings file at {missing}. Run `teams-channel-notes config` to create one.\r\n", error);

        Assert.Equal(2, Cli([]).Code);
        Assert.Equal(2, Cli(["nosuch"]).Code);
        Assert.Equal(2, Cli(["sync", "--nosuch"]).Code);
        Assert.Equal(2, Cli(["sync", "--h"]).Code);   // ambiguous: --help or --hook
        Assert.Equal(2, Cli(["autostart"]).Code);
        Assert.Equal(2, Cli(["autostart", "maybe"]).Code);
        Assert.Equal(2, Cli(["ids"]).Code);
        Assert.Equal(0, Cli(["--help"]).Code);
        Assert.Equal(0, Cli(["--version"]).Code);

        var config = RunTests.Setup(temp);
        var signIn = new ThrowingGraph(new SignInNeededException("No cached sign-in. At a console, run: teams-channel-notes sign-in"));
        Assert.Equal((2, "", "No cached sign-in. At a console, run: teams-channel-notes sign-in\r\n"), Cli(["--config", config.Path, "sync"], signIn));
        Assert.Equal(1, Cli(["--config", config.Path, "sync"], new ThrowingGraph(new HttpRequestException("boom"))).Code);

        File.WriteAllText(config.Path, "[[source]]\nname = \"x\"\nteam = \"t\"\n", new UTF8Encoding(false));
        Assert.Equal((2, "", $"[[source]] x in {config.Path} has no channel, title, folder.\r\n"), Cli(["--config", config.Path, "check"]));
        File.WriteAllText(config.Path, "﻿x = 1\n", new UTF8Encoding(false));
        Assert.Equal(2, Cli(["--config", config.Path, "check"]).Code);
    }

    [Fact]
    public void Ids_from_a_link()
    {
        var (code, output, _) = Cli(["ids", "https://teams.microsoft.com/l/channel/19%3aabc%40thread.tacv2/N?groupId=g1&tenantId=t"]);
        Assert.Equal((0, "team = \"g1\"\r\nchannel = \"19:abc@thread.tacv2\"\r\n"), (code, output));
        Assert.Equal(2, Cli(["ids", "https://example.com/"]).Code);
    }

    [Fact]
    public void Config_creates_the_default_from_the_template()
    {
        using var temp = new Golden.TempFolder();
        string? Env(string name) => name == "APPDATA" ? temp.Path : null;
        var (code, output, _) = Cli(["config"], env: Env);
        var path = Path.Combine(temp.Path, "teams-channel-notes", "config.toml");
        Assert.Equal((0, path + "\r\n"), (code, output));
        Assert.Equal(Encoding.UTF8.GetBytes(Config.Template.ReplaceLineEndings("\n").Replace("\n", "\r\n")), File.ReadAllBytes(path));
        Assert.Contains("(?P<date>", File.ReadAllText(path));
        Assert.Equal((0, "given.toml\r\n", ""), Cli(["--config", "given.toml", "config"]));
    }

    [Fact]
    public void Template_matches_python()
    {
        var python = MiscTests.PythonSays("import sys; sys.path.insert(0, sys.argv[1]); from teams_channel_notes import config; "
                                          + "sys.stdout.write(config.TEMPLATE.encode('unicode_escape').decode())",
            Path.Combine(Golden.RepoRoot, "src"));
        Assert.SkipWhen(python is null, "python isn't here");
        var mine = string.Concat(Config.Template.ReplaceLineEndings("\n").Select(c => c switch
        {
            '\n' => "\\n",
            '\\' => "\\\\",
            > '~' => $"\\u{(int)c:x4}",
            _ => c.ToString(),
        }));
        Assert.Equal(python![0], mine);
    }

    [Fact]
    public void Sign_in_and_channels()
    {
        using var temp = new Golden.TempFolder();
        var config = RunTests.Setup(temp);
        var graph = new FakeGraph(url => url.StartsWith("/me/", StringComparison.Ordinal)
            ? [new JsonObject { ["id"] = "T1", ["displayName"] = "Team One" }]
            : [new JsonObject { ["id"] = "C1", ["displayName"] = "General" }]);
        Assert.Equal((0, "Signed in.\r\n", ""), Cli(["--config", config.Path, "sign-in"], graph));
        Assert.Equal(1, graph.Tokens);
        var (code, output, _) = Cli(["--config", config.Path, "channels"], graph);
        Assert.Equal(0, code);
        Assert.Equal("Team One\r\n  team = \"T1\"\r\n    General\r\n      channel = \"C1\"\r\n", output);
    }

    [Fact]
    public void Autostart_on_off_status()
    {
        using var temp = new Golden.TempFolder();
        var config = RunTests.Setup(temp);
        var task = new FakeTask();
        var programDir = Path.Combine(temp.Path, "bin");
        Directory.CreateDirectory(programDir);
        File.WriteAllText(Path.Combine(programDir, Autostart.BackgroundExe), "");
        var (code, output, _) = Cli(["--config", config.Path, "autostart", "on"], task: task, programDir: programDir);
        Assert.Equal(0, code);
        var exe = Path.Combine(programDir, Autostart.BackgroundExe);
        Assert.Equal($"enable {exe} --config {config.Path} watch in {temp.Path}", task.Calls.Single());
        Assert.Equal($"Autostart is on: 'teams-channel-notes watch' runs at every logon, and has started now.\r\n  {exe} --config {Shell.ListToCommandLine([config.Path])} watch\r\n  log: {config.LogFile}\r\n", output);

        Assert.Equal("Autostart was already off.\r\n", Cli(["autostart", "off"], task: task).Out);
        Assert.Equal("Autostart is off.\r\n", Cli(["autostart", "status"], task: task).Out);
        task.State = new Dictionary<string, string> { ["state"] = "Running", ["command"] = "x.exe", ["last_run"] = "today", ["last_result"] = "0" };
        Assert.Equal("Autostart is on ('teams-channel-notes watch').\r\n  state        Running\r\n  last run     today\r\n  last result  0\r\n  command      x.exe\r\n",
            Cli(["autostart", "status"], task: task).Out);
        Assert.Equal("Autostart is off.\r\n", Cli(["autostart", "off"], task: task).Out);
    }

    /// <summary>check's verdicts and diff, against Python's check() over the same files and posts.</summary>
    [Fact]
    public void Check_matches_python()
    {
        using var temp = new Golden.TempFolder();
        var config = RunTests.Setup(temp);
        var notes = config.Sources[0].Folder;
        Directory.CreateDirectory(notes);
        File.WriteAllBytes(Path.Combine(notes, "2026-10-09.md"), Encoding.UTF8.GetBytes("- a\r\n"));
        File.WriteAllBytes(Path.Combine(notes, "2026-10-02.md"), Encoding.UTF8.GetBytes("- b changed\r\n- extra\r\n"));
        var posts = RunTests.Messages(
            ("ExCo Notes - 2026-10-09", "2026-10-12T08:00:00Z", "<ul><li>a</li></ul>"),
            ("ExCo Notes - 2026-10-02", "2026-10-05T08:00:00Z", "<ul><li>b</li><li>c<ul><li>d</li></ul></li></ul>"),
            ("ExCo Notes - 2026-09-25", "2026-09-26T08:00:00Z", "<ul><li>e</li></ul>"));
        var (code, output, _) = Cli(["--config", config.Path, "check"], new FakeGraph(_ => (JsonArray)posts.DeepClone()));
        Assert.Equal(1, code);
        var python = MiscTests.PythonSays(
            "import sys,pathlib,json; sys.path.insert(0, sys.argv[1]); from teams_channel_notes import config as c, notes; "
            + "msgs = json.loads(sys.argv[3]); "
            + "notes.client = lambda config, interactive=None: type('G', (), {'get': lambda self, url, scopes=None: {'value': msgs}})(); "
            + "out = []; code = notes.check(c.load(pathlib.Path(sys.argv[2])), log=out.append); print(code); print('\\n'.join(out))",
            Path.Combine(Golden.RepoRoot, "src"), config.Path, posts.ToJsonString());
        Assert.SkipWhen(python is null, "python isn't here");
        Assert.Equal("1", python![0]);
        Assert.Equal(string.Join("\n", python[1..]) + "\n", output.Replace("\r\n", "\n"));
    }

    private sealed class ThrowingGraph(Exception error) : IGraph
    {
        public JsonObject Get(string url, IReadOnlyList<string> scopes) => throw error;

        public IEnumerable<JsonNode?> Pages(string url, IReadOnlyList<string> scopes) => throw error;

        public void Token(IReadOnlyList<string> scopes) => throw error;
    }
}
