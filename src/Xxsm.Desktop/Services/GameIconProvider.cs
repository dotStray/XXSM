using Serilog;
using Xxsm.Desktop.ViewModels;
using Xxsm.Packs.Portraits;
using Xxsm.Packs.Studio;

namespace Xxsm.Desktop.Services;

/// <summary>The game icons shown beside a game's name, one view model per game, shared by every place.</summary>
public interface IGameIconProvider
{
    /// <summary>The icon of a game's installed pack, or its initial while there is none.</summary>
    /// <param name="gameId">The game.</param>
    /// <param name="displayName">Its name, for the initial.</param>
    GameIconViewModel Installed(string gameId, string displayName);

    /// <summary>The icon of a Pack Studio draft, which is the draft's own picture, not the installed pack's.</summary>
    /// <param name="gameId">The draft's game.</param>
    /// <param name="displayName">Its name, for the initial.</param>
    /// <param name="icon">The draft-relative path from its <c>game.json</c>, or null.</param>
    GameIconViewModel Draft(string gameId, string displayName, string? icon);
}

/// <summary>The default <see cref="IGameIconProvider"/>.</summary>
public sealed class GameIconProvider(
    IGameIconSource source,
    IStudioDraftStore drafts,
    ILogger logger) : IGameIconProvider, IDisposable
{
    /// <summary>Decode width: twice the largest size an icon is drawn at, a page heading's.</summary>
    internal const int DecodeWidthPixels = 64;

    private readonly IGameIconSource _source = source;
    private readonly IStudioDraftStore _drafts = drafts;
    private readonly LeasedBitmapCache _cache = new LeasedBitmapCache(logger.ForContext<GameIconProvider>(), DecodeWidthPixels, idleCapacity: 8);
    private readonly ILogger _logger = logger.ForContext<GameIconProvider>();
    private readonly Dictionary<string, GameIconViewModel> _installed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GameIconViewModel> _drafted = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public GameIconViewModel Installed(string gameId, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        var icon = For(_installed, gameId, displayName);
        icon.Track(GuardAsync(RefreshInstalledAsync(icon, gameId), gameId));
        return icon;
    }

    /// <inheritdoc />
    public GameIconViewModel Draft(string gameId, string displayName, string? icon)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        var model = For(_drafted, gameId, displayName);

        if (icon is not { Length: > 0 } path || _drafts.GetImageVersion(gameId, path) is not { } version)
        {
            model.Show(null, null);
            return model;
        }

        var key = string.Concat("draft\n", gameId, "\n", path, "\n", version);
        if (!string.Equals(model.Key, key, StringComparison.Ordinal))
        {
            model.Track(GuardAsync(LoadAsync(model, key, ct => _drafts.OpenImageAsync(gameId, path, ct)), gameId));
        }

        return model;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var icon in _installed.Values.Concat(_drafted.Values))
        {
            icon.Show(null, null);
        }

        _cache.Dispose();
    }

    private static GameIconViewModel For(Dictionary<string, GameIconViewModel> icons, string gameId, string displayName)
    {
        if (!icons.TryGetValue(gameId, out var icon))
        {
            icon = new GameIconViewModel();
            icons[gameId] = icon;
        }

        icon.Name = string.IsNullOrWhiteSpace(displayName) ? gameId : displayName;
        return icon;
    }

    /// <summary>Runs an icon read nobody awaits, logging anything unexpected and leaving the initial.</summary>
    private async Task GuardAsync(Task work, string gameId)
    {
        try
        {
            await work.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Could not show the icon of {GameId}", gameId);
        }
    }

    private async Task RefreshInstalledAsync(GameIconViewModel icon, string gameId)
    {
        var found = await _source.FindInstalledAsync(gameId).ConfigureAwait(true);

        if (found is null)
        {
            icon.Show(null, null);
            return;
        }

        if (!string.Equals(icon.Key, found.Key, StringComparison.Ordinal))
        {
            await LoadAsync(icon, found.Key, ct => _source.OpenAsync(found, ct)).ConfigureAwait(true);
        }
    }

    private async Task LoadAsync(GameIconViewModel icon, string key, Func<CancellationToken, Task<Stream?>> open)
    {
        // Claimed before the read, so a second ask while decoding does not start another.
        icon.Key = key;
        var lease = await _cache.AcquireAsync(key, open, key).ConfigureAwait(true);

        // Asked again for something else while decoding: the newer answer wins.
        if (!string.Equals(icon.Key, key, StringComparison.Ordinal))
        {
            lease?.Dispose();
            return;
        }

        icon.Show(key, lease);
    }
}
