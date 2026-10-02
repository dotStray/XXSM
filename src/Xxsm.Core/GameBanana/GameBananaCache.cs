using System.Globalization;
using Serilog;
using Xxsm.Core.Io;

namespace Xxsm.Core.GameBanana;

/// <summary>The on-disk cache of GameBanana responses, keyed by what was asked for.</summary>
public interface IGameBananaCache
{
    /// <summary>The folder the cached responses sit in.</summary>
    string Directory { get; }

    /// <summary>Reads a cached response still young enough. Never throws: a damaged entry is a miss.</summary>
    /// <param name="key">What was asked for, for example <c>541886-profile</c>.</param>
    /// <param name="lifetime">How old an entry may be. Zero or less always misses.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The response body, or null for a miss.</returns>
    Task<string?> ReadAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken = default);

    /// <summary>Stores a response. Never throws: a cache that cannot be written is only slower.</summary>
    /// <param name="key">What was asked for.</param>
    /// <param name="body">The response body, verbatim.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task WriteAsync(string key, string body, CancellationToken cancellationToken = default);

    /// <summary>Forgets cached responses.</summary>
    /// <param name="modId">The mod to forget, or null for every mod.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    Task<int> ForgetAsync(long? modId = null, CancellationToken cancellationToken = default);

    /// <summary>The cache key for a mod's profile page.</summary>
    static string ProfileKey(long modId) =>
        $"{modId.ToString(CultureInfo.InvariantCulture)}-profile";

    /// <summary>The cache key for a mod's download page.</summary>
    static string DownloadKey(long modId) =>
        $"{modId.ToString(CultureInfo.InvariantCulture)}-download";
}

/// <summary>The default <see cref="IGameBananaCache"/>: one file per response under the cache root.</summary>
public sealed class GameBananaCache(IAppPaths paths, ILogger logger, TimeProvider? time = null) : IGameBananaCache
{
    private readonly ILogger _logger = logger.ForContext<GameBananaCache>();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <inheritdoc />
    public string Directory { get; } = Path.Combine(paths.CacheDirectory, "gamebanana");

    /// <inheritdoc />
    public async Task<string?> ReadAsync(
        string key, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (lifetime <= TimeSpan.Zero)
        {
            return null;
        }

        var path = PathFor(key);

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var age = _time.GetUtcNow() - File.GetLastWriteTimeUtc(path);

            // A negative age is a clock that moved: stale, not fresh.
            if (age < TimeSpan.Zero || age > lifetime)
            {
                return null;
            }

            var body = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

            return body.Length == 0 ? null : body;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Debug(ex, "Could not read the cached GameBanana response {Path}; refetching", path);

            return null;
        }
    }

    /// <inheritdoc />
    public async Task WriteAsync(string key, string body, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(body);

        var path = PathFor(key);

        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            await AtomicFile.WriteAllTextAsync(path, body, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Debug(ex, "Could not cache the GameBanana response at {Path}", path);
        }
    }

    /// <inheritdoc />
    public Task<int> ForgetAsync(long? modId = null, CancellationToken cancellationToken = default)
    {
        var pattern = modId is { } id
            ? $"{id.ToString(CultureInfo.InvariantCulture)}-*.json"
            : "*.json";
        var removed = 0;

        try
        {
            if (!System.IO.Directory.Exists(Directory))
            {
                return Task.FromResult(0);
            }

            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, pattern))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.Debug(ex, "Could not remove the cached GameBanana response {Path}", file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Debug(ex, "Could not list the GameBanana cache at {Path}", Directory);
        }

        if (removed > 0)
        {
            _logger.Information(
                "Forgot {Count} cached GameBanana response(s) for {Scope}",
                removed,
                modId?.ToString(CultureInfo.InvariantCulture) ?? "every mod");
        }

        return Task.FromResult(removed);
    }

    private string PathFor(string key)
    {
        // Keys are built here, but the characters are fixed so no key can escape the cache folder.
        var safe = new string([.. key.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_')]);

        return Path.Combine(Directory, safe + ".json");
    }
}
