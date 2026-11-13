using PyCompat;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TeamsChannelNotes;

/// <summary>A source's title pattern is written in Python's regex syntax (the settings file is shared with the
/// Python version), so it's translated to .NET's: (?P&lt;name&gt; and (?P=name), \Z, {,n}, and Python's \s (which
/// also counts U+001C to U+001F). Python-only syntax with no clean translation (possessive quantifiers, the a
/// and L flags, \N{...}) is refused with a clear message rather than guessed at. Built at run
/// time, which is fine under Native AOT: it runs on the regex interpreter.</summary>
public static class PyRegex
{
    public static Regex Compile(string pattern)
    {
        string translated;
        try
        {
            translated = Translate(pattern);
            return new Regex(translated, RegexOptions.CultureInvariant);
        }
        catch (ArgumentException error)
        {
            throw new PyValueError($"title isn't a regular expression this can use: {error.Message}");
        }
    }

    /// <summary>Python syntax to .NET syntax. Throws ArgumentException for Python-only syntax.</summary>
    public static string Translate(string pattern)
    {
        var output = new StringBuilder(pattern.Length + 16);
        var set = new StringBuilder();   // the current set's contents, emitted when it closes
        var inClass = false;
        var negated = false;
        var nonSpace = false;            // the set holds Python's \S
        var classStart = -1;             // where the current set's contents start (a "]" there is literal)
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            var target = inClass ? set : output;
            if (c == '\\')
            {
                if (i + 1 >= pattern.Length)
                    throw new ArgumentException("the pattern ends with a backslash");
                var next = pattern[++i];
                switch (next)
                {
                    case 's':
                        target.Append(inClass ? PyStr.SpaceClass : PyText.Space);
                        break;
                    case 'S':
                        if (inClass)
                            nonSpace = true;
                        else
                            output.Append(NotSpace);
                        break;
                    case 'Z':
                        target.Append(inClass ? "Z" : @"\z");
                        break;
                    case 'N':
                        throw new ArgumentException(@"\N{...} (a character by name) isn't supported; use \uXXXX");
                    case 'U':
                        if (i + 8 >= pattern.Length)
                            throw new ArgumentException(@"\U needs eight hex digits");
                        var hex = pattern.Substring(i + 1, 8);
                        if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code) || code > 0x10FFFF)
                            throw new ArgumentException($@"\U{hex} isn't a character");
                        var text = char.ConvertFromUtf32(code);
                        if (inClass && text.Length > 1)
                            throw new ArgumentException(@"a character beyond U+FFFF inside a set [...] isn't supported");
                        target.Append(Regex.Escape(text));
                        i += 8;
                        break;
                    default:
                        target.Append('\\').Append(next);
                        break;
                }
                continue;
            }
            if (inClass)
            {
                if (c == ']' && i == classStart)
                    set.Append(@"\]");
                else if (c == ']')
                {
                    inClass = false;
                    output.Append(CloseSet(set.ToString(), negated, nonSpace));
                }
                else if (c == '[' && i > 0 && pattern[i - 1] == '-')
                    throw new ArgumentException("\"-[\" inside a set means set subtraction in .NET; escape the \"[\"");
                else if (c == '[')
                    set.Append(@"\[");
                else
                    set.Append(c);
                continue;
            }
            switch (c)
            {
                case '[':
                    inClass = true;
                    set.Clear();
                    nonSpace = false;
                    negated = i + 1 < pattern.Length && pattern[i + 1] == '^';
                    if (negated)
                        i++;
                    classStart = i + 1;
                    break;
                case '(' when Next(pattern, i, "(?P<"):
                    output.Append("(?<");
                    i += 3;
                    break;
                case '(' when Next(pattern, i, "(?P="):
                    var close = pattern.IndexOf(')', i);
                    if (close < 0)
                        throw new ArgumentException("(?P= isn't closed");
                    output.Append(@"\k<").Append(pattern, i + 4, close - i - 4).Append('>');
                    i = close;
                    break;
                case '(' when Next(pattern, i, "(?"):
                    // inline flags: (?aiLmsux) or (?flags:...) or (?-flags:...)
                    var j = i + 2;
                    var flags = new StringBuilder();
                    while (j < pattern.Length && (char.IsAsciiLetter(pattern[j]) || pattern[j] == '-'))
                        flags.Append(pattern[j++]);
                    if (flags.Length > 0 && j < pattern.Length && pattern[j] is ')' or ':')
                    {
                        var kept = new StringBuilder();
                        foreach (var flag in flags.ToString())
                        {
                            if (flag is 'a' or 'L')
                                throw new ArgumentException($"the (?{flag}) flag has no .NET equivalent");
                            if (flag != 'u')   // Unicode matching is the default in both
                                kept.Append(flag);
                        }
                        if (kept.Length == 0 && pattern[j] == ')')
                        {
                            i = j;   // (?u) alone: nothing left to say
                            break;
                        }
                        output.Append("(?").Append(kept.Length == 0 ? "" : kept.ToString()).Append(pattern[j]);
                        i = j;
                        break;
                    }
                    output.Append(c);
                    break;
                case '{' when Next(pattern, i, "{,"):
                    output.Append("{0,");
                    i += 1;
                    break;
                case '*' or '+' or '?' or '}' when i + 1 < pattern.Length && pattern[i + 1] == '+':
                    throw new ArgumentException("possessive quantifiers (*+, ++, ?+, {m,n}+) aren't supported; use (?>...)");
                default:
                    output.Append(c);
                    break;
            }
        }
        return output.ToString();
    }

    private const string NotSpace = @"[^\s\x1c-\x1f]";

    /// <summary>A set as .NET writes it. Python's \S inside a set (not space, as Python counts space) has no .NET
    /// spelling, so a set holding it becomes an alternation: [a\S] is "a, or not space"; [^a\S] is "space, and
    /// not a".</summary>
    private static string CloseSet(string contents, bool negated, bool nonSpace)
    {
        if (!nonSpace)
            return (negated ? "[^" : "[") + contents + "]";
        if (contents.Length == 0)
            return negated ? PyText.Space : NotSpace;
        return negated ? $"(?:(?![{contents}]){PyText.Space})" : $"(?:[{contents}]|{NotSpace})";
    }

    private static bool Next(string pattern, int at, string text) =>
        at + text.Length <= pattern.Length && string.CompareOrdinal(pattern, at, text, 0, text.Length) == 0;

    /// <summary>The named groups a pattern defines, as Python's groupindex lists them.</summary>
    public static List<string> GroupNames(Regex regex) =>
        regex.GetGroupNames().Where(name => !int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out _)).ToList();
}

/// <summary>Python's str.format(**fields) for a filename: {name} fields and {{ }} escapes only. Anything else
/// (positional fields, attribute or index access, conversions, format specs) is refused when the settings load,
/// so a bad filename is caught before a sync, not part-way through one.</summary>
public static class PyFormat
{
    /// <summary>The field names in a template. Throws PyValueError for anything but plain {name} fields.</summary>
    public static List<string> Fields(string template)
    {
        var fields = new List<string>();
        Walk(template, field => fields.Add(field), null);
        return fields;
    }

    public static string Format(string template, IReadOnlyDictionary<string, string> values)
    {
        var output = new StringBuilder();
        Walk(template, field => output.Append(values[field]), text => output.Append(text));
        return output.ToString();
    }

    private static void Walk(string template, Action<string> field, Action<string>? literal)
    {
        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];
            if (c == '{')
            {
                if (i + 1 < template.Length && template[i + 1] == '{')
                {
                    literal?.Invoke("{");
                    i += 2;
                    continue;
                }
                var close = template.IndexOf('}', i + 1);
                if (close < 0)
                    throw new PyValueError("filename: expected '}' before end of string");
                var name = template[(i + 1)..close];
                if (name.Length == 0 || !IsIdentifier(name))
                    throw new PyValueError($"filename: {{{name}}} isn't supported; use a named group from title, as {{date}}");
                field(name);
                i = close + 1;
                continue;
            }
            if (c == '}')
            {
                if (i + 1 < template.Length && template[i + 1] == '}')
                {
                    literal?.Invoke("}");
                    i += 2;
                    continue;
                }
                throw new PyValueError("filename: Single '}' encountered in format string");
            }
            var start = i;
            while (i < template.Length && template[i] is not ('{' or '}'))
                i++;
            literal?.Invoke(template[start..i]);
        }
    }

    private static bool IsIdentifier(string name) =>
        (char.IsLetter(name[0]) || name[0] == '_') && name.All(ch => char.IsLetterOrDigit(ch) || ch == '_');
}
