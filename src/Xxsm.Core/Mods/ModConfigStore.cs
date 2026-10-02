using System.Text.Json;
using Serilog;
using Xxsm.Core.Io;
using Xxsm.Core.Serialization;

namespace Xxsm.Core.Mods;

/// <summary>The default <see cref="IModConfigStore"/>: one JSON file per mod, inside the mod.</summary>
public sealed class ModConfigStore(TimeProvider time, ILogger logger) : IModConfigStore
{
    private const string BackupSuffix = ".bak";

    /// <summary>The largest mod.json read: a megabyte.</summary>
    private const long MaxBytes = 1024 * 1024;

    private readonly TimeProvider _time = time;
    private readonly ILogger _logger = logger.ForContext<ModConfigStore>();

    // One read-change-write at a time per mod.
    private readonly KeyedAsyncLock _turns = new(PathComparer.Instance);

    /// <inheritdoc />
    public string GetConfigPath(string modFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modFolder);

        return PathComparer.Normalize(
            Path.Combine(modFolder, ModConfigSchema.DirectoryName, ModConfigSchema.FileName));
    }

    /// <inheritdoc />
    public string GetBackupPath(string modFolder) => GetConfigPath(modFolder) + BackupSuffix;

    /// <inheritdoc />
    public async Task<ModConfig?> ReadAsync(string modFolder, CancellationToken cancellationToken = default)
    {
        var path = GetConfigPath(modFolder);

        if (!PathComparer.TryResolveExisting(path, out var resolved) || !File.Exists(resolved))
        {
            return null;
        }

        string json;

        try
        {
            // Refused over a megabyte: a mod's details are a few hundred bytes.
            if (new FileInfo(resolved).Length > MaxBytes)
            {
                throw new ModOperationException(
                    $"'{PathDisplay.Show(resolved)}' is larger than {MaxBytes / 1024} KB, far more than a mod's details are, so it was not read.",
                    resolved);
            }

            json = await File.ReadAllTextAsync(resolved, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException(
                $"Could not read this mod's details from '{PathDisplay.Show(resolved)}': {ex.Message}",
                resolved,
                ex);
        }

        try
        {
            var config = JsonSerializer.Deserialize(json, CoreJsonContext.Default.ModConfig);

            if (config is null)
            {
                throw new ModOperationException(
                    $"'{PathDisplay.Show(resolved)}' contains only 'null'. Nothing has been changed. " +
                    $"A backup of the previous version may be at '{GetBackupPath(modFolder)}'.",
                    resolved);
            }

            if (!ModConfigSchema.IsSupported(config.SchemaVersion))
            {
                throw new ModOperationException(
                    $"'{PathDisplay.Show(resolved)}' uses format version {config.SchemaVersion}, which this " +
                    "version of XXSM does not understand. Update XXSM rather than deleting " +
                    "the file — it holds details you typed and cannot be re-downloaded.",
                    resolved);
            }

            return config;
        }
        catch (JsonException ex)
        {
            throw new ModOperationException(
                $"'{PathDisplay.Show(resolved)}' is not valid JSON: {ex.Message}. Nothing has been changed. " +
                $"A backup of the previous version may be at '{GetBackupPath(modFolder)}'.",
                resolved,
                ex);
        }
    }

    /// <inheritdoc />
    public async Task WriteAsync(
        string modFolder, ModConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var folder = RequireFolder(modFolder);

        using (await _turns.AcquireAsync(folder, cancellationToken).ConfigureAwait(false))
        {
            await WriteCoreAsync(folder, config, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<ModConfig> UpdateAsync(
        string modFolder,
        Func<ModConfig, ModConfig> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var folder = RequireFolder(modFolder);

        using (await _turns.AcquireAsync(folder, cancellationToken).ConfigureAwait(false))
        {
            var existing = await ReadAsync(folder, cancellationToken).ConfigureAwait(false)
                           ?? NewConfig();

            var updated = update(existing) ?? throw new ModOperationException(
                $"Refusing to save empty details for '{PathDisplay.Show(modFolder)}'.", modFolder);

            // An id is generated once and never changes: profiles record mods by it.
            if (string.IsNullOrWhiteSpace(updated.Id))
            {
                updated = updated with { Id = Guid.NewGuid().ToString("n") };
            }

            await WriteCoreAsync(folder, updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
    }

    private static string RequireFolder(string modFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modFolder);

        if (!PathComparer.TryResolveExisting(modFolder, out var folder) || !Directory.Exists(folder))
        {
            throw new ModOperationException(
                $"Cannot save this mod's details: the folder '{PathDisplay.Show(modFolder)}' does not exist.",
                modFolder);
        }

        return folder;
    }

    private async Task WriteCoreAsync(string folder, ModConfig config, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = GetConfigPath(folder);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var json = JsonSerializer.Serialize(config, CoreJsonContext.Default.ModConfig);

            // Not flushed to disk: a sort writes hundreds of these.
            await AtomicFile.WriteAllTextAsync(
                    path, json, new AtomicWriteOptions { BackupPath = path + BackupSuffix }, cancellationToken)
                .ConfigureAwait(false);

            _logger.Information("Wrote mod details to {Path}", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException(
                $"Could not save this mod's details to '{PathDisplay.Show(path)}': {ex.Message}. " +
                "The previous details have not been changed.",
                path,
                ex);
        }
    }

    private ModConfig NewConfig() => new()
    {
        Id = Guid.NewGuid().ToString("n"),
        DateAdded = _time.GetUtcNow(),
    };
}
