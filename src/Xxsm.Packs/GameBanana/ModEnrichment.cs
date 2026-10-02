using Xxsm.Core.GameBanana;
using Xxsm.Core.Mods;

namespace Xxsm.Packs.GameBanana;

/// <summary>One thing a look-up offers to fill in. The address is always written, so it is not one.</summary>
public enum ModEnrichmentField
{
    /// <summary>The name shown in the interface — <c>customName</c>. Never the folder name.</summary>
    Name,

    /// <summary>Who made it — <c>author</c>.</summary>
    Author,

    /// <summary>The author's own version string — <c>version</c>.</summary>
    Version,

    /// <summary>The description, as plain text — <c>description</c>.</summary>
    Description,

    /// <summary>The preview picture, downloaded into the mod's own <c>.xxsm/</c>.</summary>
    Picture,
}

/// <summary>One row of the comparison: what the mod has now, and what its page says.</summary>
/// <param name="Field">Which field.</param>
/// <param name="Current">What the mod has now, or null when it has nothing.</param>
/// <param name="Proposed">What GameBanana says, or null when its page says nothing either.</param>
/// <param name="Selected">Whether this row starts ticked: only when the mod has nothing there.</param>
public sealed record ModEnrichmentEntry(
    ModEnrichmentField Field,
    string? Current,
    string? Proposed,
    bool Selected)
{
    /// <summary>Whether the mod has nothing in this field yet.</summary>
    public bool IsBlank => Current is not { Length: > 0 };

    /// <summary>Whether taking this row would actually change anything.</summary>
    public bool Changes =>
        Proposed is { Length: > 0 }
        && !string.Equals(Current ?? string.Empty, Proposed, StringComparison.Ordinal);
}

/// <summary>What a look-up found for one installed mod, before anything is written.</summary>
public sealed record ModEnrichmentPlan
{
    /// <summary>The mod folder this is about.</summary>
    public required string ModFolder { get; init; }

    /// <summary>What GameBanana said.</summary>
    public required GameBananaMod Mod { get; init; }

    /// <summary>Every field, whether or not it would change.</summary>
    public required IReadOnlyList<ModEnrichmentEntry> Entries { get; init; }

    /// <summary>Only the fields where the page says something different from the mod.</summary>
    public IReadOnlyList<ModEnrichmentEntry> Differences => [.. Entries.Where(entry => entry.Changes)];

    /// <summary>Whether there is anything to offer at all.</summary>
    public bool HasDifferences => Entries.Any(entry => entry.Changes);

    /// <summary>The fields that would be filled in without overwriting anything.</summary>
    public IReadOnlyList<ModEnrichmentField> BlankFields =>
        [.. Entries.Where(entry => entry.Changes && entry.IsBlank).Select(entry => entry.Field)];

    /// <summary>Every field the page has something for.</summary>
    public IReadOnlyList<ModEnrichmentField> AllFields =>
        [.. Entries.Where(entry => entry.Changes).Select(entry => entry.Field)];
}

/// <summary>What applying a look-up did.</summary>
/// <param name="Config">The mod's metadata as it now stands on disk.</param>
/// <param name="Applied">Which fields were written.</param>
/// <param name="PictureError">Why the picture could not be fetched, or null; the rest was still
/// written.</param>
public sealed record ModEnrichmentResult(
    ModConfig Config,
    IReadOnlyList<ModEnrichmentField> Applied,
    string? PictureError = null)
{
    /// <summary>Whether anything at all was written to the mod's metadata.</summary>
    public bool Changed => Applied.Count > 0;
}

/// <summary>Fills an installed mod in from its GameBanana page; a field with a value is only offered.</summary>
public interface IModEnrichment
{
    /// <summary>Works out what a mod's page would change about it. Writes nothing.</summary>
    /// <param name="modFolder">The mod's own folder.</param>
    /// <param name="page">What GameBanana said, from <see cref="IGameBananaClient.GetModAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the read of the mod's current metadata.</param>
    /// <exception cref="Xxsm.Core.ModOperationException">The mod folder is missing, or its
    /// <c>.xxsm/mod.json</c> could not be read.</exception>
    Task<ModEnrichmentPlan> PlanAsync(
        string modFolder, GameBananaMod page, CancellationToken cancellationToken = default);

    /// <summary>Writes the fields that were chosen, and the provenance behind them.</summary>
    /// <param name="plan">The plan, from <see cref="PlanAsync"/>.</param>
    /// <param name="take">Which entries to write. Empty still links the mod to its page.</param>
    /// <param name="cancellationToken">Cancels before anything is written.</param>
    /// <exception cref="Xxsm.Core.ModOperationException">The metadata could not be written.</exception>
    Task<ModEnrichmentResult> ApplyAsync(
        ModEnrichmentPlan plan,
        IReadOnlyCollection<ModEnrichmentField> take,
        CancellationToken cancellationToken = default);
}
