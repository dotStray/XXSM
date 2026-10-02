namespace Xxsm.Core.Ini;

/// <summary>Thrown when an INI file cannot be changed as asked. Nothing on disk was touched.</summary>
public sealed class IniWriteException : XxsmException
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="message">The message, written to be shown to a user unedited.</param>
    /// <param name="path">The INI file involved, when known.</param>
    /// <param name="line">The 1-based line involved, or 0.</param>
    /// <param name="innerException">The underlying failure.</param>
    public IniWriteException(string message, string? path = null, int line = 0, Exception? innerException = null)
        : base(message, innerException)
    {
        Path = path;
        Line = line;
    }

    /// <summary>The INI file involved, when known.</summary>
    public string? Path { get; }

    /// <summary>The 1-based line involved, or 0 when the problem is not about one line.</summary>
    public int Line { get; }
}
