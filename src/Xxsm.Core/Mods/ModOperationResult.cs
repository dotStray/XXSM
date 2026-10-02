using Xxsm.Core.Io;

namespace Xxsm.Core.Mods;

/// <summary>Which file operation was performed on a mod.</summary>
public enum ModOperationKind
{
    /// <summary>The <c>DISABLED_</c> prefix was removed, so 3DMigoto will load the mod.</summary>
    Enable,

    /// <summary>The <c>DISABLED_</c> prefix was added, so 3DMigoto will ignore the mod.</summary>
    Disable,

    /// <summary>The mod folder was moved under a different character.</summary>
    Move,

    /// <summary>The mod folder was given a different name, in the same place.</summary>
    Rename,

    /// <summary>A folder was copied into the Mods folder as a new mod.</summary>
    Install,

    /// <summary>The mod was moved to the trash.</summary>
    Delete,

    /// <summary>A mod that had been moved to the trash was put back where it was.</summary>
    Restore,
}

/// <summary>How a mod folder got from one place to another.</summary>
public enum ModMoveMethod
{
    /// <summary>Nothing was moved — the mod was already where it was asked to be.</summary>
    None,

    /// <summary>A same-filesystem rename. Instant, and atomic as far as a reader is concerned.</summary>
    Rename,

    /// <summary>Copied, verified, and only then the original moved to the trash: across filesystems.</summary>
    CopyVerifyTrash,

    /// <summary>Copied, leaving the source untouched. How an install works.</summary>
    Copy,
}

/// <summary>What one file operation did.</summary>
public sealed record ModOperationResult
{
    /// <summary>What was done.</summary>
    public required ModOperationKind Kind { get; init; }

    /// <summary>Where the mod was before.</summary>
    public required string FromPath { get; init; }

    /// <summary>Where the mod is now; for a deletion, where in the trash it landed.</summary>
    public required string ToPath { get; init; }

    /// <summary>Whether anything changed; false when the mod was already in the requested state.</summary>
    public required bool Changed { get; init; }

    /// <summary>How it moved.</summary>
    public required ModMoveMethod Method { get; init; }

    /// <summary>The name the mod would have had before a number was added to avoid a clash; null if none.</summary>
    public string? DisambiguatedFromName { get; init; }

    /// <summary>Where the deletion went, for a delete.</summary>
    public TrashResult? Trash { get; init; }

    /// <summary>The mod's folder name after the operation.</summary>
    public string ToName => Path.GetFileName(ToPath);
}
