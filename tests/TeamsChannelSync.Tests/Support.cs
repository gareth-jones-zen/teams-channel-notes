using System.Reflection;
using System.Text.Json;

namespace TeamsChannelSync.Tests;

/// <summary>One converter golden: the HTML, and what Python's to_bullets and to_markdown made of it.</summary>
public sealed record GoldenCase(string Name, string Input, string Bullets, string Markdown);

public static class Golden
{
    public static string RepoRoot =>
        typeof(Golden).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "RepoRoot").Value!;

    /// <summary>%LOCALAPPDATA%\teams-channel-notes-port: the recorded fixtures and the goldens made from real posts
    /// (never committed). Tests that need them are skipped where they aren't, as in CI.</summary>
    public static string LocalPort =>
        Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", "teams-channel-notes-port");

    public static (string Python, List<GoldenCase> Cases) Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var cases = root.GetProperty("cases").EnumerateArray().Select(c => new GoldenCase(
            c.GetProperty("name").GetString()!, c.GetProperty("input").GetString()!,
            c.GetProperty("bullets").GetString()!, c.GetProperty("markdown").GetString()!)).ToList();
        return (root.GetProperty("python").GetString()!, cases);
    }

    public static string Committed(string name) => Path.Combine(AppContext.BaseDirectory, "Golden", name);

    /// <summary>A temporary folder, deleted when disposed.</summary>
    public sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tcn-tests-" + Guid.NewGuid().ToString("N")[..10]);
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
