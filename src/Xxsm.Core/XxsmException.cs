namespace Xxsm.Core;

/// <summary>Base class for every exception XXSM raises deliberately.</summary>
/// <remarks>Its message is shown to the user as it is, with the operating system's own words where there are
/// any.</remarks>
public abstract class XxsmException : Exception
{
    /// <summary>Initialises a new instance with a user-facing message.</summary>
    /// <param name="message">The message, written to be shown to a user unedited.</param>
    protected XxsmException(string message) : base(message)
    {
    }

    /// <summary>Initialises a new instance with a user-facing message and the underlying cause.</summary>
    /// <param name="message">The message, written to be shown to a user unedited.</param>
    /// <param name="innerException">The underlying failure, usually an OS-level exception.</param>
    protected XxsmException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}

/// <summary>Thrown when an operation on a mod, or on a user's files, cannot be completed.</summary>
public sealed class ModOperationException : XxsmException
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="message">The message, written to be shown to a user unedited.</param>
    /// <param name="path">The path the operation was acting on, when there is one.</param>
    /// <param name="innerException">The underlying failure, usually an OS-level exception.</param>
    public ModOperationException(string message, string? path = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Path = path;
    }

    /// <summary>The path the failed operation was acting on, when known.</summary>
    public string? Path { get; }
}
