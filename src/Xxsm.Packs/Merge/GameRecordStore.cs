using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Serilog;
using Xxsm.Core;
using Xxsm.Core.Io;

namespace Xxsm.Packs.Merge;

/// <summary>One JSON record per game in a folder of the state directory, read, replaced and forgotten whole.</summary>
/// <typeparam name="T">The record.</typeparam>
public abstract class GameRecordStore<T>
    where T : class
{
    private readonly IAppPaths _paths;
    private readonly string _directoryName;
    private readonly JsonTypeInfo<T> _type;
    private readonly string _description;

    /// <summary>Creates the store.</summary>
    /// <param name="paths">Where the state directory is.</param>
    /// <param name="directoryName">The folder under the state directory.</param>
    /// <param name="type">How a record is read and written.</param>
    /// <param name="description">What a record is, for a message: "a What's new list".</param>
    /// <param name="logger">Already given the subclass's context.</param>
    protected GameRecordStore(
        IAppPaths paths, string directoryName, JsonTypeInfo<T> type, string description, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryName);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(logger);

        _paths = paths;
        _directoryName = directoryName;
        _type = type;
        _description = description;
        Logger = logger;
    }

    /// <summary>The store's log.</summary>
    protected ILogger Logger { get; }

    /// <summary>Where a game's record lives, whether or not it exists.</summary>
    public string GetRecordPath(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        return Path.Combine(_paths.StateDirectory, _directoryName, SafeFileName(gameId) + ".json");
    }

    /// <summary>Reads a game's record.</summary>
    /// <returns>The record, or null when there is none.</returns>
    /// <exception cref="ModOperationException">The file could not be read, with the operating system's
    /// words.</exception>
    public async Task<T?> ReadAsync(string gameId, CancellationToken cancellationToken = default)
    {
        var path = GetRecordPath(gameId);

        if (!PathComparer.TryResolveExisting(path, out var resolved) || !File.Exists(resolved))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(resolved);

            var record = await JsonSerializer.DeserializeAsync(stream, _type, cancellationToken).ConfigureAwait(false);

            return record is null || IsNothing(record) ? null : record;
        }
        catch (JsonException ex)
        {
            throw new ModOperationException(
                $"'{PathDisplay.Show(resolved)}' is not {_description} XXSM can read: {ex.Message}", resolved, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException($"Could not read '{PathDisplay.Show(resolved)}': {ex.Message}", resolved, ex);
        }
    }

    /// <summary>Forgets a game's record.</summary>
    /// <returns><see langword="true"/> when there was a record to forget.</returns>
    public Task<bool> ClearAsync(string gameId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = GetRecordPath(gameId);

        if (!PathComparer.TryResolveExisting(path, out var resolved) || !File.Exists(resolved))
        {
            return Task.FromResult(false);
        }

        try
        {
            // File.Delete on purpose: XXSM's own bookkeeping, not user content.
            File.Delete(resolved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warning(ex, "Could not forget {Record} for {GameId} at {Path}", _description, gameId, resolved);
            return Task.FromResult(false);
        }

        Logger.Information("Forgot {Record} for {GameId}", _description, gameId);
        return Task.FromResult(true);
    }

    /// <summary>Whether a record holds nothing worth keeping, so it reads as none and saving it forgets it.</summary>
    protected virtual bool IsNothing(T record) => false;

    /// <summary>Replaces a game's record; a failure is logged, not thrown.</summary>
    /// <returns>Where it was written, or null when it was not.</returns>
    protected async Task<string?> WriteAsync(string gameId, T record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (IsNothing(record))
        {
            await ClearAsync(gameId, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var destination = GetRecordPath(gameId);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            await AtomicFile.WriteAsync(
                    destination,
                    (stream, token) => JsonSerializer.SerializeAsync(stream, record, _type, token),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return destination;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warning(ex, "Could not record {Record} for {GameId} at {Path}", _description, gameId, destination);
            return null;
        }
    }

    private static string SafeFileName(string value) =>
        string.Concat(value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}
