using Xxsm.Core.Diagnostics;
using Xxsm.Packs.Characters;
using Xxsm.Packs.Model;

namespace Xxsm.Packs.Studio;

/// <summary>What importing one character would do.</summary>
public enum HashImportAction
{
    /// <summary>A character the draft does not have yet.</summary>
    Create,

    /// <summary>A character the draft has, with hashes it does not.</summary>
    AddHashes,

    /// <summary>A character the draft has, with nothing new.</summary>
    Unchanged,

    /// <summary>A folder whose name differs from another in the source only by capitals.</summary>
    Conflict,
}

/// <summary>One character an importer found, and what importing it would do, whatever the source.</summary>
public sealed record CharacterImportRow
{
    /// <summary>Where the character was found, relative to the source; the row's key.</summary>
    public required string SourcePath { get; init; }

    /// <summary>The folder's own name, which becomes the display name of a new character.</summary>
    public required string FolderName { get; init; }

    /// <summary>The internal name it would get: the folder name, or the nearest usable id.</summary>
    public required string InternalName { get; init; }

    /// <summary>For a folder found inside another character's folder, that folder's name.</summary>
    public string? ContainerFolder { get; init; }

    /// <summary>The folder a new character's mods are filed in, when not its internal name; null uses that.</summary>
    public string? ModFilesName { get; init; }

    /// <summary>For a character read from a Mods folder, how many mods its folder holds.</summary>
    public int? ModCount { get; init; }

    /// <summary>Every hash found for it, with its kind, not yet attributed to a variant.</summary>
    public required IReadOnlyList<PackHashEntry> Hashes { get; init; }

    /// <summary>What importing it would do.</summary>
    public required HashImportAction Action { get; init; }

    /// <summary>For a character the draft already has, how many of these hashes it lacks.</summary>
    public int NewHashCount { get; init; }

    /// <summary>For a character the draft has, how many of its hashes this source lacks; they are kept.</summary>
    public int MissingHashCount { get; init; }

    /// <summary>The proposed base/skin link, with its confidence and reason.</summary>
    public required SkinLinkProposal Link { get; init; }

    /// <summary>For a character the draft already has, the link it has now.</summary>
    public string? CurrentBaseCharacterId { get; init; }

    /// <summary>Whether the row starts ticked: new characters, and characters with new hashes.</summary>
    public required bool SelectedByDefault { get; init; }

    /// <summary>A sentence about the row worth showing beside it, or null.</summary>
    public string? Note { get; init; }

    /// <summary>What went wrong reading it, if anything. Never fatal.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];
}

/// <summary>What an import would do. Reading the source changed nothing.</summary>
public sealed record CharacterImportPlan
{
    /// <summary>The folder or archive that was read.</summary>
    public required string Source { get; init; }

    /// <summary>One row per character found, ordered by where it was found.</summary>
    public required IReadOnlyList<CharacterImportRow> Rows { get; init; }

    /// <summary>The shared shader hashes found, which importing adds to the pack's ignore list.</summary>
    public required IReadOnlyList<string> SharedShaderHashes { get; init; }

    /// <summary>Anything noticed about the source as a whole.</summary>
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }

    /// <summary>How many rows would create a character.</summary>
    public int CreateCount => Rows.Count(r => r.Action == HashImportAction.Create);

    /// <summary>How many rows would add hashes to a character the draft has.</summary>
    public int AddHashesCount => Rows.Count(r => r.Action == HashImportAction.AddHashes);

    /// <summary>How many rows would change nothing.</summary>
    public int UnchangedCount => Rows.Count(r => r.Action == HashImportAction.Unchanged);
}

/// <summary>What a person decided about one row of the preview.</summary>
public sealed record CharacterImportChoice
{
    /// <summary>The row, by <see cref="CharacterImportRow.SourcePath"/>.</summary>
    public required string SourcePath { get; init; }

    /// <summary>Whether to import it.</summary>
    public required bool Include { get; init; }

    /// <summary>A different internal name, or null to keep the proposed one.</summary>
    public string? InternalName { get; init; }

    /// <summary>The base to link it to, null to make it a base, or unchanged to take the proposal or keep it.</summary>
    public EditField<string> BaseCharacterId { get; init; }

    /// <summary>For a character the draft has, whether to drop its hashes and take only these.</summary>
    public bool ReplaceHashes { get; init; }
}

/// <summary>Applying a <see cref="CharacterImportPlan"/>, and the comparisons that build one.</summary>
public static class CharacterImports
{
    /// <summary>Applies the rows a person chose; a row that cannot be applied is skipped with a reason. Pure.</summary>
    /// <param name="draft">The draft to apply it to, normally the one the plan was made against.</param>
    /// <param name="plan">The preview.</param>
    /// <param name="choices">What was decided per row; a row with no choice takes its defaults.</param>
    /// <param name="kind">Which importer made the plan, for the draft's history.</param>
    /// <param name="now">When, for the draft's history.</param>
    public static StudioImportOutcome Apply(
        PackDraft draft,
        CharacterImportPlan plan,
        IReadOnlyList<CharacterImportChoice>? choices,
        string kind,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        var decided = new Dictionary<string, CharacterImportChoice>(StringComparer.Ordinal);
        foreach (var choice in choices ?? [])
        {
            decided[choice.SourcePath] = choice;
        }

        var variants = draft.Variants.ToList();
        var entries = (draft.Hashes.Entries ?? []).ToList();
        var created = new List<string>();
        var updated = new List<string>();
        var skipped = new List<string>();

        foreach (var row in plan.Rows)
        {
            var choice = decided.GetValueOrDefault(row.SourcePath);

            if (!(choice?.Include ?? row.SelectedByDefault))
            {
                continue;
            }

            var renamed = !string.IsNullOrWhiteSpace(choice?.InternalName);

            if (row.Action == HashImportAction.Conflict && !renamed)
            {
                skipped.Add($"{row.FolderName}: {row.Note}");
                continue;
            }

            var id = renamed ? choice!.InternalName!.Trim() : row.InternalName;

            if (!PackDrafts.IsValidId(id))
            {
                skipped.Add(
                    $"{row.FolderName}: '{id}' cannot be an internal name — only letters, digits, underscores and " +
                    "hyphens are allowed.");
                continue;
            }

            if (created.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                skipped.Add($"{row.FolderName}: another folder in this import already became '{id}'.");
                continue;
            }

            var index = variants.FindIndex(v => string.Equals(v.InternalName, id, StringComparison.OrdinalIgnoreCase));
            var setsBase = choice?.BaseCharacterId.IsSet == true;
            var chosenBase = setsBase ? Blank(choice!.BaseCharacterId.Value) : null;

            if (index < 0)
            {
                var baseId = setsBase ? chosenBase : row.Link.BaseCharacterId;

                variants.Add(new PackVariant
                {
                    InternalName = id,
                    DisplayName = row.FolderName,
                    BaseCharacterId = baseId,
                    IsDefaultVariant = baseId is null,
                    ModFilesName = row.ModFilesName ?? id,
                });

                entries.AddRange(row.Hashes.Select(h => h with { Variant = id }));
                created.Add(id);

                continue;
            }

            var existing = variants[index];
            var changed = false;

            if (setsBase && !string.Equals(existing.BaseCharacterId, chosenBase, StringComparison.OrdinalIgnoreCase))
            {
                variants[index] = existing with { BaseCharacterId = chosenBase, IsDefaultVariant = chosenBase is null };
                changed = true;
            }

            if (choice?.ReplaceHashes == true)
            {
                changed |= entries.RemoveAll(e =>
                    string.Equals(e.Variant, existing.InternalName, StringComparison.OrdinalIgnoreCase)) > 0;
            }

            var have = entries
                .Where(e => string.Equals(e.Variant, existing.InternalName, StringComparison.OrdinalIgnoreCase))
                .Select(HashKey.Of)
                .ToHashSet();

            foreach (var hash in row.Hashes)
            {
                if (have.Add(HashKey.Of(hash)))
                {
                    entries.Add(hash with { Variant = existing.InternalName });
                    changed = true;
                }
            }

            if (changed)
            {
                updated.Add(existing.InternalName);
            }
        }

        if (created.Count + updated.Count == 0)
        {
            return new StudioImportOutcome(draft, [], [], skipped);
        }

        var ignored = (draft.Hashes.IgnoredHashes ?? [])
            .Concat(plan.SharedShaderHashes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var record = new StudioImportRecord
        {
            Kind = kind,
            Source = plan.Source,
            ImportedAt = now,
            Variants = [.. created, .. updated],
        };

        var next = draft with
        {
            Variants = variants,
            Hashes = draft.Hashes with { Entries = entries, IgnoredHashes = ignored },
            Info = draft.Info with { Imports = [.. draft.Info.Imports ?? [], record] },
        };

        return new StudioImportOutcome(next, created, updated, skipped);
    }

    /// <summary>The draft's variants by internal name, the first of any duplicate winning.</summary>
    internal static Dictionary<string, PackVariant> VariantsById(PackDraft draft)
    {
        var existing = new Dictionary<string, PackVariant>(StringComparer.OrdinalIgnoreCase);

        foreach (var variant in draft.Variants.Where(v => !string.IsNullOrWhiteSpace(v.InternalName)))
        {
            existing.TryAdd(variant.InternalName, variant);
        }

        return existing;
    }

    /// <summary>The draft's hash entries by variant, as comparable keys.</summary>
    internal static Dictionary<string, HashSet<HashKey>> HashKeysById(PackDraft draft) =>
        (draft.Hashes.Entries ?? [])
            .GroupBy(e => e.Variant ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(HashKey.Of).ToHashSet(), StringComparer.OrdinalIgnoreCase);

    /// <summary>What importing a character the draft may already have would do.</summary>
    internal static (HashImportAction Action, int NewCount, int MissingCount, string? CurrentBase) Compare(
        string id,
        IReadOnlyList<PackHashEntry> hashes,
        Dictionary<string, PackVariant> existing,
        Dictionary<string, HashSet<HashKey>> existingHashes)
    {
        if (!existing.TryGetValue(id, out var variant))
        {
            return (HashImportAction.Create, 0, 0, null);
        }

        var have = existingHashes.GetValueOrDefault(variant.InternalName) ?? [];
        var incoming = hashes.Select(HashKey.Of).ToHashSet();

        var added = incoming.Count(k => !have.Contains(k));
        var missing = have.Count(k => !incoming.Contains(k));

        return (added > 0 ? HashImportAction.AddHashes : HashImportAction.Unchanged, added, missing, variant.BaseCharacterId);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>What makes two hash entries the same entry, whichever variant they are recorded against.</summary>
internal readonly record struct HashKey(string Component, HashKind Kind, string Hash, string? TextureKind, int? Slot)
{
    public static HashKey Of(PackHashEntry entry) => new(
        entry.Component ?? string.Empty,
        entry.Kind,
        entry.Hash.Trim().ToLowerInvariant(),
        entry.TextureKind,
        entry.Slot);
}
