using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PyCompat;

namespace TeamsChannelNotes.Tests;

/// <summary>Graph replaced by canned replies, keyed by the team in the URL. Records what was asked for.</summary>
public sealed class FakeGraph(Func<string, JsonArray> messages) : IGraph
{
    public List<string> Urls { get; } = [];
    public int Tokens { get; private set; }

    public JsonObject Get(string url, IReadOnlyList<string> scopes)
    {
        Urls.Add(url);
        return new JsonObject { ["value"] = messages(url.Split('/')[2]) };
    }

    public IEnumerable<JsonNode?> Pages(string url, IReadOnlyList<string> scopes)
    {
        Urls.Add(url);
        return messages(url).Select(n => n?.DeepClone());
    }

    public void Token(IReadOnlyList<string> scopes) => Tokens++;
}

/// <summary>The sync scenarios Python ran (tests/parity/scenarios.py): the same files, state bytes, report, log
/// lines and Graph calls, step by step.</summary>
public class ScenarioTests
{
    private static readonly JsonNode Data = JsonNode.Parse(File.ReadAllText(Golden.Committed("scenarios.json")))!;

    public static TheoryData<string> Names() =>
        new(Data["scenarios"]!.AsArray().Select(s => (string)s!["name"]!));

    [Theory]
    [MemberData(nameof(Names))]
    public void Scenario_matches_python(string name)
    {
        var scenario = Data["scenarios"]!.AsArray().First(s => (string)s!["name"]! == name)!;
        using var temp = new Golden.TempFolder();
        var root = PyPath.Resolve(temp.Path);
        File.WriteAllText(Path.Combine(root, "settings.toml"), (string)scenario["settings"]!, new UTF8Encoding(false));
        if (scenario["files"] is JsonObject files)
            foreach (var (rel, data) in files)
                Write(Path.Combine(root, rel), System.Convert.FromBase64String((string)data!));
        var statePath = Path.Combine(root, "state.json");
        if (scenario["state"] is JsonValue state)
            File.WriteAllBytes(statePath, System.Convert.FromBase64String((string)state!));

        var steps = scenario["steps"]!.AsArray();
        var expected = scenario["expected"]!.AsArray();
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i]!;
            var want = expected[i]!;
            var config = Config.Load(Path.Combine(root, "settings.toml"));
            var messages = step["messages"]!.AsObject();
            var graph = new FakeGraph(team => messages[team] is JsonArray list ? (JsonArray)list.DeepClone() : []);
            var log = new List<string>();
            var args = step["args"];
            var report = Notes.Sync(config, graph, dryRun: (bool?)args?["dry_run"] ?? false, adopt: (bool?)args?["adopt"] ?? false,
                only: (string?)args?["only"], log: log.Add);

            var where = $"{name} step {i + 1}";
            Assert.Equal(Strings(want["urls"]), graph.Urls);
            Assert.Equal(Strings(want["log"]).Select(l => l.Replace("{ROOT}", root)), log);
            var wantReport = want["report"]!;
            Assert.Equal(Paths(wantReport["written"], root), report.Written);
            Assert.Equal(Paths(wantReport["adopted"], root), report.Adopted);
            Assert.Equal(Paths(wantReport["untracked"], root), report.Untracked);
            Assert.Equal((int)wantReport["unchanged"]!, report.Unchanged);

            var actualFiles = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(p => p != statePath && Path.GetFileName(p) != "settings.toml")
                .ToDictionary(p => Path.GetRelativePath(root, p).Replace('\\', '/'), p => System.Convert.ToBase64String(File.ReadAllBytes(p)));
            var wantFiles = want["files"]!.AsObject().ToDictionary(p => p.Key, p => (string)p.Value!);
            Assert.Equal(wantFiles.OrderBy(p => p.Key, StringComparer.Ordinal), actualFiles.OrderBy(p => p.Key, StringComparer.Ordinal));

            var wantState = (string?)want["state"];
            if (wantState is null)
                Assert.False(File.Exists(statePath), where);
            else
            {
                var escaped = Escaped(root);
                var actual = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(statePath))
                    .Replace(escaped, "{ROOT}").Replace(escaped.ToLowerInvariant(), "{root}");
                Assert.Equal(wantState, actual);
            }
        }
    }

    internal static string Escaped(string text)
    {
        var builder = new StringBuilder();
        PyJson.Quote(builder, text);
        return builder.ToString()[1..^1];
    }

    private static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static List<string> Strings(JsonNode? node) => node!.AsArray().Select(n => (string)n!).ToList();

    private static List<string> Paths(JsonNode? node, string root) => Strings(node).Select(p => p.Replace("{ROOT}", root)).ToList();
}
