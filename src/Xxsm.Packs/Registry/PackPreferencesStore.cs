using System.Text.Json;
using Serilog;
using Xxsm.Core;
using Xxsm.Core.Io;
using Xxsm.Packs.Serialization;

namespace Xxsm.Packs.Registry;

/// <summary>The default <see cref="IPackPreferencesStore"/>: one JSON file in the config directory.</summary>
public sealed class PackPreferencesStore(IAppPaths paths, ILogger logger) : IPackPreferencesStore
{
    /// <summary>The file name, beside <c>settings.json</c> rather than inside it.</summary>
    public const string FileName = "packs.json";

    private readonly IAppPaths _paths = paths;
    private readonly ILogger _logger = logger.ForContext<PackPreferencesStore>();

    // One read-change-write at a time within this program; FileLock takes turns with the other.
    private readonly KeyedAsyncLock _turns = new();

    /// <inheritdoc />
    public string PreferencesPath => Path.Combine(_paths.ConfigDirectory, FileName);

    /// <inheritdoc />
    public async Task<PackPreferences> ReadAsync(CancellationToken cancellationToken = default)
    {
        var path = PreferencesPath;

        if (!File.Exists(path))
        {
            return new PackPreferences();
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

            return JsonSerializer.Deserialize(json, PackJsonContext.Default.PackPreferences)
                   ?? new PackPreferences();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new PackLoadException(
                $"Your pack preferences at '{PathDisplay.Show(path)}' could not be read: {ex.Message}. " +
                "Fix or delete the file — XXSM will not guess at what your pins were.",
                path,
                ex);
        }
    }

    /// <inheritdoc />
    public async Task WriteAsync(PackPreferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        cancellationToken.ThrowIfCancellationRequested();

        using (await _turns.AcquireAsync(nameof(PackPreferences), cancellationToken).ConfigureAwait(false))
        using (await HoldFileAsync(cancellationToken).ConfigureAwait(false))
        {
            await WriteUnlockedAsync(preferences, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteUnlockedAsync(PackPreferences preferences, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = PreferencesPath;

        try
        {
            Directory.CreateDirectory(_paths.ConfigDirectory);

            var json = JsonSerializer.Serialize(preferences, PackJsonContext.Default.PackPreferences);

            await AtomicFile.WriteAllTextAsync(path, json, AtomicWriteOptions.DurableOnly, cancellationToken)
                .ConfigureAwait(false);

            _logger.Information("Wrote pack preferences to {Path}", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException(
                $"Could not save your pack preferences to '{PathDisplay.Show(path)}': {ex.Message}",
                path,
                ex);
        }
    }

    /// <inheritdoc />
    public async Task<PackPreferences> UpdateGameAsync(
        string gameId,
        Func<PackGamePreference, PackGamePreference> change,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(change);

        using (await _turns.AcquireAsync(nameof(PackPreferences), cancellationToken).ConfigureAwait(false))
        using (await HoldFileAsync(cancellationToken).ConfigureAwait(false))
        {
            return await UpdateUnlockedAsync(gameId, change, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<PackPreferences> UpdateUnlockedAsync(
        string gameId,
        Func<PackGamePreference, PackGamePreference> change,
        CancellationToken cancellationToken)
    {
        var current = await ReadAsync(cancellationToken).ConfigureAwait(false);

        // Copied entry by entry: a hand-edited file may hold keys differing only by case.
        var games = new Dictionary<string, PackGamePreference>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in current.Games)
        {
            games[key] = value;
        }

        games[current.FindKey(gameId) ?? gameId] = change(current.ForGame(gameId));

        var updated = current with { Games = games };

        await WriteUnlockedAsync(updated, cancellationToken).ConfigureAwait(false);

        return updated;
    }

    /// <summary>The turn with the command line or the application, whichever this is not.</summary>
    private async Task<IDisposable> HoldFileAsync(CancellationToken cancellationToken)
    {
        var lockPath = Path.Combine(_paths.StateDirectory, "locks", "packs.lock");

        try
        {
            return await FileLock.AcquireAsync(lockPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException(
                $"Could not save your pack preferences at '{PathDisplay.Show(PreferencesPath)}': {ex.Message}", lockPath, ex);
        }
    }
}
