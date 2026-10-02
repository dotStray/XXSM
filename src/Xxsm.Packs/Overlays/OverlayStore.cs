using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;
using Xxsm.Core;
using Xxsm.Core.Io;
using Xxsm.Packs.Model;
using Xxsm.Packs.Serialization;

namespace Xxsm.Packs.Overlays;

/// <summary>The default <see cref="IOverlayStore"/>: one JSON file per game under the data folder's overlays.</summary>
public sealed class OverlayStore(IAppPaths paths, ILogger logger) : IOverlayStore
{
    private const string OverlayExtension = ".json";
    private const string BackupExtension = ".json.bak";

    /// <summary>How many times a change is made again when another program keeps saving the file.</summary>
    private const int Attempts = 5;

    private readonly IAppPaths _paths = paths;
    private readonly ILogger _logger = logger.ForContext<OverlayStore>();

    // One turn at a time per game within this program; across programs, the saved file's stamp.
    private readonly KeyedAsyncLock _turns = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public string GetOverlayPath(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        return PathComparer.Normalize(Path.Combine(_paths.OverlaysDirectory, gameId + OverlayExtension));
    }

    /// <inheritdoc />
    public string GetBackupPath(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        return PathComparer.Normalize(Path.Combine(_paths.OverlaysDirectory, gameId + BackupExtension));
    }

    /// <inheritdoc />
    public async Task<PackOverlay> ReadAsync(string gameId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        var path = GetOverlayPath(gameId);

        if (!PathComparer.TryResolveExisting(path, out var resolved) || !File.Exists(resolved))
        {
            return new PackOverlay { GameId = gameId };
        }

        try
        {
            var json = await File.ReadAllTextAsync(resolved, cancellationToken).ConfigureAwait(false);
            var overlay = JsonSerializer.Deserialize(json, PackJsonContext.Default.PackOverlay);

            if (overlay is not null)
            {
                overlay = RecordSpecifiedFields(overlay, json);
            }

            if (overlay is null)
            {
                throw new PackLoadException(
                    $"Your overlay at '{PathDisplay.Show(resolved)}' contains only 'null'. " +
                    $"A backup of the previous version may be at '{GetBackupPath(gameId)}'.",
                    resolved);
            }

            if (!PackSchema.IsOverlaySupported(overlay.OverlaySchemaVersion))
            {
                throw new PackLoadException(
                    $"Your overlay at '{PathDisplay.Show(resolved)}' uses format version {overlay.OverlaySchemaVersion}, " +
                    "which this version of XXSM does not understand. Update XXSM rather than " +
                    "deleting the file — it holds your own corrections and cannot be re-downloaded.",
                    resolved);
            }

            return overlay;
        }
        catch (JsonException ex)
        {
            throw new PackLoadException(
                $"Your overlay at '{PathDisplay.Show(resolved)}' is not valid JSON: {ex.Message}. " +
                $"Nothing has been changed. A backup of the previous version may be at " +
                $"'{GetBackupPath(gameId)}'.",
                resolved,
                ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PackLoadException($"Could not read '{PathDisplay.Show(resolved)}': {ex.Message}", resolved, ex);
        }
    }

    /// <inheritdoc />
    public async Task WriteAsync(PackOverlay overlay, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentException.ThrowIfNullOrWhiteSpace(overlay.GameId);

        using (await _turns.AcquireAsync(overlay.GameId, cancellationToken).ConfigureAwait(false))
        using (await HoldFileAsync(overlay.GameId, cancellationToken).ConfigureAwait(false))
        {
            await SaveAsync(overlay, expected: null, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<T> UpdateAsync<T>(
        string gameId, Func<PackOverlay, OverlayUpdate<T>> change, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(change);

        using (await _turns.AcquireAsync(gameId, cancellationToken).ConfigureAwait(false))
        using (await HoldFileAsync(gameId, cancellationToken).ConfigureAwait(false))
        {
            var path = GetOverlayPath(gameId);

            for (var attempt = 1; ; attempt++)
            {
                // Stamped before the read: a save that lands between the two is caught at the write.
                var stamp = FileStamp.Of(path);
                var current = await ReadAsync(gameId, cancellationToken).ConfigureAwait(false);
                var decided = change(current);

                if (decided.Overlay is not { } updated)
                {
                    return decided.Result;
                }

                try
                {
                    await SaveAsync(updated with { GameId = gameId }, stamp, cancellationToken).ConfigureAwait(false);
                    return decided.Result;
                }
                catch (FileChangedException) when (attempt < Attempts)
                {
                    _logger.Information(
                        "Something else saved the overlay for {GameId} while XXSM was changing it; making the change again on what it saved",
                        gameId);
                }
                catch (FileChangedException ex)
                {
                    throw new ModOperationException(
                        $"Your overlay at '{PathDisplay.Show(path)}' was saved by another program {Attempts} times while XXSM was " +
                        "trying to save a change, so the change was not made. Your overlay has what that program " +
                        "saved. Try again once it has finished.",
                        path,
                        ex);
                }
            }
        }
    }

    /// <summary>The turn with the command line or the application, whichever this is not.</summary>
    private async Task<IDisposable> HoldFileAsync(string gameId, CancellationToken cancellationToken)
    {
        // In the state folder, not beside the overlay: people open the overlays folder.
        var lockPath = Path.Combine(_paths.StateDirectory, "locks", $"overlay-{gameId.ToLowerInvariant()}.lock");

        try
        {
            return await FileLock.AcquireAsync(lockPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException(
                $"Could not save your overlay at '{GetOverlayPath(gameId)}': {ex.Message}. " +
                "Your previous overlay has not been changed.",
                lockPath,
                ex);
        }
    }

    /// <summary>Saves atomically, the old version as backup; with a stamp, only if unchanged since.</summary>
    /// <exception cref="FileChangedException">The file no longer matches <paramref name="expected"/>.</exception>
    private async Task SaveAsync(PackOverlay overlay, FileStamp? expected, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = GetOverlayPath(overlay.GameId);
        var backup = GetBackupPath(overlay.GameId);

        try
        {
            Directory.CreateDirectory(_paths.OverlaysDirectory);

            // Serialised in full before touching the disk, so a bug cannot leave a truncated overlay.
            var json = Serialize(overlay);

            await AtomicFile.WriteAllTextAsync(
                    path,
                    json,
                    new AtomicWriteOptions { BackupPath = backup, Durable = true, Expected = expected },
                    cancellationToken)
                .ConfigureAwait(false);

            _logger.Information(
                "Wrote overlay for {GameId} to {Path} ({Variants} variants, backup at {Backup})",
                overlay.GameId,
                path,
                overlay.Variants?.Count ?? 0,
                backup);
        }
        catch (FileChangedException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException(
                $"Could not save your overlay to '{PathDisplay.Show(path)}': {ex.Message}. " +
                "Your previous overlay has not been changed.",
                path,
                ex);
        }
    }

    /// <summary>Serialises an overlay, writing back the nulls a variant entry set explicitly.</summary>
    /// <remarks>The context drops nulls, and an explicit null (clear the field) is not an absent key.</remarks>
    private static string Serialize(PackOverlay overlay)
    {
        var node = JsonSerializer.SerializeToNode(overlay, PackJsonContext.Default.PackOverlay);

        if (node is not JsonObject root)
        {
            return JsonSerializer.Serialize(overlay, PackJsonContext.Default.PackOverlay);
        }

        if (overlay.Variants is { Count: > 0 } variants
            && root["variants"] is JsonObject written)
        {
            foreach (var (key, entry) in variants)
            {
                if (entry.SpecifiedFields is not { Count: > 0 } specified
                    || written[key] is not JsonObject target)
                {
                    continue;
                }

                foreach (var field in specified)
                {
                    // Known fields only: an unknown key is already carried verbatim by AdditionalData.
                    if (OverlayFields.IsKnown(field) && !target.ContainsKey(field))
                    {
                        target[field] = null;
                    }
                }
            }
        }

        return root.ToJsonString(IndentedJson);
    }

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    /// <summary>Records which JSON keys each variant entry actually carried, from a second walk of the JSON.</summary>
    private static PackOverlay RecordSpecifiedFields(PackOverlay overlay, string json)
    {
        if (overlay.Variants is not { Count: > 0 } variants)
        {
            return overlay;
        }

        using var document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        if (!document.RootElement.TryGetProperty("variants", out var variantsElement) ||
            variantsElement.ValueKind != JsonValueKind.Object)
        {
            return overlay;
        }

        var annotated = new Dictionary<string, OverlayVariant>(variants.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in variants)
        {
            annotated[key] = variantsElement.TryGetProperty(key, out var entry) &&
                             entry.ValueKind == JsonValueKind.Object
                ? value with
                {
                    SpecifiedFields = entry.EnumerateObject()
                        .Select(p => p.Name)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase),
                }
                : value;
        }

        return overlay with { Variants = annotated };
    }
}
