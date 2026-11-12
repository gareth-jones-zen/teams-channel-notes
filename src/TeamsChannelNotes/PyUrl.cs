using System.Globalization;
using System.Text;

namespace TeamsChannelNotes;

/// <summary>The parts of urllib.parse that `ids` uses, ported: urlparse's path and query, unquote (UTF-8, bad
/// bytes replaced) and parse_qs (blank values dropped).</summary>
public static class PyUrl
{
    private const string SchemeChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789+-.";

    /// <summary>urlparse(url).path and .query.</summary>
    public static (string Path, string Query) SplitPathAndQuery(string url)
    {
        url = url.TrimStart(Enumerable.Range(0, 0x21).Select(c => (char)c).ToArray());
        url = url.Replace("\t", "").Replace("\r", "").Replace("\n", "");
        var scheme = "";
        var colon = url.IndexOf(':');
        if (colon > 0 && char.IsAsciiLetter(url[0]) && url[..colon].All(c => SchemeChars.Contains(c)))
        {
            scheme = url[..colon].ToLowerInvariant();
            url = url[(colon + 1)..];
        }
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            var end = url.Length;
            foreach (var c in "/?#")
            {
                var at = url.IndexOf(c, 2);
                if (at >= 0)
                    end = Math.Min(end, at);
            }
            url = url[end..];
        }
        var hash = url.IndexOf('#');
        if (hash >= 0)
            url = url[..hash];
        var query = "";
        var question = url.IndexOf('?');
        if (question >= 0)
        {
            query = url[(question + 1)..];
            url = url[..question];
        }
        string[] usesParams = ["", "ftp", "hdl", "prospero", "http", "imap", "https", "shttp", "rtsp", "rtsps", "rtspu", "sip",
            "sips", "mms", "sftp", "tel"];
        if (usesParams.Contains(scheme) && url.Contains(';'))
        {
            var semicolon = url.Contains('/') ? url.IndexOf(';', url.LastIndexOf('/')) : url.IndexOf(';');
            if (semicolon >= 0)
                url = url[..semicolon];
        }
        return (url, query);
    }

    /// <summary>parse_qs(query): each name's values, blank ones dropped.</summary>
    public static Dictionary<string, List<string>> ParseQs(string query)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (query.Length == 0)
            return result;
        foreach (var pair in query.Split('&'))
        {
            if (pair.Length == 0)
                continue;
            var eq = pair.IndexOf('=');
            if (eq < 0)
                continue;
            var value = pair[(eq + 1)..];
            if (value.Length == 0)
                continue;
            var name = Unquote(pair[..eq].Replace('+', ' '));
            if (!result.TryGetValue(name, out var values))
                result[name] = values = [];
            values.Add(Unquote(value.Replace('+', ' ')));
        }
        return result;
    }

    /// <summary>unquote(text): %XX sequences as UTF-8, with bad bytes replaced; anything else as it is.</summary>
    public static string Unquote(string text)
    {
        if (!text.Contains('%'))
            return text;
        var output = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var start = i;
            var ascii = text[i] < 0x80;
            while (i < text.Length && text[i] < 0x80 == ascii)
                i++;
            var run = text[start..i];
            output.Append(ascii ? Encoding.UTF8.GetString(UnquoteToBytes(run)) : run);
        }
        return output.ToString();
    }

    private static byte[] UnquoteToBytes(string run)
    {
        var bytes = new List<byte>(run.Length);
        var bits = run.Split('%');
        bytes.AddRange(Encoding.ASCII.GetBytes(bits[0]));
        foreach (var item in bits.Skip(1))
        {
            if (item.Length >= 2 && byte.TryParse(item.AsSpan(0, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
            {
                bytes.Add(value);
                bytes.AddRange(Encoding.ASCII.GetBytes(item[2..]));
            }
            else
            {
                bytes.Add((byte)'%');
                bytes.AddRange(Encoding.ASCII.GetBytes(item));
            }
        }
        return bytes.ToArray();
    }
}
