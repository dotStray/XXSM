using System.Text.Json;
using Serilog;
using Xxsm.Core.Io;
using Xxsm.Core.Serialization;

namespace Xxsm.Core.Settings;

/// <summary>The default <see cref="IAppSettingsStore"/>: one JSON file in the config directory.</summary>
/// <remarks>Updates take turns in this process and with the CLI; a save by anything else is caught and
/// redone.</remarks>
public sealed class AppSettingsStore(IAppPaths paths, ILogger logger) : IAppSettingsStore, IDisposable
{
    /// <summary>How many times a change is made again when another program keeps saving the file.</summary>
    private const int Attempts = 5;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IAppPaths _paths = paths;
    private readonly ILogger _logger = logger.ForContext<AppSettingsStore>();

    /// <inheritdoc />
    public string SettingsPath => _paths.SettingsFile;

    /// <inheritdoc />
    public async Task<AppSettings> ReadAsync(CancellationToken cancellationToken = default)
    {
        var path = SettingsPath;

        if (!File.Exists(path))
        {
            return AppSettings.Empty;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

            return JsonSerializer.Deserialize(json, CoreJsonContext.Default.AppSettings)
                   ?? AppSettings.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new SettingsLoadException(
                $"Your settings at '{PathDisplay.Show(path)}' could not be read: {ex.Message}. " +
                "Fix or delete the file — XXSM will not quietly start from defaults and " +
                "leave you wondering where your Mods folder went.",
                path,
                ex);
        }
    }

    /// <inheritdoc />
    public async Task WriteAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using (await HoldFileAsync(cancellationToken).ConfigureAwait(false))
            {
                await WriteUnlockedAsync(settings, expected: null, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<AppSettings> UpdateAsync(
        Func<AppSettings, AppSettings> change,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // One turn with the other program too, for the whole read-change-write.
            using var held = await HoldFileAsync(cancellationToken).ConfigureAwait(false);

            for (var attempt = 1; ; attempt++)
            {
                // Stamped before the read: a save that lands between the two is caught at the write.
                var stamp = FileStamp.Of(SettingsPath);
                var current = await ReadAsync(cancellationToken).ConfigureAwait(false);
                var updated = change(current);

                try
                {
                    await WriteUnlockedAsync(updated, stamp, cancellationToken).ConfigureAwait(false);
                    return updated;
                }
                catch (FileChangedException) when (attempt < Attempts)
                {
                    _logger.Information(
                        "Something else saved the settings while XXSM was changing them; making the change again on what it saved");
                }
                catch (FileChangedException ex)
                {
                    throw new ModOperationException(
                        $"Your settings at '{PathDisplay.Show(SettingsPath)}' were saved by another program {Attempts} times while XXSM " +
                        "was trying to save a change, so the change was not made. Try again once it has finished.",
                        SettingsPath,
                        ex);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Releases the gate that serialises writes.</summary>
    public void Dispose() => _gate.Dispose();

    /// <summary>The turn with the command line or the application, whichever this is not.</summary>
    private async Task<IDisposable> HoldFileAsync(CancellationToken cancellationToken)
    {
        var lockPath = Path.Combine(_paths.StateDirectory, "locks", "settings.lock");

        try
        {
            return await FileLock.AcquireAsync(lockPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException(
                $"Could not save your settings at '{PathDisplay.Show(SettingsPath)}': {ex.Message}", lockPath, ex);
        }
    }

    private async Task WriteUnlockedAsync(AppSettings settings, FileStamp? expected, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = SettingsPath;

        try
        {
            Directory.CreateDirectory(_paths.ConfigDirectory);

            var json = JsonSerializer.Serialize(settings, CoreJsonContext.Default.AppSettings);

            // Flushed before the rename: a power cut must not leave the file empty.
            await AtomicFile.WriteAllTextAsync(
                    path,
                    json,
                    new AtomicWriteOptions { Durable = true, Expected = expected },
                    cancellationToken)
                .ConfigureAwait(false);

            _logger.Information("Wrote settings to {Path}", path);
        }
        catch (FileChangedException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException(
                $"Could not save your settings to '{PathDisplay.Show(path)}': {ex.Message}",
                path,
                ex);
        }
    }

    /// <inheritdoc />
    public Task<AppSettings> UpdateGameAsync(
        string gameId,
        Func<GameSettings, GameSettings> change,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(change);

        return UpdateAsync(
            current =>
            {
                // Entry by entry: a hand-edited file may hold two keys differing by case. The last wins.
                var games = new Dictionary<string, GameSettings>(StringComparer.OrdinalIgnoreCase);

                foreach (var (key, value) in current.Games)
                {
                    games[key] = value;
                }

                games[current.FindKey(gameId) ?? gameId] = change(current.ForGame(gameId));

                return current with { Games = games };
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string> ResolveModsDirectoryAsync(
        string? gameId,
        string? explicitPath,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        if (string.IsNullOrWhiteSpace(gameId))
        {
            throw new SettingsLoadException(
                "No Mods folder was given. Pass --mods with the folder to use, or name a " +
                "game with --game once you have saved one with: " +
                "xxsm config set --game <game> --mods <folder>",
                SettingsPath);
        }

        var settings = await ReadAsync(cancellationToken).ConfigureAwait(false);
        var saved = settings.ForGame(gameId).ModsDirectory;

        if (!string.IsNullOrWhiteSpace(saved))
        {
            _logger.Debug(
                "Using the saved Mods folder {Path} for {GameId}", saved, gameId);

            return saved;
        }

        throw new SettingsLoadException(
            $"No Mods folder is set for '{gameId}'. Pass --mods with the folder to use, " +
            $"or save it once with: xxsm config set --game {gameId} --mods <folder>",
            SettingsPath);
    }
}
