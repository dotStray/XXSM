using System.Globalization;
using Xxsm.Core;
using Xxsm.Packs.Characters;
using Xxsm.Packs.Loading;
using Xxsm.Packs.Model;

namespace Xxsm.Packs.Studio;

/// <summary>What the new-game wizard collects. Only a name is required.</summary>
public sealed record NewGame
{
    /// <summary>The game's name. The only required field.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The game id to use, or null to derive one from <see cref="DisplayName"/>.</summary>
    public string? GameId { get; init; }

    /// <summary>A short form such as an abbreviation, for narrow places.</summary>
    public string? ShortName { get; init; }

    /// <summary>The XXMI importer folder name. Informational.</summary>
    public string? Importer { get; init; }

    /// <summary>The game's Mods folder, offered later as the folder for <em>Try it</em>.</summary>
    public string? ModsDirectory { get; init; }
}

/// <summary>Building and tidying drafts. Pure: no I/O, no clock — the caller supplies the time.</summary>
public static class PackDrafts
{
    /// <summary>What a Studio export names as its builder, before the version.</summary>
    public const string Builder = "XXSM Pack Studio";

    /// <summary>The <c>authoredBy</c> value of anything Studio writes.</summary>
    public const string AuthoredByUser = "user";

    /// <summary>The id used when a game's name has nothing an id can be made from.</summary>
    public const string FallbackGameId = "game";

    /// <summary>Whether a string can be a game id, internal name or attribute id.</summary>
    /// <returns><see langword="true"/> for <c>[A-Za-z0-9_-]+</c>.</returns>
    public static bool IsValidId(string? value) => value is not null && GamePackLoader.IsValidId(value);

    /// <summary>A pack version for a given day: date-based, so versions sort lexically.</summary>
    public static string VersionFor(DateTimeOffset now) =>
        now.UtcDateTime.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);

    /// <summary>Derives a lower-case game id from a name, avoiding ids already in use.</summary>
    public static string ProposeGameId(string? displayName, IEnumerable<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);

        var slug = CharacterNames.Slugify(displayName);

        if (string.Equals(slug, CharacterNames.Fallback, StringComparison.Ordinal)
            && displayName?.Contains(CharacterNames.Fallback, StringComparison.OrdinalIgnoreCase) != true)
        {
            slug = FallbackGameId;
        }

        return CharacterNames.ProposeAmong(slug.ToLowerInvariant(), taken).Name.ToLowerInvariant();
    }

    /// <summary>Starts a draft for a game the application has never heard of.</summary>
    /// <param name="request">What the wizard collected.</param>
    /// <param name="taken">Game ids already in use, for deriving a free one.</param>
    /// <param name="now">The time to record.</param>
    /// <returns>An empty pack: no characters, no hashes, no attributes, and valid as it stands.</returns>
    /// <exception cref="ModOperationException">The name is blank, or the id given is not a usable id.</exception>
    public static PackDraft Create(NewGame request, IEnumerable<string> taken, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(taken);

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            throw new ModOperationException("A new game needs a name.");
        }

        var takenList = taken.ToList();
        string gameId;

        if (string.IsNullOrWhiteSpace(request.GameId))
        {
            gameId = ProposeGameId(request.DisplayName, takenList);
        }
        else
        {
            gameId = request.GameId.Trim();

            if (!IsValidId(gameId))
            {
                throw new ModOperationException(
                    $"'{gameId}' cannot be a game id — only letters, digits, underscores and hyphens " +
                    "are allowed, because it becomes a folder name.");
            }

            if (takenList.Find(t => string.Equals(t, gameId, StringComparison.OrdinalIgnoreCase)) is { } clash)
            {
                throw new ModOperationException(
                    $"The game id '{clash}' is already used by an installed pack or another draft.");
            }
        }

        return new PackDraft
        {
            Manifest = new PackManifest
            {
                PackSchemaVersion = PackSchema.SupportedVersions.Max(),
                GameId = gameId,
                PackVersion = VersionFor(now),
                AuthoredBy = AuthoredByUser,
            },
            Game = new GameDefinition
            {
                GameId = gameId,
                DisplayName = request.DisplayName.Trim(),
                ShortName = Blank(request.ShortName),
                Importer = Blank(request.Importer),
                DisabledPrefix = PackSchema.DefaultDisabledPrefix,
            },
            Variants = [],
            Hashes = new HashIndexFile { IgnoredHashes = [], Entries = [] },
            Info = new StudioDraftInfo
            {
                CreatedAt = now,
                ModsDirectory = Blank(request.ModsDirectory),
            },
        };
    }

    /// <summary>Starts a draft from an installed pack, so it can be edited and exported again.</summary>
    public static PackDraft FromPack(GamePack pack, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pack);

        return new PackDraft
        {
            Manifest = pack.Manifest,
            Game = pack.Game,
            Variants = pack.Variants,
            Hashes = pack.Hashes ?? new HashIndexFile { IgnoredHashes = [], Entries = [] },
            Info = new StudioDraftInfo
            {
                CreatedAt = now,
                OpenedFrom = new StudioPackOrigin(pack.GameId, pack.PackVersion),
            },
        };
    }

    /// <summary>Presents a draft as a loaded pack, for the merge, the sorter and every other reader.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="directory">The draft's folder, against which its image paths resolve.</param>
    /// <returns>A pack. Its diagnostics are empty; validation is a separate question.</returns>
    public static GamePack ToGamePack(PackDraft draft, string directory)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        return new GamePack(directory, draft.Manifest, draft.Game, draft.Variants, draft.Hashes, []);
    }

    /// <summary>The order variants are written in, so two exports of an unchanged draft are identical.</summary>
    public static IReadOnlyList<PackVariant> Ordered(IEnumerable<PackVariant> variants)
    {
        ArgumentNullException.ThrowIfNull(variants);

        return
        [
            .. variants
                .OrderBy(v => v.InternalName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.InternalName, StringComparer.Ordinal),
        ];
    }

    /// <summary>The hash file in the order it is written in: by variant, deny-list sorted and unique.</summary>
    public static HashIndexFile Ordered(HashIndexFile hashes)
    {
        ArgumentNullException.ThrowIfNull(hashes);

        return hashes with
        {
            IgnoredHashes =
            [
                .. (hashes.IgnoredHashes ?? [])
                    .Where(h => !string.IsNullOrWhiteSpace(h))
                    .Select(h => h.Trim().ToLowerInvariant())
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal),
            ],
            Entries =
            [
                .. (hashes.Entries ?? [])
                    .OrderBy(e => e.Variant, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.Variant, StringComparer.Ordinal),
            ],
        };
    }

    /// <summary>Whether a string can be a pack version, which names a folder when installed.</summary>
    /// <returns><see langword="true"/> when it can be used.</returns>
    public static bool IsValidVersion(string? version) => GamePackLoader.IsValidVersion(version);

    /// <summary>Proposes the version to export a draft as: today's date, unless that is not newer.</summary>
    /// <returns>Today's date when newer than the source pack and the last export; else the newer of those with a
    /// two-digit counter, <c>2026.09.15.01</c> then <c>.02</c>.</returns>
    public static string ProposeVersion(PackDraft draft, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var today = VersionFor(now);
        var floor = new[] { draft.Info.OpenedFrom?.PackVersion, draft.Info.LastExportedVersion }
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .Order(StringComparer.Ordinal)
            .LastOrDefault();

        if (floor is null || string.CompareOrdinal(today, floor) > 0)
        {
            return today;
        }

        var parts = floor.Split('.');

        if (parts.Length >= 4
            && parts[^1].Length == 2
            && int.TryParse(parts[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var counter)
            && counter < 99)
        {
            parts[^1] = (counter + 1).ToString("00", CultureInfo.InvariantCulture);
            return string.Join('.', parts);
        }

        return floor + ".01";
    }

    /// <summary>Records that a draft was exported, so it carries the version it went out as.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="packVersion">The version it was exported as.</param>
    public static PackDraft AfterExport(PackDraft draft, string packVersion)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(packVersion);

        return draft with
        {
            Manifest = draft.Manifest with { PackVersion = packVersion },
            Info = draft.Info with { LastExportedVersion = packVersion },
        };
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
