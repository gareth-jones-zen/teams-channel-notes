using System.Text;
using System.Text.RegularExpressions;

namespace TeamsChannelNotes;

/// <summary>Turn a Teams channel post's HTML body into markdown. Two formats, chosen per source:
///
/// - "bullets": only the post's lists, as plain nested bullets with a two-space indent. Paragraphs outside the
///   lists (a "Notes" header, a "Have a great weekend!" sign-off) are dropped. Written for weekly notes posted as
///   one nested list; entities are converted, emphasis dropped, emoji and wording kept, and links reduced to their
///   text.
/// - "markdown": the whole post: paragraphs, line breaks, nested lists, bold and italic, links as [text](url).
///
/// A line for line port of the Python version's convert.py, including its quirks (kept for parity in 1.0): text
/// after a nested list inside a bullet is dropped in "bullets", and an &lt;emoji alt&gt; loses its emoji.</summary>
public static partial class NoteFormats
{
    public const string Bullets = "bullets";
    public const string Markdown = "markdown";

    /// <summary>The formats, in the order Python's FORMATS dict lists them.</summary>
    public static readonly string[] Names = [Bullets, Markdown];

    public static string Convert(string format, string content) => format switch
    {
        Bullets => ToBullets(content),
        Markdown => ToMarkdown(content),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "format must be bullets or markdown"),
    };

    [GeneratedRegex(@"[\s\x1c-\x1f]+")]
    internal static partial Regex PySpaceRun();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex SpacesAndTabs();

    [GeneratedRegex(@" *\n *")]
    private static partial Regex SpacedNewline();

    [GeneratedRegex(@"\*\*[\s\x1c-\x1f]*\*\*")]
    private static partial Regex EmptyBold();

    /// <summary>A &lt;br&gt; while collapsing whitespace (U+2028); becomes a markdown hard break at the end.</summary>
    internal const char Break = '\u2028';

    public static string ToBullets(string content)
    {
        var parser = new BulletsParser();
        parser.Feed(content.Replace('\u00a0', ' '));
        parser.Flush();
        return string.Join('\n', parser.Lines) + "\n";
    }

    public static string ToMarkdown(string content)
    {
        var parser = new MarkdownParser();
        parser.Feed(content.Replace('\u00a0', ' '));
        parser.EndBlock();
        var text = new StringBuilder();
        int? previous = null;
        foreach (var (group, raw) in parser.Out)
        {
            var block = EmptyBold().Replace(raw, "");
            // items of the same list sit on consecutive lines; everything else is a paragraph apart
            text.Append(group != 0 && group == previous ? "\n" : text.Length > 0 ? "\n\n" : "").Append(block);
            previous = group;
        }
        return PyText.Strip(text.ToString()) + "\n";
    }

    private static string Repeat(string text, int times) => times <= 0 ? "" : string.Concat(Enumerable.Repeat(text, times));

    private sealed class BulletsParser : HtmlParser
    {
        private int depth;
        private (int Depth, List<string> Parts)? current;

        public List<string> Lines { get; } = [];

        public void Flush()
        {
            if (current is { } item)
            {
                var text = PyText.Strip(PySpaceRun().Replace(string.Concat(item.Parts), " "));
                if (text.Length > 0)
                    Lines.Add(Repeat("  ", item.Depth - 1) + "- " + text);
            }
            current = null;
        }

        protected override void HandleStartTag(string tag, IReadOnlyList<(string Name, string? Value)> attrs)
        {
            if (tag is "ul" or "ol")
            {
                Flush();
                depth++;
            }
            else if (tag == "li")
            {
                Flush();
                current = (depth, []);
            }
            else if (tag == "br" && current is { } item)
                item.Parts.Add(" ");
        }

        protected override void HandleEndTag(string tag)
        {
            if (tag is "ul" or "ol")
            {
                Flush();
                depth--;
            }
            else if (tag == "li")
                Flush();
        }

        protected override void HandleData(string data)
        {
            if (current is { } item && depth != 0)
                item.Parts.Add(data);
        }
    }

    /// <summary>Block structure and inline emphasis; anything else keeps its text.</summary>
    private sealed class MarkdownParser : HtmlParser
    {
        private static readonly string[] Blocks = ["p", "div", "h1", "h2", "h3", "h4", "blockquote"];

        public List<(int Group, string Text)> Out { get; } = [];   // finished blocks: (list group, 0 for none; text)

        private List<string> line = [];         // the block being built
        private string prefix = "";             // its marker: "  - ", "1. ", "## "
        private readonly List<string> lists = []; // "ul" / "ol" stack
        private readonly List<int> counters = [];
        private int group;                      // each top-level list is a group, so two lists don't merge
        private string? href;

        public void EndBlock()
        {
            var text = PyText.Strip(SpacesAndTabs().Replace(string.Concat(line), " ")).Replace(Break.ToString(), "  \n");
            text = SpacedNewline().Replace(text, "\n").Replace("\n", "  \n").Replace("    \n", "  \n");
            if (text.Length > 0)
                Out.Add((lists.Count > 0 ? group : 0, prefix + text));
            line = [];
            prefix = "";
        }

        protected override void HandleStartTag(string tag, IReadOnlyList<(string Name, string? Value)> attrs)
        {
            if (Blocks.Contains(tag))
            {
                EndBlock();
                if (tag.StartsWith('h'))
                    prefix = new string('#', tag[1] - '0') + " ";
            }
            else if (tag is "ul" or "ol")
            {
                EndBlock();
                if (lists.Count == 0)
                    group++;
                lists.Add(tag);
                counters.Add(0);
            }
            else if (tag == "li")
            {
                EndBlock();
                var indent = Repeat("  ", lists.Count - 1);
                if (lists.Count > 0 && lists[^1] == "ol")
                {
                    counters[^1]++;
                    prefix = $"{indent}{counters[^1]}. ";
                }
                else
                    prefix = $"{indent}- ";
            }
            else if (tag == "br")
                line.Add(Break.ToString());
            else if (tag is "b" or "strong")
                line.Add("**");
            else if (tag is "i" or "em")
                line.Add("*");
            else if (tag == "a")
            {
                href = null;   // dict(attrs).get("href"): the last one wins
                foreach (var (name, value) in attrs)
                    if (name == "href")
                        href = value;
                line.Add("[");
            }
        }

        protected override void HandleEndTag(string tag)
        {
            if (Blocks.Contains(tag) || tag == "li")
                EndBlock();
            else if (tag is "ul" or "ol")
            {
                EndBlock();
                if (lists.Count > 0)
                {
                    lists.RemoveAt(lists.Count - 1);
                    counters.RemoveAt(counters.Count - 1);
                }
            }
            else if (tag is "b" or "strong")
                line.Add("**");
            else if (tag is "i" or "em")
                line.Add("*");
            else if (tag == "a")
            {
                line.Add(!string.IsNullOrEmpty(href) ? $"]({href})" : "]");
                href = null;
            }
        }

        protected override void HandleData(string data) => line.Add(data.Replace('\n', ' '));
    }
}
