using System.Text;

namespace TeamsChannelNotes;

internal static partial class Html5Entities
{
    /// <summary>html.entities.html5: the name (with or without its ";") to its replacement.</summary>
    public static readonly Dictionary<string, string> Table = Build();

    private static Dictionary<string, string> Build()
    {
        var table = new Dictionary<string, string>(Pairs.Length / 2, StringComparer.Ordinal);
        for (var i = 0; i < Pairs.Length; i += 2)
            table[Pairs[i]] = Pairs[i + 1];
        return table;
    }
}

/// <summary>Python's html.unescape, ported exactly: every HTML5 named reference, with or without its semicolon
/// (the longest name that fits wins), and numeric references with the HTML5 rules for invalid ones (the cp1252
/// remap for 0x80 to 0x9F, U+FFFD for 0 and surrogates, nothing for the code points HTML forbids).
/// WebUtility.HtmlDecode does none of this.</summary>
public static class HtmlUnescape
{
    private static readonly Dictionary<int, string> InvalidCharrefs = new()
    {
        [0x00] = "\ufffd", [0x0d] = "\r", [0x80] = "€", [0x81] = "\x81", [0x82] = "‚", [0x83] = "ƒ",
        [0x84] = "„", [0x85] = "…", [0x86] = "†", [0x87] = "‡", [0x88] = "ˆ", [0x89] = "‰",
        [0x8a] = "Š", [0x8b] = "‹", [0x8c] = "Œ", [0x8d] = "\x8d", [0x8e] = "Ž", [0x8f] = "\x8f",
        [0x90] = "\x90", [0x91] = "‘", [0x92] = "’", [0x93] = "“", [0x94] = "”", [0x95] = "•",
        [0x96] = "–", [0x97] = "—", [0x98] = "˜", [0x99] = "™", [0x9a] = "š", [0x9b] = "›",
        [0x9c] = "œ", [0x9d] = "\x9d", [0x9e] = "ž", [0x9f] = "Ÿ",
    };

    private static bool InvalidCodepoint(long n) =>
        n is >= 0x1 and <= 0x8 or >= 0xe and <= 0x1f or >= 0x7f and <= 0x9f or >= 0xfdd0 and <= 0xfdef or 0xb
        || (n & 0xFFFE) == 0xFFFE && n <= 0x10FFFF;

    /// <summary>Whether a name (without the "&amp;", with any ";") is in html.entities.html5.</summary>
    public static bool IsEntity(string name) => Html5Entities.Table.ContainsKey(name);

    /// <summary>html.unescape(s).</summary>
    public static string Unescape(string s)
    {
        if (!s.Contains('&'))
            return s;
        var result = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            var amp = s.IndexOf('&', i);
            if (amp < 0)
            {
                result.Append(s, i, s.Length - i);
                break;
            }
            result.Append(s, i, amp - i);
            var end = MatchCharref(s, amp + 1);
            if (end < 0)
            {
                result.Append('&');
                i = amp + 1;
                continue;
            }
            result.Append(Replace(s[(amp + 1)..end]));
            i = end;
        }
        return result.ToString();
    }

    /// <summary>The end of _charref's group at <paramref name="start"/> (just after the "&amp;"), or -1:
    /// #[0-9]+;? | #[xX][0-9a-fA-F]+;? | [^\t\n\f &lt;&amp;#;]{1,32};? (32 code points, not UTF-16 units).</summary>
    private static int MatchCharref(string s, int start)
    {
        var i = start;
        if (i < s.Length && s[i] == '#')
        {
            i++;
            if (i < s.Length && s[i] is >= '0' and <= '9')
            {
                while (i < s.Length && s[i] is >= '0' and <= '9')
                    i++;
                return i < s.Length && s[i] == ';' ? i + 1 : i;
            }
            if (i + 1 < s.Length && s[i] is 'x' or 'X' && IsHex(s[i + 1]))
            {
                i++;
                while (i < s.Length && IsHex(s[i]))
                    i++;
                return i < s.Length && s[i] == ';' ? i + 1 : i;
            }
            return -1;   // "#" is excluded from the named form, so nothing else can match here
        }
        var count = 0;
        while (i < s.Length && count < 32 && s[i] is not ('\t' or '\n' or '\f' or ' ' or '<' or '&' or '#' or ';'))
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                i++;
            i++;
            count++;
        }
        if (count == 0)
            return -1;
        return i < s.Length && s[i] == ';' ? i + 1 : i;
    }

    private static bool IsHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    /// <summary>_replace_charref, for the text after the "&amp;".</summary>
    private static string Replace(string s)
    {
        if (s[0] == '#')
        {
            var hex = s[1] is 'x' or 'X';
            var digits = (hex ? s[2..] : s[1..]).TrimEnd(';');
            var num = ParseBig(digits, hex ? 16 : 10);
            if (num <= 0x9f && InvalidCharrefs.TryGetValue((int)num, out var mapped))
                return mapped;
            if (num is >= 0xD800 and <= 0xDFFF || num > 0x10FFFF)
                return "\ufffd";
            if (InvalidCodepoint(num))
                return "";
            return char.ConvertFromUtf32((int)num);
        }
        if (Html5Entities.Table.TryGetValue(s, out var value))
            return value;
        // the longest name that fits, as the standard defines it: s[:x] for x from len(s)-1 down to 2 (code points)
        var points = CodePointStarts(s);
        for (var x = points.Count - 2; x > 1; x--)
        {
            var cut = points[x];
            if (Html5Entities.Table.TryGetValue(s[..cut], out var prefix))
                return prefix + s[cut..];
        }
        return "&" + s;
    }

    /// <summary>Python's int(digits, base), capped: anything past 0x10FFFF is as good as infinite here.</summary>
    private static long ParseBig(string digits, int radix)
    {
        long value = 0;
        foreach (var c in digits)
        {
            var d = c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;
            value = value * radix + d;
            if (value > 0x10FFFF)
                return 0x110000;
        }
        return value;
    }

    /// <summary>The UTF-16 index where each code point starts, plus the length at the end.</summary>
    private static List<int> CodePointStarts(string s)
    {
        var starts = new List<int>(s.Length + 1);
        for (var i = 0; i < s.Length; i++)
        {
            starts.Add(i);
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                i++;
        }
        starts.Add(s.Length);
        return starts;
    }
}
