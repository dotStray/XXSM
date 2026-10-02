namespace Xxsm.Core.Io;

/// <summary>Opens a web address in the user's browser.</summary>
public interface IUrlLauncher
{
    /// <summary>Opens <paramref name="url"/> in the default browser.</summary>
    /// <param name="url">The address to open. Only <c>http</c> and <c>https</c> are accepted.</param>
    /// <param name="cancellationToken">Cancels before the handler is started.</param>
    /// <returns>A task that completes once the handler has been started.</returns>
    /// <exception cref="ModOperationException">The address is not a web address, or nothing is registered to open one;
    /// with the operating system's words.</exception>
    Task OpenAsync(string url, CancellationToken cancellationToken = default);
}
