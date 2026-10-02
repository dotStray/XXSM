using System.Globalization;
using Serilog;
using Xxsm.Core.GameBanana;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Core.Settings;
using Xxsm.Packs.Pictures;

namespace Xxsm.Packs.GameBanana;

/// <summary>Everything needed to install one GameBanana mod, with the archive on disk; disposing removes it.</summary>
public sealed class GameBananaFetch : IDisposable
{
    private bool _disposed;

    internal GameBananaFetch(
        GameBananaMod mod,
        GameBananaFile file,
        GameBananaDownloadedFile download,
        ModGameBananaInfo provenance,
        PreviewImageSource? picture,
        string? pictureError,
        ILogger logger)
    {
        Mod = mod;
        File = file;
        Download = download;
        Provenance = provenance;
        Picture = picture;
        PictureError = pictureError;
        Logger = logger;
    }

    /// <summary>What the mod's page said.</summary>
    public GameBananaMod Mod { get; }

    /// <summary>Which of its files was downloaded.</summary>
    public GameBananaFile File { get; }

    /// <summary>Where the archive landed, and whether its checksum matched.</summary>
    public GameBananaDownloadedFile Download { get; }

    /// <summary>The <c>gameBanana</c> block to write into the installed mod's metadata.</summary>
    public ModGameBananaInfo Provenance { get; }

    /// <summary>The preview picture, when one was fetched.</summary>
    public PreviewImageSource? Picture { get; }

    /// <summary>Why the preview picture was not fetched, or null.</summary>
    public string? PictureError { get; }

    /// <summary>The archive to hand to the installer.</summary>
    public string ArchivePath => Download.Path;

    private ILogger Logger { get; }

    /// <summary>Removes a fetch's own folder once it is empty; anything else is left alone.</summary>
    /// <param name="folder">The folder the archive was in.</param>
    /// <param name="logger">Where a failure is noted.</param>
    internal static void RemoveEmptyFolder(string? folder, ILogger logger)
    {
        if (folder is null
            || Path.GetFileName(folder) is not { Length: 32 } name
            || !name.All(char.IsAsciiHexDigitLower))
        {
            return;
        }

        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Debug(ex, "Could not remove the empty download folder {Folder}", folder);
        }
    }

    /// <summary>Removes the downloaded archive, and its folder once empty.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (System.IO.File.Exists(Download.Path))
            {
                System.IO.File.Delete(Download.Path);
            }

            RemoveEmptyFolder(Path.GetDirectoryName(Download.Path), Logger);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not ITrashService on purpose: XXSM's own download in its own cache, not user content.
            Logger.Debug(ex, "Could not remove the downloaded archive {Path}", Download.Path);
        }
    }
}

/// <summary>Turns a GameBanana mod page into an archive on disk the ordinary install flow can take.</summary>
public interface IGameBananaInstallSource
{
    /// <summary>Downloads a mod's file, and its preview picture.</summary>
    /// <param name="page">The mod, from <see cref="IGameBananaClient.GetModAsync"/>.</param>
    /// <param name="file">Which file to take, or null when the page has only one.</param>
    /// <param name="progress">Told how far the download has got.</param>
    /// <param name="cancellationToken">Cancels the download and removes the part file.</param>
    /// <returns>The archive and the metadata. Dispose it when the install is finished or abandoned.</returns>
    /// <exception cref="GameBananaDisabledException">GameBanana support is switched off.</exception>
    /// <exception cref="GameBananaDownloadBlockedException">The site wants a browser to fetch it.</exception>
    /// <exception cref="GameBananaException">The mod has no file, is unavailable, or the download
    /// failed.</exception>
    Task<GameBananaFetch> FetchAsync(
        GameBananaMod page,
        GameBananaFile? file = null,
        IProgress<GameBananaDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Fetches only a mod's preview picture, for a card that has no archive yet.</summary>
    /// <returns>The picture, or null when the mod has none or pictures are switched off.</returns>
    Task<PreviewImageSource?> PictureAsync(GameBananaMod page, CancellationToken cancellationToken = default);

    /// <summary>The <c>gameBanana</c> block recording where an install came from.</summary>
    /// <param name="page">The mod.</param>
    /// <param name="file">The file that was taken, or null when none was.</param>
    ModGameBananaInfo ProvenanceOf(GameBananaMod page, GameBananaFile? file);
}

/// <summary>The default <see cref="IGameBananaInstallSource"/>.</summary>
public sealed class GameBananaInstallSource(
    IGameBananaClient client,
    IPictureDownloader pictures,
    IAppSettingsStore settings,
    IAppPaths paths,
    ILogger logger,
    TimeProvider? time = null) : IGameBananaInstallSource
{
    private readonly IGameBananaClient _client = client;
    private readonly IPictureDownloader _pictures = pictures;
    private readonly IAppSettingsStore _settings = settings;
    private readonly IAppPaths _paths = paths;
    private readonly ILogger _logger = logger.ForContext<GameBananaInstallSource>();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<GameBananaFetch> FetchAsync(
        GameBananaMod page,
        GameBananaFile? file = null,
        IProgress<GameBananaDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        var chosen = page.FileToTake(file);

        // A folder of its own for each fetch, so one's clean-up never deletes another's archive.
        var into = Path.Combine(
            _paths.DownloadsDirectory,
            "gamebanana",
            page.ModId.ToString(CultureInfo.InvariantCulture),
            Guid.NewGuid().ToString("n"));

        var download = await _client
            .DownloadAsync(chosen, into, page.ModId, progress, cancellationToken)
            .ConfigureAwait(false);

        PreviewImageSource? picture;
        string? pictureError;

        try
        {
            (picture, pictureError) = await TryPictureAsync(page, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The archive has not been handed over yet, so nothing else would ever remove it.
            new GameBananaFetch(page, chosen, download, ProvenanceOf(page, chosen), null, null, _logger).Dispose();

            throw;
        }

        return new GameBananaFetch(
            page, chosen, download, ProvenanceOf(page, chosen), picture, pictureError, _logger);
    }

    /// <inheritdoc />
    public async Task<PreviewImageSource?> PictureAsync(
        GameBananaMod page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        var (picture, _) = await TryPictureAsync(page, cancellationToken).ConfigureAwait(false);

        return picture;
    }

    /// <inheritdoc />
    public ModGameBananaInfo ProvenanceOf(GameBananaMod page, GameBananaFile? file)
    {
        ArgumentNullException.ThrowIfNull(page);

        return new ModGameBananaInfo
        {
            ModId = page.ModId,
            FileId = file?.IdRow,
            FileName = file?.File,
            DateModifiedTs = page.DateModifiedTs,
            LastChecked = _time.GetUtcNow(),
            UpdateAvailable = false,
        };
    }

    /// <summary>The mod's picture, or why there is none. Throws only on cancellation.</summary>
    private async Task<(PreviewImageSource? Picture, string? Error)> TryPictureAsync(
        GameBananaMod page, CancellationToken cancellationToken)
    {
        try
        {
            var settings = await _settings.ReadAsync(cancellationToken).ConfigureAwait(false);

            if (!settings.GameBanana.DownloadPicturesOrDefault || page.PreviewImageUrl is not { } address)
            {
                return (null, null);
            }

            return (await _pictures.DownloadAsync(address, cancellationToken).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is Xxsm.Core.XxsmException or IOException or HttpRequestException
                                       or UnauthorizedAccessException or InvalidDataException or FormatException)
        {
            _logger.Warning(
                ex, "Could not fetch the preview picture for GameBanana mod {ModId}", page.ModId);

            return (null, ex.Message);
        }
    }
}
