using Serilog;
using Xxsm.Core;
using Xxsm.Core.Diagnostics;
using Xxsm.Core.Hashes;
using Xxsm.Core.Io;
using Xxsm.Packs.Merge;
using Xxsm.Packs.Model;
using Xxsm.Packs.Overlays;

namespace Xxsm.Packs.Studio;

/// <summary>What promoting one character's corrections would do to a draft.</summary>
public enum PromotionAction
{
    /// <summary>A character the user created, which the draft does not have.</summary>
    Add,

    /// <summary>A character the draft has, which the user's corrections change.</summary>
    Update,
}

/// <summary>One character whose corrections would go into the draft.</summary>
public sealed record OverlayPromotionRow
{
    /// <summary>The character. The row's key.</summary>
    public required string InternalName { get; init; }

    /// <summary>The name to show for it, with the user's correction applied.</summary>
    public required string DisplayName { get; init; }

    /// <summary>What promoting it would do.</summary>
    public required PromotionAction Action { get; init; }

    /// <summary>The fields it would change, by their pack names; <c>hashes</c> when its hashes change.</summary>
    public required IReadOnlyList<string> Fields { get; init; }

    /// <summary>The character as it would be in the draft. A portrait still to be copied is not in it yet.</summary>
    public required PackVariant Variant { get; init; }

    /// <summary>When its hashes change, every hash it would have; otherwise null.</summary>
    public IReadOnlyList<PackHashEntry>? Hashes { get; init; }

    /// <summary>The user's own portrait file to copy into the pack, or null.</summary>
    public string? ImageFile { get; init; }

    /// <summary>Whether the row starts ticked. Every row does; each is a correction the user made.</summary>
    public required bool SelectedByDefault { get; init; }

    /// <summary>Sentences about the row — a portrait that could not be found, say.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>What promoting a user's corrections into a draft would do. Reading them changed nothing.</summary>
public sealed record OverlayPromotionPlan
{
    /// <summary>The game.</summary>
    public required string GameId { get; init; }

    /// <summary>Where the user's corrections were read from.</summary>
    public required string OverlayPath { get; init; }

    /// <summary>One row per character the corrections would change, by internal name.</summary>
    public required IReadOnlyList<OverlayPromotionRow> Rows { get; init; }

    /// <summary>Hashes the user told XXSM to ignore that the draft does not ignore yet.</summary>
    public required IReadOnlyList<string> IgnoredHashes { get; init; }

    /// <summary>How many corrected characters the draft already matches, and so are not listed.</summary>
    public required int UnchangedCount { get; init; }

    /// <summary>Anything worth saying about the corrections as a whole.</summary>
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }
}

/// <summary>Whether to promote one character's corrections.</summary>
/// <param name="InternalName">The row, by <see cref="OverlayPromotionRow.InternalName"/>.</param>
/// <param name="Include">Whether to promote it.</param>
public sealed record OverlayPromotionChoice(string InternalName, bool Include);

/// <summary><em>Promote overlay to pack</em>: folds the user's Character Manager corrections into a draft.</summary>
public interface IOverlayPromotion
{
    /// <summary>Works out which of the user's corrections the draft lacks. Changes nothing.</summary>
    /// <param name="draft">The draft to promote into, usually one opened from the installed pack.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="PackLoadException">The corrections file exists but cannot be read.</exception>
    Task<OverlayPromotionPlan> PlanAsync(PackDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Applies the chosen rows, copying the user's portraits in; the corrections file is untouched.</summary>
    Task<StudioImportOutcome> ApplyAsync(
        PackDraft draft,
        OverlayPromotionPlan plan,
        IReadOnlyList<OverlayPromotionChoice>? choices,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

/// <summary>The default <see cref="IOverlayPromotion"/>: the draft merged with and without the corrections.</summary>
public sealed class OverlayPromotion(
    IStudioDraftStore store,
    IOverlayStore overlays,
    IGameDataService gameData,
    ILogger logger) : IOverlayPromotion
{
    private readonly IStudioDraftStore _store = store;
    private readonly IOverlayStore _overlays = overlays;
    private readonly IGameDataService _gameData = gameData;
    private readonly ILogger _logger = logger.ForContext<OverlayPromotion>();

    /// <inheritdoc />
    public async Task<OverlayPromotionPlan> PlanAsync(PackDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var overlay = await _overlays.ReadAsync(draft.GameId, cancellationToken).ConfigureAwait(false);
        var directory = _store.GetDraftDirectory(draft.GameId);
        var pack = MergedVariantDifferences.UsablePack(draft, directory);

        var plain = _gameData.Merge(draft.GameId, pack, new PackOverlay { GameId = draft.GameId });
        var corrected = _gameData.Merge(draft.GameId, pack, overlay);
        var drafted = CharacterImports.VariantsById(draft);

        var rows = new List<OverlayPromotionRow>();
        var unchanged = 0;

        foreach (var key in OverlayKeys(overlay))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (corrected.Find(key) is not { } after)
            {
                continue;
            }

            var before = plain.Find(key);
            var fields = before is null ? FieldsOf(after) : MergedVariantDifferences.Between(before, after);

            if (fields.Count == 0)
            {
                unchanged++;
                continue;
            }

            var start = drafted.GetValueOrDefault(after.InternalName)
                        ?? new PackVariant { InternalName = after.InternalName, DisplayName = after.DisplayName };
            var (variant, imageFile, notes) = Build(start, after, fields, directory);

            rows.Add(new OverlayPromotionRow
            {
                InternalName = after.InternalName,
                DisplayName = after.DisplayName,
                Action = before is null ? PromotionAction.Add : PromotionAction.Update,
                Fields = fields,
                Variant = variant,
                Hashes = fields.Contains(OverlayFields.Hashes)
                    ? [.. after.Hashes.Select(h => h with { Variant = after.InternalName })]
                    : null,
                ImageFile = imageFile,
                SelectedByDefault = true,
                Notes = notes,
            });
        }

        var alreadyIgnored = (draft.Hashes.IgnoredHashes ?? [])
            .Select(HashText.Normalize)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        var ignored = (overlay.IgnoredHashes ?? [])
            .Select(HashText.Normalize)
            .OfType<string>()
            .Where(h => !alreadyIgnored.Contains(h))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var diagnostics = new List<Diagnostic>();

        if (rows.Count == 0 && ignored.Count == 0)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Info,
                StudioImportCodes.NothingFound,
                overlay.Variants is { Count: > 0 } || overlay.Hashes is not null
                    ? "Your corrections for this game add nothing the draft does not already have."
                    : "You have no corrections for this game yet."));
        }

        _logger.Information(
            "Planned promoting the corrections for {GameId} into its Studio draft: {Rows} characters, {Ignored} ignored hashes, {Unchanged} already there",
            draft.GameId,
            rows.Count,
            ignored.Count,
            unchanged);

        return new OverlayPromotionPlan
        {
            GameId = draft.GameId,
            OverlayPath = _overlays.GetOverlayPath(draft.GameId),
            Rows = rows,
            IgnoredHashes = ignored,
            UnchangedCount = unchanged,
            Diagnostics = diagnostics,
        };
    }

    /// <inheritdoc />
    public async Task<StudioImportOutcome> ApplyAsync(
        PackDraft draft,
        OverlayPromotionPlan plan,
        IReadOnlyList<OverlayPromotionChoice>? choices,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(plan);

        var decided = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var choice in choices ?? [])
        {
            decided[choice.InternalName] = choice.Include;
        }

        var variants = draft.Variants.ToList();
        var entries = (draft.Hashes.Entries ?? []).ToList();
        var created = new List<string>();
        var updated = new List<string>();
        var skipped = new List<string>();

        foreach (var row in plan.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!(decided.TryGetValue(row.InternalName, out var include) ? include : row.SelectedByDefault))
            {
                continue;
            }

            var variant = row.Variant;

            if (row.ImageFile is not null)
            {
                try
                {
                    var image = await _store.StoreImageAsync(draft.GameId, row.InternalName, row.ImageFile, cancellationToken)
                        .ConfigureAwait(false);
                    variant = variant with { Image = image };
                }
                catch (ModOperationException ex)
                {
                    skipped.Add($"{row.DisplayName}: the portrait was not added. {ex.Message}");
                }
            }

            var index = variants.FindIndex(v => PathComparer.AreNamesEqual(v.InternalName, row.InternalName));

            if (index < 0)
            {
                variants.Add(variant);
                created.Add(row.InternalName);
            }
            else
            {
                variants[index] = variant;
                updated.Add(row.InternalName);
            }

            if (row.Hashes is not null)
            {
                entries.RemoveAll(e => PathComparer.AreNamesEqual(e.Variant, row.InternalName));
                entries.AddRange(row.Hashes);
            }
        }

        var ignoresAdded = plan.IgnoredHashes.Count > 0;

        if (created.Count + updated.Count == 0 && !ignoresAdded)
        {
            return new StudioImportOutcome(draft, [], [], skipped);
        }

        var record = new StudioImportRecord
        {
            Kind = StudioImportKinds.Overlay,
            Source = plan.OverlayPath,
            ImportedAt = now,
            Variants = [.. created, .. updated],
        };

        var next = draft with
        {
            Variants = variants,
            Hashes = draft.Hashes with
            {
                Entries = entries,
                IgnoredHashes = [.. (draft.Hashes.IgnoredHashes ?? []).Concat(plan.IgnoredHashes).Distinct(StringComparer.OrdinalIgnoreCase)],
            },
            Info = draft.Info with { Imports = [.. draft.Info.Imports ?? [], record] },
        };

        _logger.Information(
            "Promoted the corrections for {GameId} into its Studio draft: created {Created}, updated {Updated}, {Ignored} ignored hashes, skipped {Skipped}",
            draft.GameId,
            created.Count,
            updated.Count,
            plan.IgnoredHashes.Count,
            skipped.Count);

        return new StudioImportOutcome(next, created, updated, skipped) { OtherChanges = ignoresAdded };
    }

    /// <summary>Every character the overlay says anything about: an entry, or a hash edit.</summary>
    internal static IReadOnlyList<string> OverlayKeys(PackOverlay overlay)
    {
        var keys = (overlay.Variants?.Keys ?? [])
            .Concat((overlay.Hashes?.Add ?? []).Select(h => h.Variant))
            .Concat((overlay.Hashes?.Remove ?? []).Select(h => h.Variant))
            .Concat(overlay.Hashes?.VariantModes?.Keys ?? []);

        return [.. keys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The fields a character the draft does not have would bring with it.</summary>
    private static List<string> FieldsOf(MergedVariant variant)
    {
        var fields = new List<string> { OverlayFields.DisplayName, OverlayFields.IsDefaultVariant };

        void When(bool present, string field)
        {
            if (present)
            {
                fields.Add(field);
            }
        }

        When(variant.BaseCharacterId is not null, OverlayFields.BaseCharacterId);
        When(variant.Aliases.Count > 0, OverlayFields.Aliases);
        When(!string.Equals(variant.ModFilesName, variant.InternalName, StringComparison.Ordinal), OverlayFields.ModFilesName);
        When(variant.Image is not null, OverlayFields.Image);
        When(variant.ReleaseDate is not null, OverlayFields.ReleaseDate);
        When(variant.Attributes.Count > 0, OverlayFields.Attributes);
        When(variant.Hidden, OverlayFields.Hidden);
        When(variant.Notes is not null, OverlayFields.Notes);
        When(variant.Hashes.Count > 0, OverlayFields.Hashes);

        return fields;
    }

    /// <summary>Applies the changed fields of the corrected view onto the draft's own row.</summary>
    private static (PackVariant Variant, string? ImageFile, List<string> Notes) Build(
        PackVariant start,
        MergedVariant after,
        List<string> fields,
        string directory)
    {
        var variant = start;
        string? imageFile = null;
        var notes = new List<string>();

        foreach (var field in fields)
        {
            switch (field)
            {
                case OverlayFields.DisplayName:
                    variant = variant with { DisplayName = after.DisplayName };
                    break;
                case OverlayFields.BaseCharacterId:
                    variant = variant with { BaseCharacterId = after.BaseCharacterId };
                    break;
                case OverlayFields.IsDefaultVariant:
                    variant = variant with { IsDefaultVariant = after.IsDefaultVariant };
                    break;
                case OverlayFields.Aliases:
                    variant = variant with { Aliases = after.Aliases.Count == 0 ? null : [.. after.Aliases] };
                    break;
                case OverlayFields.ModFilesName:
                    variant = variant with { ModFilesName = after.ModFilesName };
                    break;
                case OverlayFields.ReleaseDate:
                    variant = variant with { ReleaseDate = after.ReleaseDate };
                    break;
                case OverlayFields.Attributes:
                    variant = variant with
                    {
                        Attributes = after.Attributes.Count == 0
                            ? null
                            : new Dictionary<string, AttributeValue>(after.Attributes, StringComparer.OrdinalIgnoreCase),
                    };
                    break;
                case OverlayFields.Hidden:
                    variant = variant with { Hidden = after.Hidden };
                    break;
                case OverlayFields.Notes:
                    variant = variant with { Notes = after.Notes };
                    break;
                case OverlayFields.Image:
                    var (image, file, note) = PackImage(after.Image, directory);

                    if (note is not null)
                    {
                        notes.Add(note);
                    }
                    else if (file is not null)
                    {
                        imageFile = file;
                    }
                    else
                    {
                        variant = variant with { Image = image };
                    }

                    break;
            }
        }

        return (variant, imageFile, notes);
    }

    /// <summary>How a corrected portrait goes into a pack: kept, made pack-relative, copied in, or explained.</summary>
    private static (string? Image, string? File, string? Note) PackImage(string? image, string directory)
    {
        if (string.IsNullOrWhiteSpace(image))
        {
            return (null, null, null);
        }

        if (Uri.TryCreate(image, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            return (image, null, null);
        }

        var local = image.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && uri is { IsFile: true }
            ? uri.LocalPath
            : Path.IsPathRooted(image) ? image : null;

        if (local is null)
        {
            return (image, null, null);
        }

        if (PathComparer.IsSameOrUnder(directory, PathComparer.Normalize(local))
            && PathComparer.TryGetRelativePath(directory, local) is { } relative)
        {
            return (relative.Replace(Path.DirectorySeparatorChar, '/'), null, null);
        }

        if (MergedVariantDifferences.LocalFile(local) is not { } found)
        {
            return (null, null, $"Its portrait file '{local}' is not there any more, so the portrait was left as it is.");
        }

        return StudioDraftStore.ImageExtensions.Contains(Path.GetExtension(found).ToLowerInvariant(), StringComparer.Ordinal)
            ? (null, found, null)
            : (null, null, $"Its portrait '{found}' is not a PNG, JPEG or WebP picture, so it cannot go into a pack.");
    }
}
