using System.Text;
using PyCompat;

namespace TeamsChannelSync;

/// <summary>The two bits of Python's text behaviour this tool needs beyond the library's PyStr: \s as a regex
/// (for the PyRegex translator), and read_text's decode error as the ValueError the command line reports.</summary>
public static class PyText
{
    /// <summary>A regex class for Python's \s: .NET's \s is char.IsWhiteSpace, and Python also counts
    /// U+001C to U+001F (the information separators) as space.</summary>
    public const string Space = "[" + PyStr.SpaceClass + "]";

    /// <summary>path.read_text(encoding="utf-8") (PyStr.ReadText), with invalid UTF-8 raised as Python's
    /// UnicodeDecodeError, a ValueError.</summary>
    public static string ReadText(string path)
    {
        try
        {
            return PyStr.ReadText(path);
        }
        catch (DecoderFallbackException error)
        {
            throw new PyValueError($"'utf-8' codec can't decode {Path.GetFileName(path)}: {error.Message}");
        }
    }
}

/// <summary>What Python raises as ValueError (including tomllib's and json's decode errors): the command line
/// reports it and exits 2.</summary>
public class PyValueError(string message, Exception? inner = null) : Exception(message, inner);
