using System.Text;

namespace TeamsChannelNotes;

/// <summary>Python's text behaviour where it differs from .NET's, so a note comes out byte for byte as the Python
/// version wrote it. Parity depends on these, so they're kept in one place and tested against tables Python
/// generated (tests/TeamsChannelNotes.Tests/Generated).</summary>
public static class PyText
{
    /// <summary>A regex class for Python's \s: .NET's \s is char.IsWhiteSpace, and Python also counts
    /// U+001C to U+001F (the information separators) as space.</summary>
    public const string Space = @"[\s\x1c-\x1f]";

    /// <summary>The same, for use inside a character class.</summary>
    public const string SpaceInClass = @"\s\x1c-\x1f";

    /// <summary>Python's str.isspace() for one UTF-16 unit. Astral characters are never space, so a surrogate
    /// (half of one) isn't either.</summary>
    public static bool IsSpace(char c) => char.IsWhiteSpace(c) || c is >= '\x1c' and <= '\x1f';

    /// <summary>str.strip() with no arguments.</summary>
    public static string Strip(string text)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && IsSpace(text[start]))
            start++;
        while (end > start && IsSpace(text[end - 1]))
            end--;
        return start == 0 && end == text.Length ? text : text[start..end];
    }

    /// <summary>str.split() with no arguments: runs of whitespace separate, and there are no empty parts.</summary>
    public static List<string> Split(string text)
    {
        var parts = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && IsSpace(text[i]))
                i++;
            var start = i;
            while (i < text.Length && !IsSpace(text[i]))
                i++;
            if (i > start)
                parts.Add(text[start..i]);
        }
        return parts;
    }

    /// <summary>" ".join(text.split()): the text with its whitespace collapsed, for comparing two notes.</summary>
    public static string Collapse(string text) => string.Join(' ', Split(text));

    /// <summary>str.splitlines(keepends=True), with every line boundary Python recognises.</summary>
    public static List<string> SplitLinesKeepEnds(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                lines.Add(text[start..(i + 1)]);
                start = i + 1;
            }
        }
        if (start < text.Length)
            lines.Add(text[start..]);
        return lines;
    }

    /// <summary>str.splitlines() without the endings.</summary>
    public static List<string> SplitLines(string text) =>
        SplitLinesKeepEnds(text).Select(line =>
        {
            if (line.EndsWith("\r\n", StringComparison.Ordinal))
                return line[..^2];
            return line.Length > 0 && line[^1] is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029'
                ? line[..^1]
                : line;
        }).ToList();

    /// <summary>str.lower() as far as it matters here (tag and attribute names): each character's own lowercase,
    /// except U+0130, which Python lowers to "i" and a combining dot (so it never becomes a plain "i"). Python's
    /// final-sigma rule isn't copied: no tag this tool looks at has a sigma in it.</summary>
    public static string Lower(string text)
    {
        var ascii = true;
        foreach (var c in text)
        {
            if (c >= 0x80 || c is >= 'A' and <= 'Z')
            {
                ascii = false;
                break;
            }
        }
        if (ascii)
            return text;
        var result = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c == '\u0130')
                result.Append("i\u0307");
            else
                result.Append(char.ToLowerInvariant(c));
        }
        return result.ToString();
    }

    /// <summary>The UTF-16 index where the last <paramref name="count"/> code points start (Python slices count
    /// code points; .NET strings count UTF-16 units, and an emoji is two of them).</summary>
    public static int IndexOfLastCodePoints(string text, int count)
    {
        var i = text.Length;
        while (count > 0 && i > 0)
        {
            i--;
            if (i > 0 && char.IsLowSurrogate(text[i]) && char.IsHighSurrogate(text[i - 1]))
                i--;
            count--;
        }
        return i;
    }

    /// <summary>text[:count] where count is in code points.</summary>
    public static string TakeCodePoints(string text, int count)
    {
        var i = 0;
        while (count > 0 && i < text.Length)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                i++;
            i++;
            count--;
        }
        return text[..i];
    }

    /// <summary>Text as Python's text mode writes it on Windows: each "\n" as "\r\n" (nothing else changes).</summary>
    public static string ToWindowsNewlines(string text) => text.Replace("\n", "\r\n");

    /// <summary>Text as Python's text mode reads it (universal newlines): "\r\n" and a lone "\r" become "\n".</summary>
    public static string FromUniversalNewlines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>path.read_text(encoding="utf-8"): invalid UTF-8 is an error (Python's UnicodeDecodeError, a
    /// ValueError), a byte order mark is kept as U+FEFF, and newlines are universal.</summary>
    public static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException error)
        {
            throw new PyValueError($"'utf-8' codec can't decode {Path.GetFileName(path)}: {error.Message}");
        }
        return FromUniversalNewlines(text);
    }

    /// <summary>path.write_text(text, encoding="utf-8"): no byte order mark, "\n" written as "\r\n".</summary>
    public static void WriteText(string path, string text) =>
        File.WriteAllBytes(path, Utf8NoBom.GetBytes(ToWindowsNewlines(text)));
}

/// <summary>What Python raises as ValueError (including tomllib's and json's decode errors): the command line
/// reports it and exits 2.</summary>
public class PyValueError(string message, Exception? inner = null) : Exception(message, inner);
