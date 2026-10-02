using System.Text.Json;
using Serilog;
using Xxsm.Packs.Serialization;

namespace Xxsm.Packs.Registry;

/// <summary>The default <see cref="IRegistryClient"/>.</summary>
public sealed class RegistryClient(IPackResourceFetcher fetcher, ILogger logger) : IRegistryClient
{
    /// <summary>The file a registry folder is expected to contain.</summary>
    public const string IndexFileName = "index.json";

    private readonly IPackResourceFetcher _fetcher = fetcher;
    private readonly ILogger _logger = logger.ForContext<RegistryClient>();

    /// <inheritdoc />
    public Uri ResolveIndexUri(string registry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registry);

        if (Uri.TryCreate(registry, UriKind.Absolute, out var absolute) && !absolute.IsFile)
        {
            return absolute.AbsolutePath.EndsWith('/')
                ? new Uri(absolute, IndexFileName)
                : absolute;
        }

        var path = absolute?.IsFile == true ? absolute.LocalPath : registry;
        var full = Path.GetFullPath(path);

        if (Directory.Exists(full))
        {
            full = Path.Combine(full, IndexFileName);
        }

        return new Uri(full);
    }

    /// <inheritdoc />
    public async Task<RegistryFetch> FetchAsync(
        string registry, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registry);

        var uri = ResolveIndexUri(registry);

        // A folder here, or https: plain http could be changed in transit, checksums included.
        if (!uri.IsFile && !Xxsm.Core.Io.UntrustedLocation.IsWebAddress(uri.OriginalString, out _))
        {
            throw new PackRegistryException(
                $"'{registry}' is not a folder on this computer or an https address, so it was not asked. " +
                "Use the https address of the pack source.");
        }

        var json = await _fetcher.ReadTextAsync(uri, cancellationToken).ConfigureAwait(false);

        RegistryIndex? index;

        try
        {
            index = JsonSerializer.Deserialize(json, PackJsonContext.Default.RegistryIndex);
        }
        catch (JsonException ex)
        {
            throw new PackRegistryException(
                $"'{uri}' is not a readable registry: {ex.Message}", ex);
        }

        if (index is null)
        {
            throw new PackRegistryException($"'{uri}' contained no registry at all.");
        }

        _logger.Information(
            "Fetched registry {Registry}: {Packs} pack(s)", uri, index.Packs.Count);

        return new RegistryFetch(registry, uri, index);
    }
}
