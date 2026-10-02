using Serilog;
using Xxsm.Core;
using Xxsm.Core.Io;
using Xxsm.Packs.Characters;
using Xxsm.Packs.Loading;
using Xxsm.Packs.Merge;
using Xxsm.Packs.Model;
using Xxsm.Packs.Studio;

namespace Xxsm.Packs.Overlays;

/// <summary>A correction the installed pack now makes itself.</summary>
/// <param name="InternalName">The character.</param>
/// <param name="DisplayName">Its name, as it shows now.</param>
/// <param name="Fields">The fields the correction sets, by their pack names; <c>hashes</c> when it edits
/// hashes.</param>
public sealed record RedundantCorrection(string InternalName, string DisplayName, IReadOnlyList<string> Fields);

/// <summary>Which of a user's corrections an installed pack has caught up with.</summary>
/// <param name="GameId">The game.</param>
/// <param name="PackVersion">The installed pack that was compared.</param>
/// <param name="Redundant">The corrections that pack already makes, by internal name.</param>
/// <param name="CorrectedCount">How many characters the user has corrections for, in all.</param>
public sealed record OverlayRedundancyReport(
    string GameId,
    string PackVersion,
    IReadOnlyList<RedundantCorrection> Redundant,
    int CorrectedCount);

/// <summary>What clearing corrections did.</summary>
/// <param name="Cleared">The characters whose corrections were removed.</param>
/// <param name="Kept">Characters asked about whose corrections still change something, and so were kept.</param>
/// <param name="BackupPath">Where the previous corrections file was backed up, when anything was cleared.</param>
public sealed record OverlayClearResult(IReadOnlyList<string> Cleared, IReadOnlyList<string> Kept, string? BackupPath);

/// <summary>Finds a user's corrections an installed pack now makes itself, and clears them on request.</summary>
public interface IOverlayRedundancy
{
    /// <summary>Compares the user's corrections with an installed pack. Changes nothing.</summary>
    /// <param name="gameId">The game.</param>
    /// <param name="packDirectory">The installed pack to compare with.</param>
    /// <param name="cancellationToken">Cancels the comparison.</param>
    /// <exception cref="PackLoadException">The pack or the corrections file cannot be read.</exception>
    /// <exception cref="ModOperationException">The pack is for a different game.</exception>
    Task<OverlayRedundancyReport> FindAsync(string gameId, string packDirectory, CancellationToken cancellationToken = default);

    /// <summary>Removes the named characters' corrections the pack still makes itself, checked again first.</summary>
    /// <param name="gameId">The game.</param>
    /// <param name="packDirectory">The installed pack the corrections were compared with.</param>
    /// <param name="internalNames">The characters whose corrections to clear.</param>
    /// <param name="cancellationToken">Cancels before anything is written.</param>
    /// <returns>What was cleared, and what was kept because it still changes something.</returns>
    /// <exception cref="PackLoadException">The pack or the corrections file cannot be read.</exception>
    /// <exception cref="ModOperationException">The pack is for a different game, or the file could not be
    /// written.</exception>
    Task<OverlayClearResult> ClearAsync(
        string gameId,
        string packDirectory,
        IReadOnlyList<string> internalNames,
        CancellationToken cancellationToken = default);
}

/// <summary>The default <see cref="IOverlayRedundancy"/>.</summary>
/// <remarks>A correction is redundant when the merge gives the same character without it.</remarks>
public sealed class OverlayRedundancy(
    IGamePackLoader loader,
    IOverlayStore overlays,
    IGameDataService gameData,
    ILogger logger) : IOverlayRedundancy
{
    private readonly IGamePackLoader _loader = loader;
    private readonly IOverlayStore _overlays = overlays;
    private readonly IGameDataService _gameData = gameData;
    private readonly ILogger _logger = logger.ForContext<OverlayRedundancy>();

    /// <inheritdoc />
    public async Task<OverlayRedundancyReport> FindAsync(
        string gameId,
        string packDirectory,
        CancellationToken cancellationToken = default)
    {
        var (pack, overlay) = await LoadAsync(gameId, packDirectory, cancellationToken).ConfigureAwait(false);
        var redundant = Find(gameId, pack, overlay, cancellationToken);

        _logger.Information(
            "Compared the corrections for {GameId} with pack {PackVersion}: {Redundant} of {Corrected} are now in the pack",
            gameId,
            pack.PackVersion,
            redundant.Count,
            OverlayPromotion.OverlayKeys(overlay).Count);

        return new OverlayRedundancyReport(gameId, pack.PackVersion, redundant, OverlayPromotion.OverlayKeys(overlay).Count);
    }

    /// <inheritdoc />
    public async Task<OverlayClearResult> ClearAsync(
        string gameId,
        string packDirectory,
        IReadOnlyList<string> internalNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(internalNames);

        var pack = await LoadPackAsync(gameId, packDirectory, cancellationToken).ConfigureAwait(false);

        var (cleared, kept) = await _overlays.UpdateAsync(
            gameId,
            overlay =>
            {
                var redundant = Find(gameId, pack, overlay, cancellationToken)
                    .Select(r => r.InternalName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var clearedNames = new List<string>();
                var keptNames = new List<string>();

                foreach (var name in internalNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (redundant.Contains(name))
                    {
                        overlay = Without(overlay, name);
                        clearedNames.Add(name);
                    }
                    else
                    {
                        keptNames.Add(name);
                    }
                }

                return clearedNames.Count == 0
                    ? OverlayUpdate.Keep((clearedNames, keptNames))
                    : OverlayUpdate.Write(overlay with { GameId = gameId }, (clearedNames, keptNames));
            },
            cancellationToken).ConfigureAwait(false);

        if (cleared.Count == 0)
        {
            return new OverlayClearResult([], kept, null);
        }

        foreach (var name in cleared)
        {
            _logger.Information(
                "Cleared the correction for {InternalName} in {GameId}: pack {PackVersion} makes it itself",
                name,
                gameId,
                pack.PackVersion);
        }

        return new OverlayClearResult(cleared, kept, _overlays.GetBackupPath(gameId));
    }

    private async Task<(GamePack Pack, PackOverlay Overlay)> LoadAsync(
        string gameId,
        string packDirectory,
        CancellationToken cancellationToken)
    {
        var pack = await LoadPackAsync(gameId, packDirectory, cancellationToken).ConfigureAwait(false);
        var overlay = await _overlays.ReadAsync(gameId, cancellationToken).ConfigureAwait(false);
        return (pack, overlay);
    }

    private async Task<GamePack> LoadPackAsync(
        string gameId,
        string packDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packDirectory);

        var pack = await _loader.LoadAsync(packDirectory, cancellationToken).ConfigureAwait(false);

        if (!PathComparer.AreNamesEqual(pack.GameId, gameId))
        {
            throw new ModOperationException(
                $"The pack at '{PathDisplay.Show(pack.Directory)}' is for '{pack.GameId}', not '{gameId}'.", pack.Directory);
        }

        return pack;
    }

    private List<RedundantCorrection> Find(
        string gameId,
        GamePack pack,
        PackOverlay overlay,
        CancellationToken cancellationToken)
    {
        var withAll = _gameData.Merge(gameId, pack, overlay);
        var redundant = new List<RedundantCorrection>();

        foreach (var key in OverlayPromotion.OverlayKeys(overlay))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!pack.Variants.Any(v => PathComparer.AreNamesEqual(v.InternalName, key)))
            {
                continue;
            }

            var withIt = withAll.Find(key);
            var withoutIt = _gameData.Merge(gameId, pack, Without(overlay, key)).Find(key);

            if (withIt is null || withoutIt is null || MergedVariantDifferences.Between(withIt, withoutIt).Count > 0)
            {
                continue;
            }

            redundant.Add(new RedundantCorrection(withIt.InternalName, withIt.DisplayName, Touched(overlay, key)));
        }

        return redundant;
    }

    /// <summary>The overlay with one character's entry and hash edits removed, as a reset removes them.</summary>
    private static PackOverlay Without(PackOverlay overlay, string internalName)
    {
        var variants = (overlay.Variants ?? new Dictionary<string, OverlayVariant>())
            .Where(pair => !PathComparer.AreNamesEqual(pair.Key, internalName))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        return CharacterEditor.ClearHashes(overlay with { Variants = variants }, internalName);
    }

    /// <summary>The fields a correction sets, for saying what clearing it would take away.</summary>
    private static List<string> Touched(PackOverlay overlay, string internalName)
    {
        var fields = new List<string>();
        var entry = (overlay.Variants ?? new Dictionary<string, OverlayVariant>())
            .FirstOrDefault(pair => PathComparer.AreNamesEqual(pair.Key, internalName))
            .Value;

        if (entry is not null)
        {
            foreach (var (field, value) in OverlayFields.ValuesOf(entry))
            {
                if (entry.IsSpecified(field, value))
                {
                    fields.Add(field);
                }
            }
        }

        var hashes = overlay.Hashes;
        if ((hashes?.Add ?? []).Any(h => PathComparer.AreNamesEqual(h.Variant, internalName))
            || (hashes?.Remove ?? []).Any(h => PathComparer.AreNamesEqual(h.Variant, internalName))
            || (hashes?.VariantModes?.Keys ?? []).Any(k => PathComparer.AreNamesEqual(k, internalName)))
        {
            fields.Add(OverlayFields.Hashes);
        }

        return fields;
    }
}
