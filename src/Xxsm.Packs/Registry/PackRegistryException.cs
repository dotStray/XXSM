using Xxsm.Core;

namespace Xxsm.Packs.Registry;

/// <summary>A registry could not be reached, or what it returned could not be used.</summary>
public sealed class PackRegistryException : XxsmException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong, in words a user can act on.</param>
    /// <param name="innerException">The underlying failure, when there was one.</param>
    public PackRegistryException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
