using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PyCompat;

namespace TeamsChannelNotes;

/// <summary>A post that becomes a note: where it goes, Graph's lastModifiedDateTime, and the converted text.</summary>
public sealed record Post(Source Source, string Path, string Modified, string Text);

/// <summary>What a sync did: files written, files adopted, files left alone, and how many were unchanged.</summary>
public sealed class Report
{
    public List<string> Written { get; } = [];
    public List<string> Adopted { get; } = [];
    public List<string> Untracked { get; } = [];
    public int Unchanged { get; set; }

    /// <summary>The summary line: "1 written, 0 adopted, 0 untracked, 9 unchanged".</summary>
    public string Summary() =>
        $"{Written.Count} written, {Adopted.Count} adopted, {Untracked.Count} untracked, {Unchanged} unchanged";

    /// <summary>json.dumps(report, indent=1), as `--json` prints it (with "\n" newlines).</summary>
    public string ToJson()
    {
        static string List(List<string> items) =>
            items.Count == 0 ? "[]" : "[\n  " + string.Join(",\n  ", items.Select(Quote)) + "\n ]";
        return "{\n \"written\": " + List(Written) + ",\n \"adopted\": " + List(Adopted) + ",\n \"untracked\": "
               + List(Untracked) + ",\n \"unchanged\": " + Unchanged.ToString(CultureInfo.InvariantCulture) + "\n}";
    }

    private static string Quote(string text)
    {
        var builder = new System.Text.StringBuilder();
        PyJson.Quote(builder, text);
        return builder.ToString();
    }
}

/// <summary>Fetch a channel's recent posts and keep a folder of notes in step with them.
///
/// The rules, from filing a weekly ExCo notes post by hand and then automatically:
/// - A post is named by the date in its title ("ExCo Notes - 2026-10-09"), never the date it went up. A post whose
///   title doesn't match (a notice such as "No meeting this week", or a reply, which has no title) gets no file.
/// - Authors edit posts after publishing. Each file's state records the post's lastModifiedDateTime when it was
///   written; a post modified since, whose text now differs, replaces the file.
/// - Files the tool didn't write are never overwritten. A file that exists with no state (copied in by hand, say)
///   is taken on only if it matches the post, ignoring whitespace. Otherwise it's reported as untracked, and
///   `adopt` takes it on as it stands, so later edits to the post replace it.</summary>
public static class Notes
{
    /// <summary>The posts in a source's newest <c>recent</c> that have a matching title, newest activity first
    /// (Graph's order).</summary>
    public static List<Post> Posts(IGraph graph, Source source, Action<string>? log = null)
    {
        var url = $"/teams/{source.Team}/channels/{source.Channel}/messages?$top={source.Recent}";
        var found = new List<Post>();
        var reply = graph.Get(url, Config.Scopes);
        if (!reply.TryGetPropertyValue("value", out var value) || value is not JsonArray messages)
            return found;
        foreach (var node in messages)
        {
            if (node is not JsonObject message)
                continue;
            var subject = Text(message["subject"]) ?? "";
            var match = source.Title.Match(subject);
            if (Text(message["messageType"]) != "message" || Truthy(message["deletedDateTime"]) || !match.Success)
                continue;
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            string? missing = null;
            foreach (var name in PyRegex.GroupNames(source.Title))
            {
                var group = match.Groups[name];
                if (group.Success)
                    fields[name] = group.Value;
                else
                    missing ??= name;
            }
            if (missing is not null && PyFormat.Fields(source.Filename).Contains(missing))
            {
                // Python would have named the file "None" here; never seen, and better skipped (decision 4)
                log?.Invoke($"{source.Name}: \"{subject}\" matches the title, but its {missing} group is empty; no file");
                continue;
            }
            var name2 = PyFormat.Format(source.Filename, fields);
            var content = message["body"] is JsonObject body ? Text(body["content"]) ?? "" : "";
            var text = NoteFormats.Convert(source.Format, content);
            found.Add(new Post(source, Join(source.Folder, name2), Text(message["lastModifiedDateTime"]) ?? "", text));
        }
        return found;
    }

    /// <summary>folder / name, as pathlib joins them.</summary>
    internal static string Join(string folder, string name)
    {
        var n = name.Replace('/', '\\');
        if (PyPath.IsAbsolute(n))
            return PyPath.Pure(n);
        if (n.StartsWith('\\'))
            return PyPath.Pure(Path.GetPathRoot(folder)!.TrimEnd('\\') + n);
        return PyPath.Pure(folder + "\\" + n);
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>().Length > 0,
            JsonValueKind.False or JsonValueKind.Null => false,
            JsonValueKind.Number => v.ToJsonString() is not ("0" or "0.0" or "-0" or "-0.0"),
            _ => true,
        },
        JsonObject o => o.Count > 0,
        JsonArray a => a.Count > 0,
        _ => true,
    };

    private static bool Same(string a, string b) => PyStr.Collapse(a) == PyStr.Collapse(b);

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>Write new and edited notes. adopt takes on untracked files as they stand, writing nothing.</summary>
    public static Report Sync(Config config, IGraph graph, bool dryRun = false, bool adopt = false, string? only = null,
        Action<string>? log = null)
    {
        log ??= _ => { };
        var state = new State(config.StateFile);
        var report = new Report();
        var dry = dryRun ? " (dry run)" : "";
        foreach (var source in config.Sources)
        {
            if (!string.IsNullOrEmpty(only) && source.Name != only)
                continue;
            foreach (var post in Posts(graph, source, log))
            {
                var path = post.Path;
                var recorded = state.Get(path);
                var file = Path.GetFileName(path);
                if (!Exists(path))
                {
                    if (adopt)
                        continue;
                    log($"{source.Name}: {file} new{dry}");
                    if (!dryRun)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        PyStr.WriteText(path, post.Text);
                        state.Set(path, post.Modified);
                    }
                    report.Written.Add(path);
                    continue;
                }
                var old = PyText.ReadText(path);
                if (recorded is null)
                {
                    if (Same(old, post.Text) || adopt)
                    {
                        state.Set(path, post.Modified);
                        report.Adopted.Add(path);
                        if (adopt && !Same(old, post.Text))
                            log($"{source.Name}: {file} adopted as it stands (differs from the post)");
                    }
                    else
                    {
                        log($"{source.Name}: {file} exists but wasn't written by this tool and differs from the "
                            + "post; left alone (`adopt` takes it on)");
                        report.Untracked.Add(path);
                    }
                    continue;
                }
                if (adopt || IsoTime.Compare(post.Modified, recorded) <= 0 || Same(old, post.Text))
                {
                    if (!adopt && IsoTime.Compare(post.Modified, recorded) > 0)
                        state.Set(path, post.Modified);   // edited, but the text we keep didn't change
                    report.Unchanged++;
                    continue;
                }
                log($"{source.Name}: {file} edited since filed (post modified {post.Modified}){dry}");
                if (!dryRun)
                {
                    PyStr.WriteText(path, post.Text);
                    state.Set(path, post.Modified);
                }
                report.Written.Add(path);
            }
        }
        if (!dryRun)
            state.Save();
        return report;
    }

    /// <summary>Convert the recent posts and compare them with the files, line by line. 1 if any differ.</summary>
    public static int Check(Config config, IGraph graph, Action<string> log)
    {
        var differ = 0;
        foreach (var source in config.Sources)
        {
            foreach (var post in Posts(graph, source, log))
            {
                var file = Path.GetFileName(post.Path);
                if (!Exists(post.Path))
                {
                    log($"{source.Name}: {file}: no file");
                    continue;
                }
                var old = PyText.ReadText(post.Path).Replace("\r\n", "\n");
                if (old == post.Text)
                {
                    log($"{source.Name}: {file}: identical");
                    continue;
                }
                differ++;
                log($"{source.Name}: {file}: DIFFERS");
                log(UnifiedDiff.Of(PyStr.SplitLinesKeepEnds(old), PyStr.SplitLinesKeepEnds(post.Text), "file", "post", 0));
            }
        }
        return differ > 0 ? 1 : 0;
    }

    /// <summary>(team id, channel id) from a channel's "Copy link" in Teams, with no sign-in needed:
    /// https://teams.microsoft.com/l/channel/19%3a...%40thread.tacv2/Name?groupId=&lt;team id&gt;&amp;tenantId=...</summary>
    public static (string Team, string Channel) FromLink(string link)
    {
        var (path, query) = PyUrl.SplitPathAndQuery(PyStr.Strip(link));
        var parts = path.Split('/');
        var at = Array.IndexOf(parts, "channel");
        if (at < 0)
            throw new PyValueError("That isn't a Teams channel link. In Teams, use the channel's ... menu, then Copy link.");
        if (at + 1 >= parts.Length)
            throw new IndexOutOfRangeException("list index out of range");   // as Python failed
        var channel = PyUrl.Unquote(parts[at + 1]);
        var team = PyUrl.ParseQs(query).TryGetValue("groupId", out var values) ? values[0] : "";
        if (team.Length == 0)
            throw new PyValueError("The link has no groupId; copy the channel's link, not a message's.");
        return (team, channel);
    }

    /// <summary>Your teams and their channels, with the ids a [[source]] needs. Run by hand, so it may open a
    /// browser to approve Team.ReadBasic.All and Channel.ReadBasic.All (users can usually approve these).</summary>
    public static void Channels(IGraph graph, Action<string> log)
    {
        string[] scopes = ["Team.ReadBasic.All", "Channel.ReadBasic.All"];
        foreach (var team in graph.Pages("/me/joinedTeams?$select=id,displayName", scopes))
        {
            log($"{Str(team?["displayName"])}\n  team = \"{Str(team?["id"])}\"");
            foreach (var channel in graph.Pages($"/teams/{Str(team?["id"])}/channels?$select=id,displayName", scopes))
                log($"    {Str(channel?["displayName"])}\n      channel = \"{Str(channel?["id"])}\"");
        }
    }

    private static string Str(JsonNode? node) => node is null ? "None" : Text(node) ?? node.ToJsonString();
}

/// <summary>The timestamps Graph gives (lastModifiedDateTime), compared as Python compared them:
/// datetime.fromisoformat(stamp.replace("Z", "+00:00")), to the microsecond. An empty or unreadable stamp is
/// Python's ValueError; comparing one with a time zone to one without is its TypeError.</summary>
public static class IsoTime
{
    public static int Compare(string a, string b)
    {
        var (x, xAware) = Parse(a);
        var (y, yAware) = Parse(b);
        if (xAware != yAware)
            throw new InvalidOperationException("can't compare offset-naive and offset-aware datetimes");
        return x.CompareTo(y);
    }

    /// <summary>The instant in UTC ticks, truncated to microseconds, and whether it had an offset.</summary>
    public static (long Ticks, bool Aware) Parse(string stamp)
    {
        var text = stamp.Replace("Z", "+00:00");
        var error = new PyValueError($"Invalid isoformat string: '{text}'");
        if (text.Length < 10 || text[4] != '-' || text[7] != '-')
            throw error;
        var date = ParseInt(text, 0, 4, error);
        var month = ParseInt(text, 5, 2, error);
        var day = ParseInt(text, 8, 2, error);
        int hour = 0, minute = 0, second = 0;
        long micro = 0;
        var i = 10;
        TimeSpan? offset = null;
        if (i < text.Length)
        {
            i++;   // any one separator character, as Python allows
            hour = ParseInt(text, i, 2, error);
            i += 2;
            if (i < text.Length && text[i] == ':')
            {
                minute = ParseInt(text, i + 1, 2, error);
                i += 3;
                if (i < text.Length && text[i] == ':')
                {
                    second = ParseInt(text, i + 1, 2, error);
                    i += 3;
                    if (i < text.Length && text[i] is '.' or ',')
                    {
                        i++;
                        var start = i;
                        while (i < text.Length && char.IsAsciiDigit(text[i]))
                            i++;
                        if (i == start)
                            throw error;
                        var digits = text[start..i];
                        micro = long.Parse((digits.Length >= 6 ? digits[..6] : digits.PadRight(6, '0')), CultureInfo.InvariantCulture);
                    }
                }
            }
            if (i < text.Length)
            {
                if (text[i] is not ('+' or '-') || text.Length - i < 3)
                    throw error;
                var sign = text[i] == '-' ? -1 : 1;
                var oh = ParseInt(text, i + 1, 2, error);
                var om = 0;
                var j = i + 3;
                if (j < text.Length)
                {
                    if (text[j] == ':')
                        j++;
                    om = ParseInt(text, j, 2, error);
                    j += 2;
                    if (j != text.Length)
                        throw error;
                }
                offset = TimeSpan.FromMinutes(sign * (oh * 60 + om));
            }
        }
        try
        {
            var local = new DateTime(date, month, day, hour, minute, second, DateTimeKind.Unspecified).AddTicks(micro * 10);
            var ticks = offset is { } o ? local.Ticks - o.Ticks : local.Ticks;
            return (ticks, offset is not null);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw error;
        }
    }

    private static int ParseInt(string text, int at, int length, Exception error)
    {
        if (at + length > text.Length)
            throw error;
        var value = 0;
        for (var k = at; k < at + length; k++)
        {
            if (!char.IsAsciiDigit(text[k]))
                throw error;
            value = value * 10 + (text[k] - '0');
        }
        return value;
    }
}
