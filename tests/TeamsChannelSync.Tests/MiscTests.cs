using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using PyCompat;

namespace TeamsChannelSync.Tests;

/// <summary>Links, keys, title patterns, timestamps, the report's JSON and the hook's payload, against Python's
/// answers (tests/parity/misc_goldens.py).</summary>
public class MiscTests
{
    private static readonly JsonNode Data = JsonNode.Parse(File.ReadAllText(Golden.Committed("misc.json")))!;

    [Fact]
    public void Links_match_python()
    {
        foreach (var item in Data["links"]!.AsArray())
        {
            var link = (string)item!["link"]!;
            if (item["error"] is { } error)
                Assert.Equal((string)error!, Assert.Throws<PyValueError>(() => Notes.FromLink(link)).Message);
            else if (item["crash"] is not null)
                Assert.Throws<IndexOutOfRangeException>(() => Notes.FromLink(link));
            else
                Assert.Equal(((string)item["team"]!, (string)item["channel"]!), Notes.FromLink(link));
        }
    }

    [Fact]
    public void Lock_and_state_keys_match_python()
    {
        foreach (var item in Data["keys"]!.AsArray())
        {
            var path = (string)item!["path"]!;
            Assert.Equal((string)item["lock"]!, WinBackground.Locks.Key(path));
            Assert.Equal((string)item["state"]!, Config.StateKey(path));
        }
    }

    [Fact]
    public void Titles_match_python()
    {
        foreach (var item in Data["titles"]!.AsArray())
        {
            var regex = PyRegex.Compile((string)item!["pattern"]!);
            var subject = (string)item["subject"]!;
            var match = regex.Match(subject);
            if (item["groups"] is not JsonObject groups)
            {
                Assert.False(match.Success, $"{regex} on {subject}");
                continue;
            }
            Assert.True(match.Success, $"{regex} on {subject}");
            foreach (var (name, value) in groups)
                Assert.Equal((string?)value, match.Groups[name].Success ? match.Groups[name].Value : null);
            Assert.Equal(groups.Select(g => g.Key).Order(), PyRegex.GroupNames(regex).Order());
        }
    }

    [Theory]
    [InlineData(@"(?P<a>x)(?P=a)\Z", @"(?<a>x)\k<a>\z")]
    [InlineData(@"a{,3}[\s]\s\S", @"a{0,3}[\s\x1c-\x1f][\s\x1c-\x1f][^\s\x1c-\x1f]")]
    [InlineData(@"(?u)x(?iu:y)", @"x(?i:y)")]
    [InlineData(@"[]a][^]b]", @"[\]a][^\]b]")]
    [InlineData(@"[^\S][\S]", @"[\s\x1c-\x1f][^\s\x1c-\x1f]")]
    [InlineData(@"[a\S]+[^a\S]", @"(?:[a]|[^\s\x1c-\x1f])+(?:(?![a])[\s\x1c-\x1f])")]
    public void Python_regex_is_translated(string python, string dotnet) => Assert.Equal(dotnet, PyRegex.Translate(python));

    [Theory]
    [InlineData(@"a*+")]
    [InlineData(@"(?a)x")]
    [InlineData(@"\N{EM DASH}")]
    [InlineData(@"[a-[b]]")]
    public void Python_only_regex_is_refused(string python) => Assert.Throws<PyValueError>(() => PyRegex.Compile(python));

    [Fact]
    public void Timestamps_compare_as_python()
    {
        foreach (var item in Data["stamps"]!.AsArray())
            Assert.Equal((int)item!["compare"]!, Math.Sign(IsoTime.Compare((string)item["a"]!, (string)item["b"]!)));
        Assert.Throws<PyValueError>(() => IsoTime.Compare("", "2026-10-12T08:22:23Z"));
        Assert.Throws<InvalidOperationException>(() => IsoTime.Compare("2026-10-12T08:22:23", "2026-10-12T08:22:23Z"));
    }

    [Fact]
    public void Report_json_summary_and_hook_payload_match_python()
    {
        var golden = Data["report"]!;
        var report = new Report { Unchanged = (int)golden["report"]!["unchanged"]! };
        report.Written.AddRange(golden["report"]!["written"]!.AsArray().Select(n => (string)n!));
        report.Untracked.AddRange(golden["report"]!["untracked"]!.AsArray().Select(n => (string)n!));
        Assert.Equal((string)golden["json"]!, report.ToJson());
        Assert.Equal((string)golden["summary"]!, report.Summary());
        var config = new Config { Path = (string)golden["config"]!, StateFile = "s", LogFile = "l" };
        Assert.Equal((string)golden["payload"]!, Run.Payload(config, report));
        Assert.Equal("{}", State.Dump(new Dictionary<string, JsonNode?>()));
    }

    [Fact]
    public void Filename_templates()
    {
        Assert.Equal(["date"], PyFormat.Fields("{date}.md"));
        Assert.Equal("{2026}-x}.md", PyFormat.Format("{{{y}}}-x}}.md", new Dictionary<string, string> { ["y"] = "2026" }));
        Assert.Throws<PyValueError>(() => PyFormat.Fields("{0}.md"));
        Assert.Throws<PyValueError>(() => PyFormat.Fields("{date!r}.md"));
        Assert.Throws<PyValueError>(() => PyFormat.Fields("a}b"));
    }

    // ---- on GJ's laptop only: the real settings and state, read and never written ----------------------------

    private static readonly string EaToml = Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? "",
        "OneDrive - Zen Internet", "claude-executive-assistant", "tools", "teams-channel-sync.toml");

    [Fact]
    public void Real_state_keys_are_the_ones_python_stored()
    {
        Assert.SkipUnless(File.Exists(EaToml), "not on GJ's laptop");
        var before = File.GetLastWriteTimeUtc(EaToml);
        var config = Config.Load(EaToml);
        Assert.True(File.Exists(config.StateFile), config.StateFile);
        var stateBefore = File.ReadAllBytes(config.StateFile);
        var state = new State(config.StateFile);
        Assert.NotEmpty(state.Data);
        var folder = config.Sources.Single().Folder;
        foreach (var key in state.Data.Keys)
        {
            var file = Notes.Join(folder, Path.GetFileName(key));
            Assert.Equal(key, State.Key(file));
            Assert.NotNull(state.Get(file));
        }
        // the state, re-written by .NET, is the same bytes Python wrote (not written to disk: compared in memory)
        Assert.Equal(stateBefore, Encoding.UTF8.GetBytes(PyStr.WindowsNewlines(State.Dump(state.Data))));
        Assert.Equal(before, File.GetLastWriteTimeUtc(EaToml));
        Assert.Equal(stateBefore, File.ReadAllBytes(config.StateFile));
    }

    [Fact]
    public void Real_lock_key_and_paths_match_python()
    {
        Assert.SkipUnless(File.Exists(EaToml), "not on GJ's laptop");
        var python = PythonSays(
            "import sys,pathlib; sys.path.insert(0, sys.argv[1]); from teams_channel_notes import config as c; "
            + "from win_background import key; x = c.load(pathlib.Path(sys.argv[2])); "
            + "print('\\n'.join([str(x.path), key(x.path), str(x.state_file), str(x.log_file), str(x.msal_cache), "
            + "str(x.sources[0].folder), ' '.join(x.after_run), str(x.watch_every_minutes)]))",
            Path.Combine(Golden.RepoRoot, "src"), EaToml);
        Assert.SkipWhen(python is null, "python isn't here");
        var config = Config.Load(EaToml);
        string[] mine = [config.Path, WinBackground.Locks.Key(config.Path), config.StateFile, config.LogFile, config.MsalCache ?? "None",
            config.Sources[0].Folder, string.Join(' ', config.AfterRun), config.WatchEveryMinutes.ToString()];
        Assert.Equal(python!, mine);
    }

    [Fact]
    public void Resolve_matches_pathlib()
    {
        using var temp = new Golden.TempFolder();
        Directory.CreateDirectory(Path.Combine(temp.Path, "Mixed Case"));
        string[] paths = [temp.Path, Path.Combine(temp.Path, "mixed case", "missing", "..", "x.md"), Path.Combine(temp.Path, "MIXED CASE"),
            Path.GetTempPath(), Path.Combine(temp.Path, "nope", "deeper", "file.toml")];
        var python = PythonSays("import sys,pathlib; print('\\n'.join(str(pathlib.Path(p).resolve()) for p in sys.argv[1:]))", paths);
        Assert.SkipWhen(python is null, "python isn't here");
        Assert.Equal(python!, paths.Select(PyPath.Resolve).ToList());
    }

    internal static List<string>? PythonSays(string code, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo("python") { RedirectStandardOutput = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8 };
            info.Environment["PYTHONIOENCODING"] = "utf-8";
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(code);
            foreach (var arg in args)
                info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList() : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
