namespace Xxsm.Core.Profiles;

/// <summary>Thrown when a profile cannot be saved, changed or found: a taken name, read-only, or no such id.</summary>
public sealed class ProfileException : XxsmException
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="message">The message, written to be shown to a user unedited.</param>
    /// <param name="innerException">The underlying failure, if any.</param>
    public ProfileException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
