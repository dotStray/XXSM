using Serilog;
using Xxsm.Core.Diagnostics;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Packs.Loading;
using Xxsm.Packs.Model;
using Xxsm.Packs.Overlays;

namespace Xxsm.Packs.Merge;

/// <summary>Merges a Game Pack with the user's overlay into the model everything else reads.</summary>
/// <remarks>Per field, the overlay wins; an explicit null clears. Locks are not enforced here.</remarks>
public sealed class GameDataService(IGamePackLoader loader, IOverlayStore overlays, ILogger logger) : IGameDataService
{
    private readonly IGamePackLoader _loader = loader;
    private readonly IOverlayStore _overlays = overlays;
    private readonly ILogger _logger = logger.ForContext<GameDataService>();

    /// <inheritdoc />
    public async Task<GameData> LoadAsync(
        string gameId,
        string? packDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        var pack = string.IsNullOrWhiteSpace(packDirectory)
            ? null
            : await _loader.LoadAsync(packDirectory, cancellationToken).ConfigureAwait(false);

        var overlay = await _overlays.ReadAsync(gameId, cancellationToken).ConfigureAwait(false);

        return Merge(gameId, pack, overlay);
    }

    /// <inheritdoc />
    public GameData Merge(string gameId, GamePack? pack, PackOverlay overlay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(overlay);

        var diagnostics = new List<PackDiagnostic>(pack?.Diagnostics ?? []);
        var overlayVariants = overlay.Variants ?? new Dictionary<string, OverlayVariant>();

        var hashesByVariant = GroupPackHashes(pack);
        var merged = new List<MergedVariant>();
        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var packVariant in pack?.Variants ?? [])
        {
            var edit = FindOverlayEntry(overlayVariants, packVariant.InternalName);
            handled.Add(packVariant.InternalName);

            merged.Add(Build(
                packVariant.InternalName,
                packVariant,
                edit,
                pack,
                overlay,
                hashesByVariant.GetValueOrDefault(packVariant.InternalName) ?? []));
        }

        foreach (var (key, edit) in overlayVariants)
        {
            if (handled.Contains(key))
            {
                continue;
            }

            if (edit.Origin != VariantOrigin.Custom)
            {
                // The pack lost this character but the overlay edits it: keep the edit.
                diagnostics.Add(new PackDiagnostic(
                    DiagnosticSeverity.Warning,
                    PackDiagnosticCodes.OverlayForUnknownVariant,
                    $"Your overlay has changes for '{key}', but the installed pack no longer has that " +
                    "character. Your changes are kept and it still appears in the grid.",
                    "overlay",
                    key));
            }

            if (string.IsNullOrWhiteSpace(edit.DisplayName) && edit.Origin == VariantOrigin.Custom)
            {
                diagnostics.Add(new PackDiagnostic(
                    DiagnosticSeverity.Warning,
                    PackDiagnosticCodes.OverlayCustomWithoutName,
                    $"The character you created as '{key}' has no display name, so its internal name " +
                    "is shown instead.",
                    "overlay",
                    key));
            }

            merged.Add(Build(
                key,
                packVariant: null,
                edit,
                pack,
                overlay,
                packHashes: []));
        }

        var resolved = ResolveFamilies(merged);

        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hash in (pack?.Hashes?.IgnoredHashes ?? []).Concat(overlay.IgnoredHashes ?? []))
        {
            if (!string.IsNullOrWhiteSpace(hash))
            {
                ignored.Add(hash.Trim().ToLowerInvariant());
            }
        }

        var game = pack?.Game ?? new GameDefinition { GameId = gameId, DisplayName = gameId };

        _logger.Debug(
            "Merged {GameId}: {Total} variants ({Custom} customised, {Pending} awaiting hashes)",
            gameId,
            resolved.Count,
            resolved.Count(v => v.IsCustomised),
            resolved.Count(v => v.IsHashesPending));

        return new GameData(
            gameId,
            game,
            pack,
            overlay,
            resolved,
            ignored,
            [.. diagnostics.OrderByDescending(d => d.Severity)]);
    }

    private static OverlayVariant? FindOverlayEntry(
        IReadOnlyDictionary<string, OverlayVariant> variants,
        string internalName)
    {
        if (variants.TryGetValue(internalName, out var exact))
        {
            return exact;
        }

        // A dictionary built in code may not compare ids ignoring case.
        foreach (var (key, value) in variants)
        {
            if (string.Equals(key, internalName, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private static Dictionary<string, List<PackHashEntry>> GroupPackHashes(GamePack? pack)
    {
        var grouped = new Dictionary<string, List<PackHashEntry>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in pack?.Hashes?.Entries ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.Hash) || string.IsNullOrWhiteSpace(entry.Variant))
            {
                continue;
            }

            if (!grouped.TryGetValue(entry.Variant, out var list))
            {
                list = [];
                grouped[entry.Variant] = list;
            }

            list.Add(entry with { Hash = entry.Hash.Trim().ToLowerInvariant() });
        }

        return grouped;
    }

    /// <summary>Builds one merged variant, deriving every field the overlay did not specify.</summary>
    private static MergedVariant Build(
        string internalName,
        PackVariant? packVariant,
        OverlayVariant? edit,
        GamePack? pack,
        PackOverlay overlay,
        IReadOnlyList<PackHashEntry> packHashes)
    {
        var origin = edit?.Origin ?? VariantOrigin.Pack;

        var displayName = Pick(edit, "displayName", edit?.DisplayName, packVariant?.DisplayName);
        var baseId = Pick(edit, "baseCharacterId", edit?.BaseCharacterId, packVariant?.BaseCharacterId);
        var modFilesName = Pick(edit, "modFilesName", edit?.ModFilesName, packVariant?.ModFilesName);
        var image = Pick(edit, "image", edit?.Image, packVariant?.Image);
        var releaseDate = Pick(edit, "releaseDate", edit?.ReleaseDate, packVariant?.ReleaseDate);
        var notes = Pick(edit, "notes", edit?.Notes, packVariant?.Notes);
        var aliases = Pick(edit, "aliases", edit?.Aliases, packVariant?.Aliases);
        var attributes = Pick(edit, "attributes", edit?.Attributes, packVariant?.Attributes);
        var hidden = Pick(edit, "hidden", edit?.Hidden, packVariant?.Hidden);
        var isDefault = Pick(edit, "isDefaultVariant", edit?.IsDefaultVariant, packVariant?.IsDefaultVariant);

        var hashes = MergeHashes(internalName, packHashes, overlay);

        return new MergedVariant
        {
            InternalName = internalName,

            DisplayName = string.IsNullOrWhiteSpace(displayName) ? internalName : displayName,
            BaseCharacterId = string.IsNullOrWhiteSpace(baseId) ? null : baseId,

            // Provisional: rewritten by ResolveFamilies, since a base may come from the overlay.
            FamilyId = internalName,
            IsDefaultVariant = isDefault ?? string.IsNullOrWhiteSpace(baseId),
            Aliases = aliases ?? [],
            ModFilesName = string.IsNullOrWhiteSpace(modFilesName)
                           || ModsFolderLayout.DescribeUnusableFolderName(modFilesName) is not null
                ? internalName
                : modFilesName,
            Image = ResolveImage(image, pack, fromOverlay: edit is not null && edit.IsSpecified("image", edit.Image)),
            ReleaseDate = releaseDate,
            Attributes = attributes ?? new Dictionary<string, AttributeValue>(),
            Hidden = hidden ?? false,
            Notes = notes,
            Origin = origin,

            IsLocked = edit?.Locked ?? origin != VariantOrigin.Pack,
            LockedFields = edit?.LockedFields ?? [],
            Hashes = hashes,
            HashesPendingFlag = packVariant?.HashesPending ?? false,
            CreatedAt = edit?.CreatedAt,
        };
    }

    private static T? Pick<T>(OverlayVariant? edit, string jsonName, T? overlayValue, T? packValue) =>
        edit is not null && edit.IsSpecified(jsonName, overlayValue) ? overlayValue : packValue;

    /// <summary>Applies the overlay's hash additions, removals and per-variant mode.</summary>
    private static List<PackHashEntry> MergeHashes(
        string internalName,
        IReadOnlyList<PackHashEntry> packHashes,
        PackOverlay overlay)
    {
        var hashes = overlay.Hashes;

        // The variant's own mode wins over the global one; replace leaves out the pack's entries.
        var mode = hashes?.ModeFor(internalName) ?? HashMergeMode.Merge;

        var result = mode == HashMergeMode.Replace
            ? new List<PackHashEntry>()
            : [.. packHashes];

        foreach (var entry in hashes?.Add ?? [])
        {
            if (!string.Equals(entry.Variant, internalName, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(entry.Hash))
            {
                continue;
            }

            result.Add(entry with { Hash = entry.Hash.Trim().ToLowerInvariant() });
        }

        foreach (var removal in hashes?.Remove ?? [])
        {
            if (!string.Equals(removal.Variant, internalName, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(removal.Hash))
            {
                continue;
            }

            var target = removal.Hash.Trim().ToLowerInvariant();

            result.RemoveAll(e =>
                string.Equals(e.Hash, target, StringComparison.OrdinalIgnoreCase) &&
                (removal.Kind == HashKind.Unknown || e.Kind == removal.Kind));
        }

        return Deduplicate(result);
    }

    private static List<PackHashEntry> Deduplicate(List<PackHashEntry> entries)
    {
        var seen = new HashSet<(string, HashKind, string, string?, int?)>();
        var result = new List<PackHashEntry>(entries.Count);

        foreach (var entry in entries)
        {
            if (seen.Add((entry.Hash, entry.Kind, entry.Component ?? string.Empty, entry.TextureKind, entry.Slot)))
            {
                result.Add(entry);
            }
        }

        return result;
    }

    /// <summary>Rewrites each variant's family once every variant, pack and custom, is known.</summary>
    private static List<MergedVariant> ResolveFamilies(List<MergedVariant> variants)
    {
        var known = variants
            .Select(v => v.InternalName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return
        [
            .. variants.Select(v =>
            {
                var parent = v.BaseCharacterId;

                var isRealParent = parent is { Length: > 0 }
                                   && known.Contains(parent)
                                   && !string.Equals(parent, v.InternalName, StringComparison.OrdinalIgnoreCase);

                // A dangling or self link makes the variant a base: the loader has reported it.
                return v with
                {
                    BaseCharacterId = isRealParent ? parent : null,
                    FamilyId = isRealParent ? parent! : v.InternalName,
                    IsDefaultVariant = isRealParent ? v.IsDefaultVariant : true,
                };
            }),
        ];
    }

    /// <summary>Where a portrait may be read from: https, inside the pack, or anywhere for the overlay's own.</summary>
    private static string? ResolveImage(string? image, GamePack? pack, bool fromOverlay)
    {
        if (string.IsNullOrWhiteSpace(image))
        {
            return null;
        }

        if (UntrustedLocation.IsWebAddress(image, out _))
        {
            return image;
        }

        if (fromOverlay
            && ((Uri.TryCreate(image, UriKind.Absolute, out var uri) && uri.IsFile) || Path.IsPathRooted(image)))
        {
            return image;
        }

        if (pack is null)
        {
            // A relative picture with no pack to be relative to: kept, and opened by nothing.
            return fromOverlay ? image : null;
        }

        return UntrustedLocation.TryResolveInside(pack.Directory, image, out var inside)
            ? PathComparer.Normalize(inside)
            : null;
    }
}
