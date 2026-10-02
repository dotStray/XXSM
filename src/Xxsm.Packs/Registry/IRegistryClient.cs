namespace Xxsm.Packs.Registry;

/// <summary>Fetches and parses a registry's <c>index.json</c>.</summary>
public interface IRegistryClient
{
    /// <summary>Turns a configured registry (a URL, a folder or an <c>index.json</c>) into its index's URI.</summary>
    /// <exception cref="PackRegistryException">It is not a usable location.</exception>
    Uri ResolveIndexUri(string registry);

    /// <summary>Fetches one registry.</summary>
    /// <param name="registry">A URL, or a path to a folder or an <c>index.json</c>.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <exception cref="PackRegistryException">It could not be reached, or what came back was not a
    /// registry.</exception>
    Task<RegistryFetch> FetchAsync(string registry, CancellationToken cancellationToken = default);
}

/// <summary>One registry, as fetched.</summary>
/// <param name="Registry">The registry as the user configured it.</param>
/// <param name="IndexUri">The absolute location the index was read from.</param>
/// <param name="Index">What it contained.</param>
public sealed record RegistryFetch(string Registry, Uri IndexUri, RegistryIndex Index);
