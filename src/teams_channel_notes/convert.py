"""Turn a Teams channel post's HTML body into markdown.

Two formats, chosen per source:

- "bullets": only the post's lists, as plain nested bullets with a two-space indent. Paragraphs
  outside the lists (a "Notes" header, a "Have a great weekend!" sign-off) are dropped. Written for
  weekly notes posted as one nested list; entities are converted, emphasis dropped, emoji and
  wording kept, and links reduced to their text.
- "markdown": the whole post: paragraphs, line breaks, nested lists, bold and italic, links as
  [text](url).
"""

from __future__ import annotations

import html.parser
import re


class _Bullets(html.parser.HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.depth, self.lines, self.current = 0, [], None

    def flush(self):
        if self.current is not None:
            depth, parts = self.current
            text = re.sub(r"\s+", " ", "".join(parts)).strip()
            if text:
                self.lines.append("  " * (depth - 1) + "- " + text)
        self.current = None

    def handle_starttag(self, tag, attrs):
        if tag in ("ul", "ol"):
            self.flush()
            self.depth += 1
        elif tag == "li":
            self.flush()
            self.current = (self.depth, [])
        elif tag == "br" and self.current:
            self.current[1].append(" ")

    def handle_endtag(self, tag):
        if tag in ("ul", "ol"):
            self.flush()
            self.depth -= 1
        elif tag == "li":
            self.flush()

    def handle_data(self, data):
        if self.current is not None and self.depth:
            self.current[1].append(data)


_BREAK = " "   # a <br> while collapsing whitespace; becomes a markdown hard break at the end


class _Markdown(html.parser.HTMLParser):
    """Block structure and inline emphasis; anything else keeps its text."""

    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.out: list[tuple[int, str]] = []   # finished blocks: (list group, 0 for none; text)
        self.line: list[str] = []              # the block being built
        self.prefix = ""                       # its marker: "  - ", "1. ", "## "
        self.lists: list[str] = []             # "ul" / "ol" stack
        self.counters: list[int] = []
        self.group = 0                         # each top-level list is a group, so two lists don't merge
        self.href: str | None = None

    def _end_block(self):
        text = re.sub(r"[ \t]+", " ", "".join(self.line)).strip().replace(_BREAK, "  \n")
        text = re.sub(r" *\n *", "\n", text).replace("\n", "  \n").replace("    \n", "  \n")
        if text:
            self.out.append((self.group if self.lists else 0, self.prefix + text))
        self.line, self.prefix = [], ""

    def handle_starttag(self, tag, attrs):
        if tag in ("p", "div", "h1", "h2", "h3", "h4", "blockquote"):
            self._end_block()
            if tag.startswith("h"):
                self.prefix = "#" * int(tag[1]) + " "
        elif tag in ("ul", "ol"):
            self._end_block()
            if not self.lists:
                self.group += 1
            self.lists.append(tag)
            self.counters.append(0)
        elif tag == "li":
            self._end_block()
            indent = "  " * (len(self.lists) - 1)
            if self.lists and self.lists[-1] == "ol":
                self.counters[-1] += 1
                self.prefix = f"{indent}{self.counters[-1]}. "
            else:
                self.prefix = f"{indent}- "
        elif tag == "br":
            self.line.append(_BREAK)
        elif tag in ("b", "strong"):
            self.line.append("**")
        elif tag in ("i", "em"):
            self.line.append("*")
        elif tag == "a":
            self.href = dict(attrs).get("href")
            self.line.append("[")

    def handle_endtag(self, tag):
        if tag in ("p", "div", "h1", "h2", "h3", "h4", "blockquote", "li"):
            self._end_block()
        elif tag in ("ul", "ol"):
            self._end_block()
            if self.lists:
                self.lists.pop()
                self.counters.pop()
        elif tag in ("b", "strong"):
            self.line.append("**")
        elif tag in ("i", "em"):
            self.line.append("*")
        elif tag == "a":
            self.line.append(f"]({self.href})" if self.href else "]")
            self.href = None

    def handle_data(self, data):
        self.line.append(data.replace("\n", " "))


def to_bullets(content: str) -> str:
    parser = _Bullets()
    parser.feed(content.replace(" ", " "))
    parser.flush()
    return "\n".join(parser.lines) + "\n"


def to_markdown(content: str) -> str:
    parser = _Markdown()
    parser.feed(content.replace(" ", " "))
    parser._end_block()
    text, previous = "", None
    for group, block in parser.out:
        block = re.sub(r"\*\*\s*\*\*", "", block)
        # items of the same list sit on consecutive lines; everything else is a paragraph apart
        text += ("\n" if (group and group == previous) else ("\n\n" if text else "")) + block
        previous = group
    return text.strip() + "\n"


FORMATS = {"bullets": to_bullets, "markdown": to_markdown}
