using System.Text.Json;
using Serilog;
using Xxsm.Core.Io;
using Xxsm.Packs.Installation;
using Xxsm.Packs.Model;
using Xxsm.Packs.Serialization;

namespace Xxsm.Packs.Portraits;

/// <summary>A game's icon, as the installed pack declares it.</summary>
/// <param name="Key">The pack's folder and the icon's path; a new pack version is a new key.</param>
/// <param name="PackDirectory">The installed pack the path is relative to.</param>
/// <param name="Image">The icon's path or address, as <c>game.json</c> gives it.</param>
public sealed record GameIcon(string Key, string PackDirectory, string Image);

/// <summary>Finds and opens the icon a game's installed pack declares.</summary>
public interface IGameIconSource
{
    /// <summary>The icon of the game's active installed pack.</summary>
    /// <returns>The icon, or null when there is no pack, no icon or no readable <c>game.json</c>.</returns>
    Task<GameIcon?> FindInstalledAsync(string gameId, CancellationToken cancellationToken = default);

    /// <summary>Opens an icon for decoding.</summary>
    /// <returns>A readable stream, or null when the file is missing or unreadable.</returns>
    Task<Stream?> OpenAsync(GameIcon icon, CancellationToken cancellationToken = default);
}

/// <summary>The default <see cref="IGameIconSource"/>.</summary>
public sealed class GameIconSource(IPackInstaller installer, IPortraitSource images, ILogger logger) : IGameIconSource
{
    private readonly IPackInstaller _installer = installer;
    private readonly IPortraitSource _images = images;
    private readonly ILogger _logger = logger.ForContext<GameIconSource>();

    /// <inheritdoc />
    public async Task<GameIcon?> FindInstalledAsync(string gameId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        if (await _installer.FindActivePackDirectoryAsync(gameId, cancellationToken).ConfigureAwait(false)
            is not { } directory)
        {
            return null;
        }

        var file = PathComparer.Join(directory, PackSchema.GameFile);

        try
        {
            await using var stream = File.OpenRead(file);
            var game = await JsonSerializer
                .DeserializeAsync(stream, PackJsonContext.Default.GameDefinition, cancellationToken)
                .ConfigureAwait(false);

            return game?.Icon is { Length: > 0 } icon
                ? new GameIcon(string.Concat(directory, "\n", icon), directory, icon)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.Warning(ex, "Could not read the icon of {GameId} from {File}", gameId, file);
            return null;
        }
    }

    /// <inheritdoc />
    public Task<Stream?> OpenAsync(GameIcon icon, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(icon);
        return _images.OpenImageAsync(icon.PackDirectory, icon.Image, "the game icon", cancellationToken);
    }
}
