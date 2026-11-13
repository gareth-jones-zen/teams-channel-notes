using PyCompat;
using System.Text.RegularExpressions;

namespace TeamsChannelSync;

/// <summary>Python 3.14.5's html.parser.HTMLParser(convert_charrefs=True), ported from CPython's own source
/// (Lib/html/parser.py) rather than reimplemented, because the notes have to come out byte for byte the same.
///
/// It's a tokenizer, not a tree builder: no implied end tags and no fix-ups, which is why AngleSharp and
/// HtmlAgilityPack won't do. Only what the tool reaches is here: the tool calls feed() once and never close(),
/// so text after the last tag that ends in an unfinished "&amp;..." is held back and lost, and so is an
/// unfinished tag at the end. Both are copied, deliberately. With convert_charrefs on, the parser only ever
/// stops at a "&lt;", so CPython's "&amp;" branches in goahead can't be reached and aren't ported.</summary>
public abstract partial class HtmlParser
{
    private static readonly string[] CdataContentElements = ["script", "style", "xmp", "iframe", "noembed", "noframes"];
    private static readonly string[] RcdataContentElements = ["textarea", "title"];

    [GeneratedRegex(@"\G<[a-zA-Z]")]
    private static partial Regex StartTagOpen();

    [GeneratedRegex(@"\G</[a-zA-Z]")]
    private static partial Regex EndTagOpen();

    [GeneratedRegex(@"--!?>")]
    private static partial Regex CommentClose();

    [GeneratedRegex(@"\G-?>")]
    private static partial Regex CommentAbruptClose();

    [GeneratedRegex(@"\G([a-zA-Z][^\t\n\r\f />]*)(?:[\t\n\r\f ]|/(?!>))*")]
    private static partial Regex TagFindTolerant();

    [GeneratedRegex(@"\G((?<=['""\t\n\r\f /])[^\t\n\r\f />][^\t\n\r\f /=>]*)([\t\n\r\f ]*=[\t\n\r\f ]*('[^']*'|""[^""]*""|(?!['""])[^>\t\n\r\f ]*))?(?:[\t\n\r\f ]|/(?!>))*")]
    private static partial Regex AttrFindTolerant();

    [GeneratedRegex(@"\G[a-zA-Z][^\t\n\r\f />]*[\t\n\r\f /]*(?:(?<=['""\t\n\r\f /])[^\t\n\r\f />][^\t\n\r\f /=>]*(?:[\t\n\r\f ]*=[\t\n\r\f ]*(?:'[^']*'|""[^""]*""|(?!['""])[^>\t\n\r\f ]*))?[\t\n\r\f /]*)*>?")]
    private static partial Regex LocateTagEnd();

    [GeneratedRegex(@"&(#[0-9]+|#[xX][0-9a-fA-F]+|[a-zA-Z][a-zA-Z0-9]*)[;=]?")]
    private static partial Regex AttrCharref();

    [GeneratedRegex(@"[\t\n\r\f ;]")]
    private static partial Regex CharrefTerminator();

    private string rawdata = "";
    private string? cdataElem;
    private bool escapable = true;

    /// <summary>A start tag: the name lowercased, and its attributes in order (a valueless one has a null value).</summary>
    protected virtual void HandleStartTag(string tag, IReadOnlyList<(string Name, string? Value)> attrs)
    {
    }

    protected virtual void HandleEndTag(string tag)
    {
    }

    /// <summary>Text, with character references already converted (it may come in several pieces).</summary>
    protected virtual void HandleData(string data)
    {
    }

    /// <summary>An XHTML-style empty tag, &lt;br/&gt;: a start tag, then an end tag.</summary>
    protected virtual void HandleStartEndTag(string tag, IReadOnlyList<(string Name, string? Value)> attrs)
    {
        HandleStartTag(tag, attrs);
        HandleEndTag(tag);
    }

    /// <summary>feed(data), with no close() after it, as the tool uses it.</summary>
    public void Feed(string data)
    {
        rawdata += data;
        GoAhead();
    }

    private void GoAhead()
    {
        var raw = rawdata;
        var i = 0;
        var n = raw.Length;
        while (i < n)
        {
            int j;
            if (cdataElem is null)
            {
                j = raw.IndexOf('<', i);
                if (j < 0)
                {
                    // No "<" left: the text may end in a reference cut in half, so hold it back if there's an "&"
                    // near the end (34 code points, as Python counts) with nothing after it to end it.
                    var from = Math.Max(i, raw.Length - PyStr.Tail(raw, 34).Length);
                    var amppos = from < n ? raw.LastIndexOf('&', n - 1, n - from) : -1;
                    if (amppos >= 0 && !CharrefTerminator().IsMatch(raw, amppos))
                        break;
                    j = n;
                }
            }
            else
            {
                j = FindCdataEnd(raw, i);
                if (j < 0)
                    break;
            }
            if (i < j)
                HandleData(escapable ? PyHtml.Unescape(raw[i..j]) : raw[i..j]);
            i = j;
            if (i == n)
                break;
            if (raw[i] != '<')
                throw new InvalidOperationException("interesting.search() lied");
            int k;
            if (StartTagOpen().IsMatch(raw, i))
                k = ParseStartTag(i);
            else if (StartsWith(raw, "</", i))
                k = ParseEndTag(i);
            else if (StartsWith(raw, "<!--", i))
                k = ParseComment(i);
            else if (StartsWith(raw, "<?", i))
                k = ParsePi(i);
            else if (StartsWith(raw, "<!", i))
                k = ParseHtmlDeclaration(i);
            else if (i + 1 < n)
            {
                HandleData("<");
                k = i + 1;
            }
            else
                break;
            if (k < 0)
                break;   // incomplete, and without close() it's never finished
            i = k;
        }
        rawdata = raw[i..];
    }

    private static bool StartsWith(string text, string prefix, int at) =>
        string.CompareOrdinal(text, at, prefix, 0, prefix.Length) == 0 && at + prefix.Length <= text.Length;

    /// <summary>The cdata mode's "interesting" pattern, &lt;/elem(?=[\t\n\r\f /&gt;]), case-insensitive in ASCII only
    /// (re.IGNORECASE|re.ASCII); "plaintext" never ends. Searched by hand so .NET's wider case-insensitivity
    /// (the Kelvin sign, the long s) can't creep in.</summary>
    private int FindCdataEnd(string raw, int start)
    {
        if (cdataElem == "plaintext")
            return raw.Length;
        var name = cdataElem!;
        for (var at = raw.IndexOf("</", start, StringComparison.Ordinal); at >= 0; at = raw.IndexOf("</", at + 1, StringComparison.Ordinal))
        {
            var after = at + 2 + name.Length;
            if (after >= raw.Length)
                return -1;
            var same = true;
            for (var c = 0; c < name.Length && same; c++)
            {
                var ch = raw[at + 2 + c];
                if (ch is >= 'A' and <= 'Z')
                    ch = (char)(ch + 32);
                same = ch == name[c];
            }
            if (same && raw[after] is '\t' or '\n' or '\r' or '\f' or ' ' or '/' or '>')
                return at;
        }
        return -1;
    }

    private void SetCdataMode(string elem, bool escapableContent)
    {
        cdataElem = PyStr.Lower(elem);
        escapable = escapableContent;
    }

    private void ClearCdataMode()
    {
        cdataElem = null;
        escapable = true;
    }

    private int ParseHtmlDeclaration(int i)
    {
        var raw = rawdata;
        if (StartsWith(raw, "<!--", i))
            return ParseComment(i);
        if (StartsWith(raw, "<![CDATA[", i))
        {
            var j = IndexOf(raw, "]]>", i + 9);
            return j < 0 ? -1 : j + 3;
        }
        if (raw.Length >= i + 9 && string.Equals(raw.Substring(i, 9), "<!doctype", StringComparison.OrdinalIgnoreCase))
        {
            var gtpos = IndexOf(raw, ">", i + 9);
            return gtpos < 0 ? -1 : gtpos + 1;
        }
        return ParseBogusComment(i);
    }

    private static int IndexOf(string raw, string value, int start) =>
        start > raw.Length ? -1 : raw.IndexOf(value, start, StringComparison.Ordinal);

    private int ParseComment(int i)
    {
        var raw = rawdata;
        var match = CommentClose().Match(raw, i + 4);
        if (!match.Success)
        {
            match = CommentAbruptClose().Match(raw, i + 4);
            if (!match.Success)
                return -1;
        }
        return match.Index + match.Length;
    }

    private int ParseBogusComment(int i)
    {
        var pos = IndexOf(rawdata, ">", i + 2);
        return pos < 0 ? -1 : pos + 1;
    }

    private int ParsePi(int i)
    {
        var pos = IndexOf(rawdata, ">", i + 2);
        return pos < 0 ? -1 : pos + 1;
    }

    private int ParseStartTag(int i)
    {
        var endpos = CheckForWholeStartTag(i);
        if (endpos < 0)
            return endpos;
        var raw = rawdata;
        var attrs = new List<(string, string?)>();
        var match = TagFindTolerant().Match(raw, i + 1);
        var k = match.Index + match.Length;
        var tag = PyStr.Lower(match.Groups[1].Value);
        while (k < endpos)
        {
            var m = AttrFindTolerant().Match(raw, k);
            if (!m.Success)
                break;
            var attrName = m.Groups[1].Value;
            string? attrValue = m.Groups[3].Success ? m.Groups[3].Value : null;
            if (!m.Groups[2].Success || m.Groups[2].Length == 0)
                attrValue = null;
            else if (attrValue!.Length >= 2 && (attrValue[0] == '\'' && attrValue[^1] == '\'' || attrValue[0] == '"' && attrValue[^1] == '"'))
                attrValue = attrValue[1..^1];
            if (!string.IsNullOrEmpty(attrValue))
                attrValue = UnescapeAttrValue(attrValue);
            attrs.Add((PyStr.Lower(attrName), attrValue));
            k = m.Index + m.Length;
        }
        var end = PyStr.Strip(raw[k..endpos]);
        if (end is not (">" or "/>"))
        {
            HandleData(raw[i..endpos]);
            return endpos;
        }
        if (end.EndsWith("/>", StringComparison.Ordinal))
            HandleStartEndTag(tag, attrs);
        else
        {
            HandleStartTag(tag, attrs);
            if (CdataContentElements.Contains(tag) || tag == "plaintext")
                SetCdataMode(tag, false);
            else if (RcdataContentElements.Contains(tag))
                SetCdataMode(tag, true);
        }
        return endpos;
    }

    private static string UnescapeAttrValue(string value) =>
        AttrCharref().Replace(value, match =>
        {
            var reference = match.Value;
            if (reference.StartsWith("&#", StringComparison.Ordinal))
                return PyHtml.Unescape(reference);
            if (!reference.EndsWith('=') && PyHtml.IsEntity(reference[1..]))
                return PyHtml.Unescape(reference);
            return reference;
        });

    private int CheckForWholeStartTag(int i)
    {
        var match = LocateTagEnd().Match(rawdata, i + 1);
        var j = match.Index + match.Length;
        return rawdata[j - 1] != '>' ? -1 : j;
    }

    private int ParseEndTag(int i)
    {
        var raw = rawdata;
        if (IndexOf(raw, ">", i + 2) < 0)
            return -1;
        if (!EndTagOpen().IsMatch(raw, i))
        {
            if (raw.Length > i + 2 && raw[i + 2] == '>')
                return i + 3;   // "</>" is ignored
            return ParseBogusComment(i);
        }
        var match = LocateTagEnd().Match(raw, i + 2);
        var j = match.Index + match.Length;
        if (raw[j - 1] != '>')
            return -1;
        var tag = PyStr.Lower(TagFindTolerant().Match(raw, i + 2).Groups[1].Value);
        HandleEndTag(tag);
        ClearCdataMode();
        return j;
    }
}
