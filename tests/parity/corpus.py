r"""The synthetic HTML corpus for the converter's goldens, and the seeded fuzz generator.

Nothing here comes from a real post. Each case is (name, html); make_goldens.py runs the Python converter over
them and saves what it produced, which the .NET tests must reproduce exactly.
"""

from __future__ import annotations

import random

NBSP = " "
LS = " "

CASES: list[tuple[str, str]] = [
    # the shape of the real posts
    ("exco-shape", '<p>Exco notes</p><p>&nbsp;</p><ul><li style="x">Consumer\n<ul><li>Orders <i>st</i> up 3%</li>'
                   '<li>Churn &amp; cancels</li></ul></li><li>Business</li></ul><p>Have a great weekend!</p>'),
    ("exco-emoji", "<ul><li>Great week \U0001F60A</li><li>John’s note</li></ul>"),
    ("exco-nested-text-after", "<ul><li>Top<ul><li>Child</li></ul>Text after the nested list</li><li>Next</li></ul>"),
    ("empty", ""),
    ("just-text", "Hello world"),
    ("just-text-amp", "Fish &amp chips"),
    ("trailing-amp", "<p>Tom & Jerry &am"),
    ("trailing-amp-space", "<p>Tom & Jerry &am "),
    ("trailing-amp-semicolon", "<ul><li>a</li></ul>x &amp;"),
    ("trailing-lt", "<ul><li>a</li></ul><"),
    ("trailing-unfinished-tag", "<ul><li>a</li><li>b</li></ul><p class='x"),
    ("trailing-unfinished-endtag", "<ul><li>a</li><li>b</ul"),
    ("lt-in-text", "<ul><li>a < b and c<d</li></ul>"),
    ("lt-space", "<p>1 < 2</p><ul><li>x <= y</li></ul>"),
    # lists
    ("ol-simple", "<ol><li>one</li><li>two</li><li>three</li></ol>"),
    ("ol-nested-ul", "<ol><li>one<ul><li>a</li><li>b</li></ul></li><li>two</li></ol>"),
    ("ul-nested-ol", "<ul><li>x<ol><li>first</li><li>second</li></ol></li></ul>"),
    ("deep-nesting", "<ul><li>1<ul><li>2<ul><li>3<ul><li>4<ul><li>5</li></ul></li></ul></li></ul></li></ul></li></ul>"),
    ("empty-list", "<ul></ul><p>after</p>"),
    ("empty-items", "<ul><li></li><li> </li><li>&nbsp;</li><li>real</li></ul>"),
    ("two-lists", "<ul><li>a</li></ul><p>between</p><ul><li>b</li></ul>"),
    ("two-lists-adjacent", "<ul><li>a</li></ul><ul><li>b</li></ul>"),
    ("li-outside-list", "<li>orphan</li><ul><li>in</li></ul>"),
    ("stray-close-ul", "</ul><ul><li>a</li></ul></ul></ul><li>b</li>"),
    ("unclosed-li", "<ul><li>a<li>b<li>c</ul>"),
    ("unclosed-ul", "<ul><li>a</li><li>b"),
    ("uppercase-tags", "<UL><LI>Upper</LI><Li>Mixed</lI></UL><P>Para</P>"),
    ("ol-counters-reset", "<ol><li>a</li><li>b</li></ol><ol><li>c</li></ol>"),
    ("ol-nested-counters", "<ol><li>a<ol><li>x</li><li>y</li></ol></li><li>b</li></ol>"),
    # breaks
    ("br", "<p>line one<br>line two</p>"),
    ("br-self-closing", "<p>line one<br/>line two<br />three</p>"),
    ("br-in-li", "<ul><li>first<br>second</li></ul>"),
    ("br-leading-trailing", "<p><br>middle<br></p>"),
    ("br-double", "<p>a<br><br>b</p>"),
    ("br-with-spaces", "<p>a  <br>  b</p>"),
    ("raw-line-separator", f"<p>a{LS}b</p><ul><li>c{LS}d</li></ul>"),
    ("br-outside-li", "<ul><br><li>a</li></ul>"),
    # emphasis and links
    ("bold", "<p><b>bold</b> and <strong>strong</strong></p>"),
    ("italic", "<p><i>it</i> and <em>em</em></p>"),
    ("empty-bold", "<p>a<b> </b>b<strong></strong>c</p>"),
    ("bold-in-li", "<ul><li><b>Key:</b> value</li></ul>"),
    ("link", '<p>See <a href="https://example.com/x?a=1&amp;b=2">the page</a>.</p>'),
    ("link-no-href", "<p><a>no href</a></p>"),
    ("link-empty-href", '<p><a href="">empty</a></p>'),
    ("link-valueless-href", "<p><a href>valueless</a></p>"),
    ("link-duplicate-href", '<p><a href="one" href="two">dup</a></p>'),
    ("link-entity-in-href", '<p><a href="a&lt;b&gt;c&#33;&copy=1&notit;">x</a></p>'),
    ("link-single-quoted", "<p><a href='single'>s</a></p>"),
    ("link-bare-href", "<p><a href=bare>b</a></p>"),
    ("link-in-li", '<ul><li><a href="u">text</a> after</li></ul>'),
    ("link-uppercase-attr", '<p><a HREF="UP">u</a></p>'),
    ("link-nested", '<p><a href="1">a<a href="2">b</a>c</a></p>'),
    # headings and blocks
    ("headings", "<h1>One</h1><h2>Two</h2><h3>Three</h3><h4>Four</h4><h5>Five</h5>"),
    ("div-blockquote", "<div>div</div><blockquote>quote</blockquote><div><div>nested</div></div>"),
    ("p-in-li", "<ul><li><p>para in li</p></li></ul>"),
    ("whitespace-collapse", "<p>  lots   of\t\tspace\n\nand lines  </p>"),
    ("whitespace-cr", "<p>a\r\nb\rc</p><ul><li>x\r\ny</li></ul>"),
    # entities
    ("entities-named", "<p>&amp; &lt; &gt; &quot; &apos; &copy; &nbsp;x &hellip; &mdash;</p>"),
    ("entities-no-semicolon", "<p>&amp &lt &copy &notin &notit &ampx</p>"),
    ("entities-numeric", "<p>&#65; &#x41; &#X42; &#150; &#x80; &#0; &#xD800; &#x110000; &#1; &#x0B; &#xFFFE; &#13;</p>"),
    ("entities-numeric-no-semicolon", "<p>&#65 &#x41x &#99999999999999999999;</p>"),
    ("entities-unknown", "<p>&nosuch; &; &# &#x; &#; &x</p>"),
    ("entities-long", "<p>&CounterClockwiseContourIntegral; &CounterClockwiseContourIntegralX;</p>"),
    ("entities-astral", "<p>&Afr; &fopf; &bopf;</p>"),
    ("entities-in-li", "<ul><li>R&amp;D &ndash; Q3</li></ul>"),
    ("entity-nbsp-vs-raw", f"<ul><li>a&nbsp;b{NBSP}c</li></ul><p>d&nbsp;&nbsp;e{NBSP}f</p>"),
    ("entities-emoji-run", "<p>&a\U0001F60A\U0001F60A;</p>"),
    # markup that's skipped
    ("comment", "<ul><li>a<!-- hidden --></li><!-- x --><li>b</li></ul>"),
    ("comment-abrupt", "<p>a<!-->b</p><p>c<!--->d</p>"),
    ("comment-unclosed", "<ul><li>a</li></ul><!-- never closed"),
    ("comment-bang", "<p>a<!-- x --!>b</p>"),
    ("doctype", "<!DOCTYPE html><ul><li>a</li></ul>"),
    ("doctype-lower", "<!doctype html><p>p</p>"),
    ("cdata", "<p>a<![CDATA[ x < y ]]>b</p>"),
    ("bogus-comment", "<p>a<!bogus>b</p><p>c</?pi>d</p>"),
    ("pi", "<?xml version='1.0'?><ul><li>a</li></ul>"),
    ("empty-endtag", "<p>a</>b</p>"),
    ("endtag-not-letter", "<p>a</1>b</p>"),
    ("script", "<ul><li>a<script>var x = '<li>not</li>';</script>b</li></ul>"),
    ("style", "<style>li { color: red }</style><ul><li>a</li></ul>"),
    ("script-uppercase-close", "<script>x</SCRIPT ><ul><li>a</li></ul>"),
    ("script-unclosed", "<ul><li>a</li></ul><script>never"),
    ("title-rcdata", "<title>A &amp; B</title><ul><li>a</li></ul>"),
    ("textarea", "<ul><li><textarea>x &lt; <b>y</b></textarea>z</li></ul>"),
    ("plaintext", "<ul><li>a<plaintext><li>b</li> &amp;</ul>"),
    ("xmp", "<xmp><b>raw</b></xmp><p>after</p>"),
    # attributes and odd tags
    ("attrs-messy", '<ul><li class="a" id=b data-x=\'c\' hidden>a</li><li  style = "x" >b</li></ul>'),
    ("attr-gt-in-quotes", '<p title="a>b">x</p>'),
    ("tag-with-slash-attr", "<p/class=x>y</p>"),
    ("tag-junk-after", '<p "junk">x</p>'),
    ("tag-name-weird", "<p:x>a</p:x><li-x>b</li-x>"),
    ("emoji-tag", '<p>Done <emoji alt="\U0001F44D" id="x"></emoji> ok</p><ul><li>a <emoji alt="\U0001F389"></emoji></li></ul>'),
    ("span-styled", '<ul><li><span style="font-size:inherit">styled</span> text</li></ul>'),
    ("img", '<p>a<img src="x.png" alt="pic">b</p>'),
    ("unicode-text", "<ul><li>Café 中文 नमस्ते</li></ul>"),
    ("info-separators", "<ul><li>a\x1cb\x1dc\x1ed\x1fe</li></ul><p>f\x1cg</p>"),
    ("other-spaces", "<ul><li>a b　c d\u0085e\x0bf\x0cg</li></ul><p>h i\x0bj</p>"),
    ("bom", "﻿<ul><li>bom</li></ul>"),
    ("bom-inside", "<ul><li>a﻿b</li></ul>"),
    ("zero-width", "<ul><li>a​b‍c</li></ul>"),
    ("tabs-markdown", "<p>a\t\tb</p>"),
    ("only-spaces-li", "<ul><li>   </li></ul>"),
    ("kelvin-tag", "<blocKquote>k</blocKquote><p>p</p>"),
    ("dotted-i-tag", "<Lİ>x</Lİ><ul><li>y</li></ul>"),
    ("long-s-script", "<ſcript>x</ſcript><p>after</p>"),
    ("star-text", "<p>**already**</p><p>* star</p>"),
    ("bold-empty-across", "<p><b> </b><i></i></p>"),
    ("hash-text", "<p># not a heading</p>"),
    ("leading-space-break", "<p> <br> a</p>"),
    ("many-spaces-before-break", "<p>a    <br>    b</p>"),
    ("markdown-spaced-newline", "<p>a <br> <br> b</p>"),
    ("li-then-p", "<ul><li>a</li></ul><p>p</p><ol><li>b</li></ol>"),
    ("div-in-list", "<ul><li>a</li><div>d</div><li>b</li></ul>"),
    ("nested-empty-ul", "<ul><li>a<ul></ul></li></ul>"),
    ("closing-li-twice", "<ul><li>a</li></li><li>b</li></ul>"),
    ("depth-negative", "</ul></ul><li>x</li><ul><li>y</li></ul>"),
    ("h-in-li", "<ul><li><h2>head</h2>tail</li></ul>"),
    ("near-end-amp-34", "<ul><li>a</li></ul>" + "x" * 40 + "&amp"),
    ("near-end-amp-33", "<ul><li>a</li></ul>" + "&amp" + "y" * 31),
    ("near-end-amp-35", "<ul><li>a</li></ul>" + "&amp" + "y" * 33),
    ("near-end-amp-emoji", "<ul><li>a</li></ul>" + "&amp" + "\U0001F600" * 20),
    ("near-end-amp-emoji-31", "<ul><li>a</li></ul>" + "&amp" + "\U0001F600" * 31),
    ("text-only-trailing-amp", "plain & text"),
    ("text-amp-at-end", "plain text &"),
]

TAGS = ["ul", "ol", "li", "p", "div", "br", "b", "strong", "i", "em", "a", "h1", "h2", "h3", "h4", "blockquote",
        "span", "UL", "Li", "script", "style", "title", "emoji", "img"]
TEXTS = ["Orders", "Consumer", "net adds", "3%", "John’s", "\U0001F60A", "café", "  ", "\n", "\t", NBSP, LS,
         "\x1c", " ", "a", "Z", "**", "*", "[", "]", "(", ")", "#", "-", "1.", "x\r\ny"]
ENTITIES = ["&amp;", "&amp", "&lt;", "&gt", "&nbsp;", "&nbsp", "&copy;", "&notit;", "&notin", "&#150;", "&#x41;",
            "&#65", "&#0;", "&#xD800;", "&hellip;", "&ndash;", "&rsquo;", "&;", "&#;", "&", "&Afr;", "&zz;"]
ATTRS = ['', ' class="x"', " style='a:b'", ' href="https://e.com/?a=1&amp;b=2"', " href", " href=''",
         ' alt="\U0001F44D"', " data-x=y", ' HREF="Up"', " id=a/"]
JUNK = ["<!-- c -->", "<!--", "<!x>", "<?pi?>", "</>", "</ 1>", "<", "<<", "< ", "<!DOCTYPE x>", "<![CDATA[z]]>", "-->"]


def fuzz(count: int, seed: int) -> list[tuple[str, str]]:
    """count random Teams-like fragments: tags (open, close, self-closing, with attributes), entities, text with
    the whitespace Python and .NET disagree about, and junk markup."""
    rng = random.Random(seed)
    cases = []
    for n in range(count):
        parts = []
        for _ in range(rng.randint(1, 40)):
            kind = rng.random()
            if kind < 0.35:
                tag = rng.choice(TAGS)
                style = rng.random()
                if style < 0.55:
                    parts.append(f"<{tag}{rng.choice(ATTRS)}>")
                elif style < 0.9:
                    parts.append(f"</{tag}>")
                else:
                    parts.append(f"<{tag}{rng.choice(ATTRS)}/>")
            elif kind < 0.7:
                parts.append(rng.choice(TEXTS))
            elif kind < 0.9:
                parts.append(rng.choice(ENTITIES))
            else:
                parts.append(rng.choice(JUNK))
        cases.append((f"fuzz-{seed}-{n:05d}", "".join(parts)))
    return cases
