using Xxsm.Core.Diagnostics;
using Xxsm.Packs.Model;

namespace Xxsm.Packs.Characters;

/// <summary>What one Character Manager edit did; failures are thrown, not reported here.</summary>
public sealed record CharacterEditResult
{
    /// <summary>The variant's internal name, after any adjustment for a collision.</summary>
    public required string InternalName { get; init; }

    /// <summary>The name that will be shown in the grid.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Where the variant's data now comes from, after the edit.</summary>
    public required VariantOrigin Origin { get; init; }

    /// <summary>Whether the overlay was written. False when the edit asked for nothing new.</summary>
    public required bool Changed { get; init; }

    /// <summary>The internal name asked for when it was taken and adjusted; otherwise null.</summary>
    public string? RequestedInternalName { get; init; }

    /// <summary>Conflicts recorded rather than refused: a duplicate hash, a claimed hash, a missing base.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];

    /// <summary>How many hashes the variant has after the edit.</summary>
    public int HashCount { get; init; }
}

/// <summary>What deleting a custom character did, including where its mods went.</summary>
public sealed record CharacterDeleteResult
{
    /// <summary>The variant that was deleted.</summary>
    public required string InternalName { get; init; }

    /// <summary>Whether the overlay entry was removed.</summary>
    public required bool Changed { get; init; }

    /// <summary>The folder every mod was re-homed to, relative to the Mods folder; null when there were none.</summary>
    public string? RehomedTo { get; init; }

    /// <summary>The mod folder names that were moved, in the order they were moved.</summary>
    public IReadOnlyList<string> RehomedMods { get; init; } = [];

    /// <summary>Anything noticed on the way. Never fatal.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];

    /// <summary>What was taken away, for <see cref="ICharacterEditor.RestoreDeletedAsync"/>, or null.</summary>
    public CharacterDeletion? Deletion { get; init; }

    /// <summary>The file the deletion was recorded in; null for nothing removed or a failed write.</summary>
    public string? RestoreRecordPath { get; init; }
}

/// <summary>Stable codes for what a Character Manager edit can report.</summary>
public static class CharacterDiagnosticCodes
{
    /// <summary>The internal name asked for was taken, so a numeric suffix was added.</summary>
    public const string InternalNameAdjusted = "character.internal-name-adjusted";

    /// <summary>A hash was already recorded against this variant.</summary>
    public const string DuplicateHash = "character.duplicate-hash";

    /// <summary>A hash is already claimed by a different variant.</summary>
    public const string HashClaimedElsewhere = "character.hash-claimed-elsewhere";

    /// <summary>The base character named by the edit is not in the pack or the overlay.</summary>
    public const string UnknownBaseCharacter = "character.unknown-base";

    /// <summary>A hash was asked to be removed that the variant did not have.</summary>
    public const string HashNotPresent = "character.hash-not-present";

    /// <summary>A mod filed under a deleted character was re-homed.</summary>
    public const string ModRehomed = "character.mod-rehomed";

    /// <summary>A deletion happened but its record for undoing it later could not be written.</summary>
    public const string RestoreRecordNotWritten = "character.restore-record-not-written";

    /// <summary>A hash is <c>root_vs</c>, which is shared and carries no identifying weight.</summary>
    public const string SharedShaderHash = "character.shared-shader-hash";
}
