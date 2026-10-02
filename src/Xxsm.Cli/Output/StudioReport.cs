namespace Xxsm.Cli.Output;

/// <summary>One draft, as <c>xxsm studio list</c> and <c>new</c> report it.</summary>
internal sealed record StudioDraftReport(
    string GameId,
    string DisplayName,
    string PackVersion,
    string Directory,
    int Variants,
    DateTimeOffset? LastSavedAt,
    string? Error);

/// <summary>What <c>xxsm studio delete</c> moved, and the record that puts it back.</summary>
internal sealed record StudioDeleteReport(
    string GameId,
    string Directory,
    string TrashedPath,
    string TrashMethod,
    string TrashRecord);

/// <summary>What <c>xxsm studio show</c> reports about a draft.</summary>
internal sealed record StudioShowReport(
    string GameId,
    string DisplayName,
    string PackVersion,
    string Directory,
    int Variants,
    int Outfits,
    int HashEntries,
    IReadOnlyList<string> Attributes,
    string? ModsDirectory,
    int Errors,
    int Warnings);

/// <summary>One problem the Problems panel would show.</summary>
internal sealed record StudioProblemReport(string Severity, string Code, string Kind, string Message, string? File, string? Subject);

/// <summary>What <c>xxsm studio check</c> found.</summary>
internal sealed record StudioCheckReport(
    string GameId,
    int Errors,
    int Warnings,
    bool CanExport,
    IReadOnlyList<StudioProblemReport> Problems);

/// <summary>One row of an import preview.</summary>
internal sealed record StudioImportRowReport(
    string Key,
    string Name,
    string? InternalName,
    string Action,
    bool Included,
    string? SkinOf,
    string? Confidence,
    string? Reason,
    int? Hashes,
    IReadOnlyList<string> Notes);

/// <summary>What an <c>xxsm studio import</c> previewed and, unless it was a dry run, did.</summary>
internal sealed record StudioImportReport(
    string GameId,
    string Source,
    bool Applied,
    IReadOnlyList<string> Created,
    IReadOnlyList<string> Updated,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<StudioImportRowReport> Rows,
    IReadOnlyList<string> Diagnostics);

/// <summary>What an edit to a draft changed.</summary>
internal sealed record StudioEditReport(string GameId, IReadOnlyList<string> Changed, IReadOnlyList<string> Notes);

/// <summary>A hash, and every character that carries it.</summary>
internal sealed record StudioSharedHashReport(string Hash, IReadOnlyList<string> Characters);

/// <summary>Families that share hashes, and the hashes they share.</summary>
internal sealed record StudioSharedGroupReport(IReadOnlyList<string> Families, IReadOnlyList<StudioSharedHashReport> Hashes);

/// <summary>What <c>xxsm studio hashes shared</c> found.</summary>
internal sealed record StudioSharedHashesReport(string GameId, IReadOnlyList<StudioSharedGroupReport> Groups);

/// <summary>What <c>xxsm studio hashes who</c> found.</summary>
internal sealed record StudioHashCarriersReport(string GameId, string Hash, IReadOnlyList<string> Characters, bool IsIgnored);

/// <summary>One entry on the pack's list of hashes to ignore.</summary>
internal sealed record StudioIgnoredHashReport(string Entry, bool IsAHash, IReadOnlyList<string> Characters);

/// <summary>What <c>xxsm studio hashes ignored</c> found.</summary>
internal sealed record StudioIgnoredHashesReport(string GameId, IReadOnlyList<StudioIgnoredHashReport> Ignored);

/// <summary>One mod in a <c>xxsm studio try</c> preview.</summary>
internal sealed record StudioTryRowReport(string Mod, string Destination, string Action, string? Character, string Reason);

/// <summary>What <c>xxsm studio try</c> showed.</summary>
internal sealed record StudioTryReport(
    string GameId,
    string ModsDirectory,
    int Mods,
    int Moves,
    IReadOnlyList<StudioTryRowReport> Rows);

/// <summary>What <c>xxsm studio install</c> did.</summary>
/// <param name="GameId">The game.</param>
/// <param name="PackVersion">The version it was installed as.</param>
/// <param name="Directory">Where the pack was unpacked, when the installer said.</param>
/// <param name="SizeBytes">How big the pack was.</param>
/// <param name="Warnings">How many warnings the draft still has.</param>
/// <param name="SkippedUpdates">How many edited characters kept the user's version.</param>
internal sealed record StudioInstallReport(
    string GameId,
    string PackVersion,
    string? Directory,
    long SizeBytes,
    int Warnings,
    int SkippedUpdates);

/// <summary>What <c>xxsm studio export</c> wrote.</summary>
internal sealed record StudioExportReport(
    string GameId,
    string PackVersion,
    string PackFile,
    string Sha256,
    long SizeBytes,
    int Variants,
    int HashEntries,
    int Pictures,
    int Warnings,
    string? IndexPath,
    string? ContributionDirectory);

/// <summary>What <c>xxsm studio caught-up</c> found and cleared.</summary>
internal sealed record StudioCaughtUpReport(
    string GameId,
    string PackVersion,
    IReadOnlyList<string> Redundant,
    IReadOnlyList<string> Cleared,
    IReadOnlyList<string> Kept);
