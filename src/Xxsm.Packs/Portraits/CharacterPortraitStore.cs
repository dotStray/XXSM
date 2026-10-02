using System.Globalization;
using Serilog;
using Xxsm.Core;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;

namespace Xxsm.Packs.Portraits;

/// <summary>The default <see cref="ICharacterPortraitStore"/>, one folder per game under <c>portraits</c>.</summary>
public sealed class CharacterPortraitStore(IAppPaths paths, TimeProvider time, ILogger logger) : ICharacterPortraitStore
{
    private const string DirectoryName = "portraits";
    private const string StoredPrefix = "portrait-";

    private readonly IAppPaths _paths = paths;
    private readonly TimeProvider _time = time;
    private readonly ILogger _logger = logger.ForContext<CharacterPortraitStore>();

    /// <inheritdoc />
    public async Task<string> StoreAsync(
        string gameId, PreviewImageSource image, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(image);

        if (!gameId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') || gameId.Trim('.').Length == 0)
        {
            throw new ModOperationException($"'{gameId}' is not a game id a portrait can be stored under.");
        }

        var bytes = await image.ReadAsync(cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        var directory = Path.Combine(_paths.DataDirectory, DirectoryName, gameId);
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

        _logger.Information(
            "Stored a portrait for {GameId}: {Source} -> {Destination}",
            gameId,
            image.FilePath ?? "(clipboard)",
            destination);

        return new Uri(destination).AbsoluteUri;
    }
}
