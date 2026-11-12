namespace TeamsChannelNotes.Tests;

/// <summary>The converter against Python's own output (tests/parity/make_goldens.py), byte for byte.</summary>
public class ConvertTests
{
    public static TheoryData<string> CorpusNames() =>
        new(Golden.Load(Golden.Committed("convert.json")).Cases.Select(c => c.Name));

    private static readonly Dictionary<string, GoldenCase> Corpus =
        Golden.Load(Golden.Committed("convert.json")).Cases.ToDictionary(c => c.Name);

    [Theory]
    [MemberData(nameof(CorpusNames))]
    public void Corpus_matches_python(string name)
    {
        var golden = Corpus[name];
        Assert.Equal(golden.Bullets, NoteFormats.ToBullets(golden.Input));
        Assert.Equal(golden.Markdown, NoteFormats.ToMarkdown(golden.Input));
    }

    [Fact]
    public void Committed_fuzz_matches_python()
    {
        var (_, cases) = Golden.Load(Golden.Committed("fuzz.json"));
        Assert.Equal(300, cases.Count);
        var failures = Differences(cases);
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(5)));
    }

    [Fact]
    public void Goldens_came_from_the_python_the_task_runs()
    {
        var (python, _) = Golden.Load(Golden.Committed("convert.json"));
        Assert.Equal("3.14.5", python);
        Assert.Equal(Html5Entities.PythonVersion, python);
    }

    /// <summary>Local only: 10,000 seeded fragments, and every post in the channel's recorded history.</summary>
    [Theory]
    [InlineData("fuzz-10000.json")]
    [InlineData("history.json")]
    public void Local_goldens_match_python(string file)
    {
        var path = Path.Combine(Golden.LocalPort, "goldens", file);
        Assert.SkipUnless(File.Exists(path), $"{path} isn't here (it's made locally from recorded posts)");
        var (_, cases) = Golden.Load(path);
        var failures = Differences(cases);
        Assert.True(failures.Count == 0, $"{failures.Count} of {cases.Count} differ:\n" + string.Join("\n", failures.Take(5)));
    }

    internal static List<string> Differences(List<GoldenCase> cases)
    {
        var failures = new List<string>();
        foreach (var c in cases)
        {
            var bullets = NoteFormats.ToBullets(c.Input);
            var markdown = NoteFormats.ToMarkdown(c.Input);
            if (bullets != c.Bullets)
                failures.Add($"{c.Name} bullets: expected {Show(c.Bullets)} got {Show(bullets)}");
            if (markdown != c.Markdown)
                failures.Add($"{c.Name} markdown: expected {Show(c.Markdown)} got {Show(markdown)}");
        }
        return failures;
    }

    private static string Show(string text) => "\"" + string.Concat(text.Select(ch => ch is >= ' ' and <= '~' ? ch.ToString() : $"<U+{(int)ch:X4}>")) + "\"";

    [Fact]
    public void Python_whitespace_table()
    {
        Assert.Equal("3.14.5", PythonWhitespace.PythonVersion);
        var expected = PythonWhitespace.CodePoints.ToHashSet();
        for (var c = 0; c <= 0xFFFF; c++)
            Assert.True(expected.Contains(c) == PyText.IsSpace((char)c), $"U+{c:X4}");
    }

    [Theory]
    [InlineData("  a b  ", "a b")]
    [InlineData("\u001c\u2028a　", "a")]
    [InlineData("", "")]
    [InlineData("​", "​")]
    public void Strip_is_pythons(string text, string expected) => Assert.Equal(expected, PyText.Strip(text));

    [Fact]
    public void Collapse_is_pythons_split_and_join() =>
        Assert.Equal("a b c", PyText.Collapse(" a\u001f\u001fb\r\n\u0085c "));
}
