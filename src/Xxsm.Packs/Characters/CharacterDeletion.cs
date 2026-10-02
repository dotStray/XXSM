using Xxsm.Core.Diagnostics;
using Xxsm.Packs.Model;

namespace Xxsm.Packs.Characters;

/// <summary>Everything deleting a custom character took away, kept on disk so the deletion can be undone.</summary>
public sealed record CharacterDeletion
{
    /// <summary>The game the character belonged to.</summary>
    public required string GameId { get; init; }

    /// <summary>The character's internal name, which it comes back under.</summary>
    public required string InternalName { get; init; }

    /// <summary>The character's name, for saying what came back.</summary>
    public required string DisplayName { get; init; }

    /// <summary>When it was deleted.</summary>
    public required DateTimeOffset DeletedAt { get; init; }

    /// <summary>The character's overlay entry, exactly as it was.</summary>
    public required OverlayVariant Entry { get; init; }

    /// <summary>The overlay's hash additions that were this character's.</summary>
    public IReadOnlyList<PackHashEntry> AddedHashes { get; init; } = [];

    /// <summary>The overlay's hash removals that were this character's.</summary>
    public IReadOnlyList<PackHashEntry> RemovedHashes { get; init; } = [];

    /// <summary>The character's own hash merge mode, if it had one.</summary>
    public HashMergeMode? HashMode { get; init; }

    /// <summary>Pack adoptions the user had declined for this character, if any.</summary>
    public IReadOnlyList<string>? DeclinedAdoption { get; init; }

    /// <summary>Each mod that was re-homed, and where it went.</summary>
    public IReadOnlyList<RehomedMod> Moves { get; init; } = [];

    /// <summary>Whether the character is already back from an earlier restore; only the mods left remain.</summary>
    public bool CharacterRestored { get; init; }
}

/// <summary>One mod moved away from a deleted character.</summary>
/// <param name="OriginalPath">Where it was, under the character's own folder.</param>
/// <param name="MovedTo">Where the deletion moved it.</param>
public sealed record RehomedMod(string OriginalPath, string MovedTo);

/// <summary>A mod a restore could not move back, and why, in a sentence.</summary>
/// <param name="Path">Where the mod was expected to be.</param>
/// <param name="Reason">Why it stayed where it is — the operating system's words, when it had some.</param>
public sealed record CharacterRestoreSkip(string Path, string Reason);

/// <summary>What undoing a character deletion did.</summary>
public sealed record CharacterRestoreResult
{
    /// <summary>The character's internal name.</summary>
    public required string InternalName { get; init; }

    /// <summary>The character's name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Where each mod that came back now is.</summary>
    public IReadOnlyList<string> RestoredMods { get; init; } = [];

    /// <summary>The mods that could not come back. Each one is still where the deletion put it.</summary>
    public IReadOnlyList<CharacterRestoreSkip> Skipped { get; init; } = [];

    /// <summary>Anything noticed on the way, such as a mod that came back under another name.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];

    /// <summary>What is left to undo: the character is back; the moves hold the mods that did not return.</summary>
    public required CharacterDeletion Remaining { get; init; }

    /// <summary>Whether everything came back.</summary>
    public bool IsComplete => Skipped.Count == 0;
}
