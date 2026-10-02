using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Serilog;
using Xxsm.Core;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Packs.Loading;
using Xxsm.Packs.Model;
using Xxsm.Packs.Serialization;

namespace Xxsm.Packs.Studio;

/// <summary>The default <see cref="IStudioDraftStore"/>.</summary>
public sealed class StudioDraftStore(
    IAppPaths paths,
    ITrashService trash,
    TimeProvider time,
    ILogger logger) : IStudioDraftStore
{
    /// <summary>Studio's own file inside a draft folder. Never exported.</summary>
    public const string InfoFile = "studio.json";

    /// <summary>The picture formats a draft accepts, by extension: PNG, JPEG and WebP.</summary>
    public static IReadOnlyList<string> ImageExtensions { get; } = [".png", ".jpg", ".jpeg", ".webp"];

    private const string BackupSuffix = ".bak";

    private readonly IAppPaths _paths = paths;
    private readonly ITrashService _trash = trash;
    private readonly TimeProvider _time = time;
    private readonly ILogger _logger = logger.ForContext<StudioDraftStore>();

    /// <inheritdoc />
    public string GetDraftDirectory(string gameId)
    {
        if (!PackDrafts.IsValidId(gameId))
        {
            throw new ModOperationException(
                $"'{gameId}' cannot name a Pack Studio draft — only letters, digits, underscores and " +
                "hyphens are allowed.");
        }

        var path = PathComparer.Normalize(Path.Combine(_paths.StudioDirectory, gameId));

        // Game ids compare ignoring case: never make a second folder beside "MyGame".
        return PathComparer.TryResolveExisting(path, out var existing) ? existing : path;
    }

    /// <inheritdoc />
    public bool Exists(string gameId) =>
        File.Exists(Path.Combine(GetDraftDirectory(gameId), PackSchema.ManifestFile));

    /// <inheritdoc />
    public async Task<IReadOnlyList<StudioDraftSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var summaries = new List<StudioDraftSummary>();

        if (!Directory.Exists(_paths.StudioDirectory))
        {
            return summaries;
        }

        foreach (var directory in Directory.EnumerateDirectories(_paths.StudioDirectory).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(Path.Combine(directory, PackSchema.ManifestFile)))
            {
                continue;
            }

            var folder = Path.GetFileName(directory);
            var normalized = PathComparer.Normalize(directory);

            try
            {
                var draft = await ReadDirectoryAsync(normalized, cancellationToken).ConfigureAwait(false);

                summaries.Add(new StudioDraftSummary(
                    draft.GameId,
                    string.IsNullOrWhiteSpace(draft.Game.DisplayName) ? draft.GameId : draft.Game.DisplayName,
                    draft.Manifest.PackVersion,
                    normalized,
                    draft.Variants.Count,
                    LastWrite(normalized),
                    Error: null,
                    Icon: draft.Game.Icon));
            }
            catch (PackLoadException ex)
            {
                _logger.Warning(ex, "The Studio draft in {Directory} could not be read", normalized);
                summaries.Add(new StudioDraftSummary(
                    folder, folder, string.Empty, normalized, 0, LastWrite(normalized), ex.Message));
            }
        }

        return summaries;
    }

    /// <inheritdoc />
    public async Task<PackDraft> ReadAsync(string gameId, CancellationToken cancellationToken = default)
    {
        var directory = GetDraftDirectory(gameId);

        if (!File.Exists(Path.Combine(directory, PackSchema.ManifestFile)))
        {
            throw new PackLoadException($"There is no Pack Studio draft for '{gameId}' at '{PathDisplay.Show(directory)}'.", directory);
        }

        return await ReadDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<StudioSaveResult> WriteAsync(PackDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        cancellationToken.ThrowIfCancellationRequested();

        var directory = GetDraftDirectory(draft.GameId);

        // Rendered in full before touching the disk, exactly as an export renders them.
        (string Name, string Json)[] files =
        [
            .. PackFiles.Render(draft),
            (InfoFile, PackFiles.Serialize(draft.Info, PackJsonContext.Default.StudioDraftInfo)),
        ];

        var written = new List<string>();

        try
        {
            Directory.CreateDirectory(directory);

            foreach (var (name, json) in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (await WriteIfChangedAsync(Path.Combine(directory, name), json, cancellationToken)
                        .ConfigureAwait(false))
                {
                    written.Add(name);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException(
                $"Could not save the Pack Studio draft for '{draft.GameId}' in '{PathDisplay.Show(directory)}': {ex.Message}. " +
                "The previous version of that file has not been changed.",
                directory,
                ex);
        }

        if (written.Count > 0)
        {
            _logger.Information(
                "Saved Studio draft {GameId} in {Directory}: {Files}",
                draft.GameId,
                directory,
                string.Join(", ", written));
        }

        return new StudioSaveResult(directory, written);
    }

    /// <inheritdoc />
    public async Task<StudioSaveResult> CreateAsync(PackDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ThrowIfExists(draft.GameId);

        return await WriteAsync(draft, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PackDraft> CreateFromPackAsync(GamePack pack, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ThrowIfExists(pack.GameId);

        var draft = PackDrafts.FromPack(pack, _time.GetUtcNow());
        var directory = GetDraftDirectory(pack.GameId);
        var sourceImages = Path.Combine(pack.Directory, PackSchema.ImagesDirectory);

        try
        {
            if (Directory.Exists(sourceImages))
            {
                var targetImages = Path.Combine(directory, PackSchema.ImagesDirectory);
                Directory.CreateDirectory(targetImages);

                foreach (var file in Directory.EnumerateFiles(sourceImages))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Copy(file, Path.Combine(targetImages, Path.GetFileName(file)), overwrite: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException(
                $"Could not copy the pictures of {pack.GameId} {pack.PackVersion} into a draft: {ex.Message}",
                directory,
                ex);
        }

        await WriteAsync(draft, cancellationToken).ConfigureAwait(false);

        _logger.Information(
            "Opened pack {GameId} {PackVersion} from {Source} as a Studio draft in {Directory}",
            pack.GameId,
            pack.PackVersion,
            pack.Directory,
            directory);

        return draft;
    }

    /// <inheritdoc />
    public async Task<string> StoreImageAsync(
        string gameId,
        string name,
        string sourceFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFile);

        if (!PackDrafts.IsValidId(name))
        {
            throw new ModOperationException(
                $"'{name}' cannot name a picture — only letters, digits, underscores and hyphens are allowed.",
                sourceFile);
        }

        var extension = Path.GetExtension(sourceFile).ToLowerInvariant();

        if (!ImageExtensions.Contains(extension, StringComparer.Ordinal))
        {
            throw new ModOperationException(
                $"'{PathDisplay.Show(sourceFile)}' is not a picture a pack can carry. Use a PNG, JPEG or WebP file.",
                sourceFile);
        }

        if (!PathComparer.TryResolveExisting(sourceFile, out var source) || !File.Exists(source))
        {
            throw new ModOperationException($"There is no file at '{PathDisplay.Show(sourceFile)}'.", sourceFile);
        }

        var images = Path.Combine(GetDraftDirectory(gameId), PackSchema.ImagesDirectory);
        var wanted = Path.Combine(images, name + extension);
        var target = PathComparer.TryResolveExisting(wanted, out var existing) ? existing : wanted;

        try
        {
            Directory.CreateDirectory(images);

            await using var input = File.OpenRead(source);
            await AtomicFile.WriteAsync(target, input.CopyToAsync, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException(
                $"Could not copy '{PathDisplay.Show(source)}' into the draft for '{gameId}': {ex.Message}",
                target,
                ex);
        }

        _logger.Information(
            "Copied picture {Source} into Studio draft {GameId} as {Target}", source, gameId, target);

        return PackSchema.ImagesDirectory + "/" + Path.GetFileName(target);
    }

    /// <inheritdoc />
    public async Task<string> StoreImageAsync(
        string gameId,
        string name,
        PreviewImageSource image,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (!PackDrafts.IsValidId(name))
        {
            throw new ModOperationException(
                $"'{name}' cannot name a picture — only letters, digits, underscores and hyphens are allowed.",
                image.FilePath);
        }

        var extension = image.Extension == ".jpeg" ? ".jpg" : image.Extension;

        if (!ImageExtensions.Contains(extension, StringComparer.Ordinal))
        {
            throw new ModOperationException(
                $"'{image.FilePath ?? image.Extension}' is not a picture a pack can carry. Use a PNG, JPEG or WebP file.",
                image.FilePath);
        }

        var bytes = await image.ReadAsync(cancellationToken).ConfigureAwait(false);
        var images = Path.Combine(GetDraftDirectory(gameId), PackSchema.ImagesDirectory);
        string target;

        try
        {
            target = await AtomicFile.WriteNewAsync(images, name, extension, bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException(
                $"Could not put the picture into the draft for '{gameId}': {ex.Message}",
                images,
                ex);
        }

        _logger.Information(
            "Stored picture {Source} in Studio draft {GameId} as {Target}",
            image.FilePath ?? "(pasted image)",
            gameId,
            target);

        return PackSchema.ImagesDirectory + "/" + Path.GetFileName(target);
    }

    /// <inheritdoc />
    public async Task<TrashResult> DeleteAsync(string gameId, CancellationToken cancellationToken = default)
    {
        var directory = GetDraftDirectory(gameId);

        if (!Directory.Exists(directory))
        {
            throw new ModOperationException($"There is no Pack Studio draft for '{gameId}' at '{PathDisplay.Show(directory)}'.", directory);
        }

        var trashed = await _trash.TrashAsync(directory, _paths.StudioDirectory, cancellationToken).ConfigureAwait(false);

        _logger.Information(
            "Deleted Studio draft {GameId}: moved {Directory} to {TrashedPath} ({Method})",
            gameId,
            directory,
            trashed.TrashedPath,
            trashed.Method);

        return trashed;
    }

    /// <inheritdoc />
    public async Task<string> RestoreAsync(TrashResult trashed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trashed);

        var original = PathComparer.Normalize(trashed.OriginalPath);
        var parent = Path.GetDirectoryName(original);
        var gameId = Path.GetFileName(original);

        if (parent is null
            || !PathComparer.AreEqual(parent, PathComparer.Normalize(_paths.StudioDirectory))
            || !PackDrafts.IsValidId(gameId))
        {
            throw new ModOperationException(
                $"'{PathDisplay.Show(trashed.OriginalPath)}' was not a Pack Studio draft, so it is not restored as one.",
                trashed.OriginalPath);
        }

        // The trash refuses only the exact path: look for the id under other capitals too.
        if (PathComparer.TryResolveExisting(original, out var existing))
        {
            throw new ModOperationException(
                $"The deleted draft for '{gameId}' was not put back: '{PathDisplay.Show(existing)}' is there now. " +
                "Delete or rename that one first. The deleted draft is still in the trash.",
                existing);
        }

        var restored = await _trash.RestoreAsync(trashed, cancellationToken).ConfigureAwait(false);

        _logger.Information(
            "Restored Studio draft {GameId}: moved {TrashedPath} back to {Directory}",
            gameId,
            trashed.TrashedPath,
            restored);

        return gameId;
    }

    /// <inheritdoc />
    public long? GetImageSize(string gameId, string relativePath) =>
        ResolveImage(gameId, relativePath) is { } found ? new FileInfo(found).Length : null;

    /// <inheritdoc />
    public string? GetImageVersion(string gameId, string relativePath)
    {
        if (ResolveImage(gameId, relativePath) is not { } found)
        {
            return null;
        }

        var file = new FileInfo(found);
        return string.Concat(
            file.Length.ToString(CultureInfo.InvariantCulture), ":",
            file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public Task<Stream?> OpenImageAsync(string gameId, string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (ResolveImage(gameId, relativePath) is not { } found)
        {
            return Task.FromResult<Stream?>(null);
        }

        try
        {
            return Task.FromResult<Stream?>(new FileStream(
                found, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(ex, "Could not open the picture {Path} in the {GameId} draft", found, gameId);
            return Task.FromResult<Stream?>(null);
        }
    }

    /// <summary>The full path of a picture inside a draft, or null when missing, absolute, web or outside.</summary>
    private string? ResolveImage(string gameId, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || relativePath.Contains(':', StringComparison.Ordinal)
            || Path.IsPathRooted(relativePath))
        {
            return null;
        }

        var directory = GetDraftDirectory(gameId);
        var full = PathComparer.Normalize(Path.GetFullPath(Path.Combine(directory, relativePath)));

        return UntrustedLocation.IsSameOrUnderExactly(directory, full)
               && PathComparer.TryResolveExisting(full, out var found)
               && File.Exists(found)
            ? found
            : null;
    }

    private void ThrowIfExists(string gameId)
    {
        if (Exists(gameId))
        {
            throw new ModOperationException(
                $"There is already a Pack Studio draft for '{gameId}'. Open that one, or delete it first.",
                GetDraftDirectory(gameId));
        }
    }

    private static async Task<PackDraft> ReadDirectoryAsync(string directory, CancellationToken cancellationToken)
    {
        var context = PackJsonContext.Default;

        var manifest = await ReadAsync(directory, PackSchema.ManifestFile, context.PackManifest, cancellationToken)
                           .ConfigureAwait(false)
                       ?? throw Missing(directory, PackSchema.ManifestFile);

        if (!PackSchema.IsSupported(manifest.PackSchemaVersion))
        {
            throw new PackLoadException(
                $"The draft in '{PathDisplay.Show(directory)}' uses pack format version " +
                $"{manifest.PackSchemaVersion.ToString(CultureInfo.InvariantCulture)}, which this version of " +
                "XXSM does not understand.",
                directory);
        }

        var game = await ReadAsync(directory, PackSchema.GameFile, context.GameDefinition, cancellationToken)
                       .ConfigureAwait(false)
                   ?? throw Missing(directory, PackSchema.GameFile);

        // Every draft has this file, so one without it is broken, not empty.
        var variants = await ReadAsync(
                           directory, PackSchema.VariantsFile, context.IReadOnlyListPackVariant, cancellationToken)
                       .ConfigureAwait(false)
                   ?? throw Missing(directory, PackSchema.VariantsFile);

        var hashes = await ReadAsync(directory, PackSchema.HashesFile, context.HashIndexFile, cancellationToken)
                         .ConfigureAwait(false);

        var info = await ReadAsync(directory, InfoFile, context.StudioDraftInfo, cancellationToken)
                       .ConfigureAwait(false);

        return new PackDraft
        {
            Manifest = manifest,
            Game = game,
            Variants = variants,
            Hashes = hashes is null
                ? new HashIndexFile { IgnoredHashes = [], Entries = [] }
                : hashes with { IgnoredHashes = hashes.IgnoredHashes ?? [], Entries = hashes.Entries ?? [] },
            Info = info ?? new StudioDraftInfo(),
        };
    }

    private static PackLoadException Missing(string directory, string file) =>
        new($"The draft in '{PathDisplay.Show(directory)}' is missing '{PathDisplay.Show(file)}'.", Path.Combine(directory, file));

    private static async Task<T?> ReadAsync<T>(
        string directory,
        string file,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
        where T : class
    {
        var path = Path.Combine(directory, file);

        if (!PathComparer.TryResolveExisting(path, out var resolved) || !File.Exists(resolved))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(resolved);
            return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new PackLoadException(
                $"'{PathDisplay.Show(resolved)}' is not valid JSON: {ex.Message}. A copy of the previous version may be at " +
                $"'{resolved}{BackupSuffix}'.",
                resolved,
                ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PackLoadException($"Could not read '{PathDisplay.Show(resolved)}': {ex.Message}", resolved, ex);
        }
    }

    /// <summary>Writes a file atomically with a backup, unless it already holds exactly this text.</summary>
    /// <returns><see langword="true"/> when the file was written.</returns>
    private static async Task<bool> WriteIfChangedAsync(string path, string contents, CancellationToken cancellationToken)
    {
        var target = PathComparer.TryResolveExisting(path, out var existing) ? existing : path;

        if (File.Exists(target))
        {
            var current = await File.ReadAllTextAsync(target, cancellationToken).ConfigureAwait(false);

            if (string.Equals(current, contents, StringComparison.Ordinal))
            {
                return false;
            }
        }

        await AtomicFile.WriteAllTextAsync(
                target, contents, new AtomicWriteOptions { BackupPath = target + BackupSuffix }, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private static DateTimeOffset? LastWrite(string directory)
    {
        try
        {
            var latest = Directory.EnumerateFiles(directory)
                .Select(File.GetLastWriteTimeUtc)
                .DefaultIfEmpty()
                .Max();

            return latest == default ? null : new DateTimeOffset(latest, TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
