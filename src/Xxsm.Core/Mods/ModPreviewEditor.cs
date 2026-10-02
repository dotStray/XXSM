using System.Globalization;
using Serilog;
using Xxsm.Core.Io;

namespace Xxsm.Core.Mods;

/// <summary>The default <see cref="IModPreviewEditor"/>.</summary>
public sealed class ModPreviewEditor(
    IModConfigStore configs,
    ITrashService trash,
    TimeProvider time,
    ILogger logger) : IModPreviewEditor
{
    private const string StoredPrefix = "preview-";

    private readonly IModConfigStore _configs = configs;
    private readonly ITrashService _trash = trash;
    private readonly TimeProvider _time = time;
    private readonly ILogger _logger = logger.ForContext<ModPreviewEditor>();

    /// <inheritdoc />
    /// <remarks>Each stored picture gets a new name, so a cache keyed by path sees the change.</remarks>
    public async Task<ModPreview> SetAsync(
        string modFolder, PreviewImageSource image, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modFolder);
        ArgumentNullException.ThrowIfNull(image);

        if (!PathComparer.TryResolveExisting(modFolder, out var mod) || !Directory.Exists(mod))
        {
            throw new ModOperationException($"There is no mod folder at '{PathDisplay.Show(modFolder)}' to set a picture for.", modFolder);
        }

        var bytes = await image.ReadAsync(cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        var previous = await _configs.ReadAsync(mod, cancellationToken).ConfigureAwait(false);

        var directory = Path.Combine(mod, ModConfigSchema.DirectoryName);
        var stem = StoredPrefix + _time.GetUtcNow().ToString("yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture);
        string destination;

        try
        {
            destination = await AtomicFile.WriteNewAsync(directory, stem, image.Extension, bytes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException($"Could not save the picture into '{PathDisplay.Show(directory)}': {ex.Message}", directory, ex);
        }

        var relative = ModConfigSchema.DirectoryName + "/" + Path.GetFileName(destination);

        try
        {
            await _configs
                .UpdateAsync(
                    mod,
                    config => config with { ImagePath = relative, NoImage = false },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Written a moment ago by this method and declared nowhere: XXSM's own scratch, not user content.
            OwnScratch.TryDeleteFile(destination, _logger);
            throw;
        }

        _logger.Information(
            "Set preview for {Mod}: {Previous} -> {Image}", mod, previous?.ImagePath ?? "(none)", relative);

        await RetireAsync(mod, previous?.ImagePath, relative, cancellationToken).ConfigureAwait(false);

        return new ModPreview(
            PathComparer.Normalize(destination), relative, ModPreviewMatch.Declared, File.GetLastWriteTimeUtc(destination));
    }

    /// <inheritdoc />
    public async Task<bool> ClearAsync(string modFolder, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modFolder);

        if (!PathComparer.TryResolveExisting(modFolder, out var mod) || !Directory.Exists(mod))
        {
            throw new ModOperationException($"There is no mod folder at '{PathDisplay.Show(modFolder)}' to remove a picture from.", modFolder);
        }

        var previous = await _configs.ReadAsync(mod, cancellationToken).ConfigureAwait(false);

        if (previous is { NoImage: true, ImagePath: null or "" })
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        await _configs
            .UpdateAsync(mod, config => config with { ImagePath = null, NoImage = true }, cancellationToken)
            .ConfigureAwait(false);

        _logger.Information("Removed the preview for {Mod}: {Previous} -> (none)", mod, previous?.ImagePath ?? "(none)");

        await RetireAsync(mod, previous?.ImagePath, current: string.Empty, cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>Trashes the picture this replaced, when XXSM stored it. A failure is logged, not thrown.</summary>
    private async Task RetireAsync(string mod, string? previous, string current, CancellationToken cancellationToken)
    {
        var folder = ModConfigSchema.DirectoryName + "/";

        if (previous is not { Length: > 0 }
            || string.Equals(previous, current, StringComparison.Ordinal)
            || !previous.StartsWith(folder + StoredPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Directly in .xxsm/ and named as this class names them, and nothing else: no '..' can reach outside.
        var name = previous[folder.Length..];

        if (name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal) || name is "." or "..")
        {
            _logger.Warning("{Mod} declares a stored preview at {Previous}, which is not one XXSM stored; it is left alone", mod, previous);
            return;
        }

        var store = Path.Combine(mod, ModConfigSchema.DirectoryName);
        var path = Path.Combine(store, name);

        if (!File.Exists(path)
            || new FileInfo(path).LinkTarget is not null
            || new DirectoryInfo(store).LinkTarget is not null)
        {
            return;
        }

        try
        {
            await _trash
                .TrashAsync(path, Path.Combine(mod, ModConfigSchema.DirectoryName), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ModOperationException ex)
        {
            _logger.Warning(ex, "Set a new preview for {Mod} but could not trash the old one at {Path}", mod, path);
        }
    }
}
