using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PyCompat;
using Tomlyn;
using Tomlyn.Model;

namespace TeamsChannelNotes;

/// <summary>One channel, and the folder its posts become.</summary>
public sealed record Source(string Name, string Team, string Channel, Regex Title, string Folder, string Filename = "{date}.md",
    string Format = NoteFormats.Markdown, long Recent = 10);

/// <summary>The settings file: who signs in, and which channels become which folders of notes.
///
///     login_hint = "you@example.com"          # optional
///     tenant = "organizations"                # optional: a tenant id pins sign-in to one organisation
///     msal_cache = "..."                      # optional: share another tool's sign-in
///     state_file = "..."                      # optional
///     log_file = "..."                        # optional: where watch logs
///     after_run = ["python", "commit.py"]     # optional: run after a sync that wrote files (JSON on stdin)
///     watch_every_minutes = 15                # optional: how often watch syncs
///
///     [[source]]
///     name = "exco"
///     team = "&lt;team id&gt;"                      # `teams-channel-notes channels` lists them
///     channel = "&lt;channel id&gt;"
///     title = '(?i)ExCo Notes\s*[-–]\s*(?P&lt;date&gt;\d{4}-\d{2}-\d{2})'
///     folder = "exco-notes"                   # relative to this file
///     filename = "{date}.md"                  # named groups from `title`
///     format = "bullets"                      # bullets | markdown
///     recent = 10                             # how many of the newest posts to look at
///
/// Paths may use ~ and %VARIABLES%; relative paths are relative to the settings file. The same file the Python
/// version read, read the same way: paths come out as the same text, so the state, log and locks are shared.</summary>
public sealed class Config
{
    public static readonly string[] Scopes = ["ChannelMessage.Read.All"];
    public const string EnvConfig = "TEAMS_CHANNEL_NOTES_CONFIG";

    public const string Template = """
        # teams-channel-notes settings. See the README.
        # login_hint = "you@example.com"

        # One [[source]] per channel. `teams-channel-notes channels` lists your teams and channels with their ids.
        # [[source]]
        # name = "weekly-notes"
        # team = "<team id>"
        # channel = "<channel id>"
        # title = '(?i)Weekly notes\s*[-–]\s*(?P<date>\d{4}-\d{2}-\d{2})'
        # folder = "weekly-notes"
        # filename = "{date}.md"
        # format = "markdown"

        """;

    public required string Path { get; init; }
    public List<Source> Sources { get; } = [];
    public string? LoginHint { get; init; }
    public string Tenant { get; init; } = "organizations";
    public string? MsalCache { get; init; }
    public required string StateFile { get; init; }
    public required string LogFile { get; init; }
    public List<string> AfterRun { get; init; } = [];
    public long WatchEveryMinutes { get; init; } = 15;

    public static string? Environment(string name) =>
        string.IsNullOrEmpty(name) || name.Contains('\0') ? null : System.Environment.GetEnvironmentVariable(name);

    /// <summary>%APPDATA%\teams-channel-notes\config.toml (or under the home folder if APPDATA isn't set).</summary>
    public static string DefaultPath(Func<string, string?>? env = null)
    {
        env ??= Environment;
        var appData = env("APPDATA");
        return PyPath.Pure(System.IO.Path.Combine(string.IsNullOrEmpty(appData) ? PyPath.Home(env) : appData, "teams-channel-notes", "config.toml"));
    }

    /// <summary>%LOCALAPPDATA%\teams-channel-notes: the default state and log, and Python's lock folder.</summary>
    public static string LocalFolder(Func<string, string?>? env = null)
    {
        env ??= Environment;
        var local = env("LOCALAPPDATA");
        return PyPath.Pure(System.IO.Path.Combine(string.IsNullOrEmpty(local) ? PyPath.Home(env) : local, "teams-channel-notes"));
    }

    private static string PathSetting(string value, string folder, Func<string, string?> env)
    {
        var expanded = PyPath.Pure(PyPath.ExpandVars(PyPath.ExpandUser(value, env), env));
        return PyPath.IsAbsolute(expanded) ? expanded : PyPath.Resolve(System.IO.Path.Combine(folder, expanded));
    }

    /// <summary>12 hex characters from the settings path, ignoring case: the default state file's name.</summary>
    internal static string StateKey(string path) =>
        System.Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..12];

    /// <summary>load(path): the given path, else TEAMS_CHANNEL_NOTES_CONFIG, else the default. Throws
    /// FileNotFoundException if there's no file there, and PyValueError for settings that can't be used.</summary>
    public static Config Load(string? path = null, Func<string, string?>? env = null)
    {
        env ??= Environment;
        var given = !string.IsNullOrEmpty(path) ? path : env(EnvConfig) is { Length: > 0 } fromEnv ? fromEnv : DefaultPath(env);
        var full = PyPath.Resolve(given);
        if (!File.Exists(full))
            throw new FileNotFoundException($"No settings file at {full}. Run `teams-channel-notes config` to create one.");
        TomlTable raw;
        try
        {
            var text = PyText.ReadText(full);
            if (text.StartsWith('\ufeff'))
                throw new PyValueError("Invalid statement (at line 1, column 1)");   // tomllib refuses a byte order mark
            raw = TomlSerializer.Deserialize(text, TomlContext.Default.TomlTable) ?? [];
        }
        catch (Exception error) when (error is not (IOException or UnauthorizedAccessException or PyValueError))
        {
            throw new PyValueError($"{full}: {error.Message}");
        }
        var folder = System.IO.Path.GetDirectoryName(full)!;
        var local = LocalFolder(env);
        string? Text(string key) => raw.TryGetValue(key, out var value) ? Str(value, key) : null;
        string? OptionalPath(string key) => Text(key) is { Length: > 0 } value ? PathSetting(value, folder, env) : null;

        var config = new Config
        {
            Path = full,
            LoginHint = Text("login_hint"),
            Tenant = Text("tenant") ?? "organizations",
            MsalCache = OptionalPath("msal_cache"),
            StateFile = OptionalPath("state_file") ?? PyPath.Pure(System.IO.Path.Combine(local, $"state-{StateKey(full)}.json")),
            LogFile = OptionalPath("log_file") ?? PyPath.Pure(System.IO.Path.Combine(local, "teams-channel-notes.log")),
            AfterRun = raw.TryGetValue("after_run", out var hook) ? Strings(hook, "after_run") : [],
            WatchEveryMinutes = Math.Max(1, raw.TryGetValue("watch_every_minutes", out var every) ? Int(every, "watch_every_minutes") : 15),
        };

        if (raw.TryGetValue("source", out var sources))
        {
            if (sources is not TomlTableArray tables)
                throw new PyValueError($"{full}: source must be a list of [[source]] tables.");
            var i = 0;
            foreach (var item in tables)
            {
                i++;
                var label = item.TryGetValue("name", out var named) ? Str(named, "name") : i.ToString(CultureInfo.InvariantCulture);
                var missing = new[] { "team", "channel", "title", "folder" }.Where(key => !Truthy(item, key)).ToList();
                if (missing.Count > 0)
                    throw new PyValueError($"[[source]] {label} in {full} has no {string.Join(", ", missing)}.");
                var format = item.TryGetValue("format", out var f) ? Str(f, "format") : NoteFormats.Markdown;
                if (!NoteFormats.Names.Contains(format))
                    throw new PyValueError($"[[source]] {label}: format must be one of {string.Join(", ", NoteFormats.Names)}.");
                var title = PyRegex.Compile(Str(item["title"], "title"));
                var filename = item.TryGetValue("filename", out var fn) ? Str(fn, "filename") : "{date}.md";
                var groups = PyRegex.GroupNames(title);
                foreach (var field in PyFormat.Fields(filename))
                    if (!groups.Contains(field))
                        throw new PyValueError($"[[source]] {label}: filename uses {{{field}}}, but title has no group named {field}.");
                config.Sources.Add(new Source(
                    Name: item.TryGetValue("name", out var n) ? Str(n, "name") : $"source{i}",
                    Team: Str(item["team"], "team"),
                    Channel: Str(item["channel"], "channel"),
                    Title: title,
                    Folder: PathSetting(Str(item["folder"], "folder"), folder, env),
                    Filename: filename,
                    Format: format,
                    Recent: item.TryGetValue("recent", out var recent) ? Int(recent, "recent") : 10));
            }
        }
        return config;
    }

    /// <summary>create_default(): the default settings file, from the template if it isn't there yet.</summary>
    public static string CreateDefault(Func<string, string?>? env = null)
    {
        var path = DefaultPath(env);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            PyStr.WriteText(path, Template.ReplaceLineEndings("\n"));
        }
        return path;
    }

    private static bool Truthy(TomlTable table, string key) =>
        table.TryGetValue(key, out var value) && value switch
        {
            null => false,
            string s => s.Length > 0,
            bool b => b,
            long l => l != 0,
            double d => d != 0,
            TomlArray a => a.Count > 0,
            TomlTable t => t.Count > 0,
            _ => true,
        };

    /// <summary>Python's str() of a setting, for the settings that are text.</summary>
    private static string Str(object? value, string key) => value switch
    {
        string s => s,
        long l => l.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "True" : "False",
        _ => throw new PyValueError($"{key} must be text."),
    };

    /// <summary>Python's int() of a setting: whole numbers, decimals truncated, or digits in text.</summary>
    private static long Int(object? value, string key) => value switch
    {
        long l => l,
        double d when double.IsFinite(d) => (long)Math.Truncate(d),
        bool b => b ? 1 : 0,
        string s when long.TryParse(PyStr.Strip(s), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => throw new PyValueError($"{key} must be a whole number."),
    };

    private static List<string> Strings(object? value, string key) => value switch
    {
        TomlArray array => array.Select(item => Str(item, key)).ToList(),
        _ => throw new PyValueError($"{key} must be a list, as [\"python\", \"commit.py\"]."),
    };
}

[Tomlyn.Serialization.TomlSerializable(typeof(TomlTable))]
internal partial class TomlContext : Tomlyn.Serialization.TomlSerializerContext;
