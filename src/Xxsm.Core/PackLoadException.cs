namespace Xxsm.Core;

/// <summary>Thrown when a Game Pack cannot be loaded at all; problems in a usable pack are diagnostics.</summary>
public sealed class PackLoadException : XxsmException
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="message">The message, written to be shown to a user unedited.</param>
    /// <param name="path">The pack directory or file that failed.</param>
    /// <param name="innerException">The underlying failure.</param>
    public PackLoadException(string message, string? path = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Path = path;
    }

    /// <summary>The pack directory or file the failure relates to.</summary>
    public string? Path { get; }
}
