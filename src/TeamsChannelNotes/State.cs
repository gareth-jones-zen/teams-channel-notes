using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TeamsChannelNotes;

/// <summary>Which files this tool wrote, and the post's lastModifiedDateTime when it did: a JSON map from the
/// file's full path (lowercased) to Graph's timestamp, exactly as Python stored it.
///
/// Written as json.dumps(data, indent=1, sort_keys=True) in text mode: "\r\n" line endings, a one-space indent,
/// no final newline, non-ASCII as lowercase \uXXXX, and "&amp;", "'", "+", "&lt;" and "&gt;" left as they are. So
/// it's written by hand: System.Text.Json escapes differently, and a path can hold "&amp;" or "'". A file that
/// can't be read or isn't JSON is treated as empty, silently, as Python did (everything is then "untracked",
/// which is safe).</summary>
public sealed class State
{
    private readonly string path;

    /// <summary>The map, in the order the file had it (Python kept dict order, and sorted on writing).</summary>
    public Dictionary<string, JsonNode?> Data { get; }

    public State(string path)
    {
        this.path = path;
        Data = Read(path);
    }

    private static Dictionary<string, JsonNode?> Read(string path)
    {
        string text;
        try
        {
            text = PyText.ReadText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        if (text.StartsWith('\ufeff'))
            return [];   // json.loads refuses a byte order mark
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 512 });
        }
        catch (JsonException)
        {
            return [];
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"{path} isn't a JSON object, so the state can't be read.");
            // a repeated key keeps its first place and takes its last value, as a Python dict does
            var data = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                data[property.Name] = property.Value.ValueKind == JsonValueKind.Null ? null : JsonNode.Parse(property.Value.GetRawText());
            return data;
        }
    }

    /// <summary>str(file).lower(): the key. (.NET's lowercasing matches Python's for every path but ones holding
    /// U+0130, which Python lowers to two characters.)</summary>
    public static string Key(string file) => file.ToLowerInvariant();

    /// <summary>The recorded timestamp, or null if the tool didn't write this file (or recorded JSON null).</summary>
    public string? Get(string file)
    {
        if (!Data.TryGetValue(Key(file), out var value) || value is null)
            return null;
        if (value is JsonValue v && v.GetValueKind() == JsonValueKind.String)
            return v.GetValue<string>();
        throw new InvalidDataException($"The state for {file} isn't text: {value.ToJsonString()}");
    }

    public void Set(string file, string modified) => Data[Key(file)] = JsonValue.Create(modified);

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        PyText.WriteText(path, Dump(Data));
    }

    /// <summary>json.dumps(data, indent=1, sort_keys=True), with "\n" newlines (WriteText makes them "\r\n").
    /// Keys sort by code point, as Python sorts them.</summary>
    public static string Dump(IReadOnlyDictionary<string, JsonNode?> data)
    {
        if (data.Count == 0)
            return "{}";
        var builder = new StringBuilder("{");
        var first = true;
        foreach (var key in data.Keys.Order(CodePointComparer.Instance))
        {
            builder.Append(first ? "\n " : ",\n ");
            first = false;
            PyJson.Quote(builder, key);
            builder.Append(": ");
            PyJson.Write(builder, data[key], 1, 1);
        }
        return builder.Append("\n}").ToString();
    }
}

/// <summary>Strings compared by code point, as Python compares them (UTF-16 order differs once a string holds a
/// character beyond U+FFFF and another holds one from U+E000 to U+FFFF).</summary>
internal sealed class CodePointComparer : IComparer<string>
{
    public static readonly CodePointComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
            return string.CompareOrdinal(x, y);
        var a = x.EnumerateRunes();
        var b = y.EnumerateRunes();
        while (true)
        {
            var hasA = a.MoveNext();
            var hasB = b.MoveNext();
            if (!hasA || !hasB)
                return hasA.CompareTo(hasB);
            var diff = a.Current.Value.CompareTo(b.Current.Value);
            if (diff != 0)
                return diff;
        }
    }
}

/// <summary>Python's json.dumps for the values that can turn up here (ensure_ascii on).</summary>
internal static class PyJson
{
    public static void Quote(StringBuilder b, string text)
    {
        b.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': b.Append("\\\""); break;
                case '\\': b.Append("\\\\"); break;
                case '\n': b.Append("\\n"); break;
                case '\r': b.Append("\\r"); break;
                case '\t': b.Append("\\t"); break;
                case '\b': b.Append("\\b"); break;
                case '\f': b.Append("\\f"); break;
                default:
                    if (c < 0x20 || c > 0x7e)
                        b.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    else
                        b.Append(c);
                    break;
            }
        }
        b.Append('"');
    }

    /// <summary>One value. indent null: one line with ", " and ": " (json.dumps' default).</summary>
    public static void Write(StringBuilder b, JsonNode? node, int? indent, int level)
    {
        switch (node)
        {
            case null:
                b.Append("null");
                return;
            case JsonObject obj:
                WriteItems(b, obj.Select(pair => ((string?)pair.Key, pair.Value)).ToList(), indent, level, '{', '}');
                return;
            case JsonArray array:
                WriteItems(b, array.Select(item => ((string?)null, item)).ToList(), indent, level, '[', ']');
                return;
            case JsonValue value:
                switch (value.GetValueKind())
                {
                    case JsonValueKind.String: Quote(b, value.GetValue<string>()); return;
                    case JsonValueKind.True: b.Append("true"); return;
                    case JsonValueKind.False: b.Append("false"); return;
                    case JsonValueKind.Number: b.Append(value.ToJsonString()); return;   // as it was in the file
                    default: b.Append("null"); return;
                }
        }
    }

    private static void WriteItems(StringBuilder b, List<(string? Key, JsonNode? Value)> items, int? indent, int level, char open, char close)
    {
        if (items.Count == 0)
        {
            b.Append(open).Append(close);
            return;
        }
        b.Append(open);
        for (var i = 0; i < items.Count; i++)
        {
            if (indent is { } size)
                b.Append(i == 0 ? "" : ",").Append('\n').Append(' ', size * (level + 1));
            else if (i > 0)
                b.Append(", ");
            if (items[i].Key is { } key)
            {
                Quote(b, key);
                b.Append(": ");
            }
            Write(b, items[i].Value, indent, level + 1);
        }
        if (indent is { } close2)
            b.Append('\n').Append(' ', close2 * level);
        b.Append(close);
    }

    /// <summary>json.dumps(value) for a list of strings, on one line.</summary>
    public static string StringList(IEnumerable<string> items)
    {
        var b = new StringBuilder("[");
        var first = true;
        foreach (var item in items)
        {
            if (!first)
                b.Append(", ");
            first = false;
            Quote(b, item);
        }
        return b.Append(']').ToString();
    }
}
