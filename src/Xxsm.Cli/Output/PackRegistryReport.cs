using Xxsm.Packs.Merge;
using Xxsm.Packs.Registry;

namespace Xxsm.Cli.Output;

/// <summary>One published version of a pack, as <c>xxsm pack list</c> reports it.</summary>
/// <param name="PackVersion">The version.</param>
/// <param name="PackSchemaVersion">The pack format it is written in.</param>
/// <param name="Availability">Whether this build can install it.</param>
/// <param name="RefusalReason">Why not, when it cannot.</param>
/// <param name="Installed">Whether this version is on disk.</param>
/// <param name="Active">Whether this is the version XXSM would load.</param>
/// <param name="SizeBytes">The archive size the registry published.</param>
/// <param name="Changelog">What changed in it.</param>
public sealed record PackCatalogVersionReport(
    string PackVersion,
    int PackSchemaVersion,
    string Availability,
    string? RefusalReason,
    bool Installed,
    bool Active,
    long SizeBytes,
    string? Changelog);

/// <summary>One game's packs, as <c>xxsm pack list</c> reports them.</summary>
/// <param name="GameId">The game.</param>
/// <param name="DisplayName">Its name.</param>
/// <param name="Registry">Which registry it came from, or null when only installed locally.</param>
/// <param name="ActiveVersion">The version XXSM would load.</param>
/// <param name="PinnedVersion">The version the user pinned, when they have.</param>
/// <param name="NewerThanHeld">The newer version the pin holds the game back from, or null when it is not held.</param>
/// <param name="UpdateAvailable">Whether a newer usable version is published.</param>
/// <param name="Versions">Every published version, newest first.</param>
public sealed record PackCatalogEntryReport(
    string GameId,
    string DisplayName,
    string? Registry,
    string? ActiveVersion,
    string? PinnedVersion,
    string? NewerThanHeld,
    bool UpdateAvailable,
    IReadOnlyList<PackCatalogVersionReport> Versions);

/// <summary>The whole catalogue.</summary>
/// <param name="Packs">One entry per game.</param>
/// <param name="Failures">Registries that could not be reached. Not an error on its own.</param>
/// <param name="AutoUpdate">Whether the app installs a newer pack by itself when it finds one.</param>
public sealed record PackCatalogReport(
    IReadOnlyList<PackCatalogEntryReport> Packs,
    IReadOnlyList<RegistryFailure> Failures,
    bool AutoUpdate);

/// <summary>Whether packs update by themselves, after <c>xxsm pack auto-update</c>.</summary>
/// <param name="AutoUpdate">On or off.</param>
/// <param name="Message">A sentence explaining it.</param>
public sealed record PackAutoUpdateReport(bool AutoUpdate, string Message);

/// <summary>One field a pack update withheld from a locked variant.</summary>
/// <param name="Field">The field.</param>
/// <param name="PackNew">What the new pack says.</param>
/// <param name="UserValue">What the user's own version says, which is what is kept.</param>
public sealed record SkippedFieldReport(string Field, string? PackNew, string? UserValue);

/// <summary>A change a pack update withheld because the variant is locked.</summary>
/// <param name="InternalName">The variant.</param>
/// <param name="DisplayName">Its name, for the review list.</param>
/// <param name="Changes">The fields the pack would have changed, and to what.</param>
public sealed record SkippedUpdateReport(
    string InternalName,
    string DisplayName,
    IReadOnlyList<SkippedFieldReport> Changes);

/// <summary>What one install or update did.</summary>
/// <param name="GameId">The game.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="PackVersion">The version now active.</param>
/// <param name="PreviousVersion">The version that was active before.</param>
/// <param name="Directory">Where it was installed.</param>
/// <param name="Message">A sentence explaining the outcome.</param>
/// <param name="SkippedUpdates">Changes withheld from variants the user has edited.</param>
/// <param name="AdoptionCandidates">Custom variants the pack has caught up with.</param>
/// <param name="Changes">What an update changed from the version it replaced; null for a first install.</param>
public sealed record PackOperationReport(
    string GameId,
    string Outcome,
    string? PackVersion,
    string? PreviousVersion,
    string? Directory,
    string Message,
    IReadOnlyList<SkippedUpdateReport> SkippedUpdates,
    IReadOnlyList<string> AdoptionCandidates,
    PackChanges? Changes);

/// <summary>What <c>xxsm pack changes</c> found recorded for a game.</summary>
/// <param name="GameId">The game.</param>
/// <param name="Changes">What its last update changed, or null when none since the first install.</param>
public sealed record PackChangesReport(string GameId, PackChanges? Changes);

/// <summary>What <c>xxsm pack skipped</c> found recorded for a game.</summary>
/// <param name="GameId">The game.</param>
/// <param name="PackVersion">The pack version that wanted to make the changes.</param>
/// <param name="RecordedAt">When they were withheld, or null when nothing is recorded.</param>
/// <param name="Updates">One entry per character, empty when nothing is recorded.</param>
public sealed record SkippedUpdatesRecordReport(
    string GameId,
    string? PackVersion,
    DateTimeOffset? RecordedAt,
    IReadOnlyList<SkippedUpdateReport> Updates);

/// <summary>The result of an update covering one or more games.</summary>
/// <param name="Results">One result per game considered.</param>
public sealed record PackUpdateReport(IReadOnlyList<PackOperationReport> Results);

/// <summary>The state of one game's pin after <c>xxsm pack pin</c>.</summary>
/// <param name="GameId">The game.</param>
/// <param name="PinnedVersion">The pinned version, or null when unpinned.</param>
/// <param name="Message">A sentence explaining the state.</param>
public sealed record PackPinReport(
    string GameId,
    string? PinnedVersion,
    string Message);

/// <summary>Which version of a game's pack is in use after <c>xxsm pack use</c>.</summary>
/// <param name="GameId">The game.</param>
/// <param name="ActiveVersion">The version now in use.</param>
/// <param name="PinnedVersion">The pin that holds it there, or null when it is the newest and follows updates.</param>
/// <param name="Message">A sentence explaining the state.</param>
public sealed record PackUseReport(string GameId, string ActiveVersion, string? PinnedVersion, string Message);

/// <summary>One folder or file a removal moved to the trash.</summary>
/// <param name="From">Where it was.</param>
/// <param name="To">Where it is in the trash.</param>
public sealed record PackTrashedReport(string From, string To);

/// <summary>What <c>xxsm pack remove</c> moved to the trash.</summary>
/// <param name="GameId">The game.</param>
/// <param name="PackVersion">The one version removed, or null when the whole pack was.</param>
/// <param name="CorrectionsRemoved">Whether the user's corrections went too.</param>
/// <param name="PinnedVersion">The pin the removal cleared, which a restore puts back.</param>
/// <param name="Trashed">Everything moved.</param>
/// <param name="Record">The record to pass to <c>xxsm pack restore</c>, or null when it could not be written.</param>
public sealed record PackRemovalReport(
    string GameId,
    string? PackVersion,
    bool CorrectionsRemoved,
    string? PinnedVersion,
    IReadOnlyList<PackTrashedReport> Trashed,
    string? Record);

/// <summary>An item <c>xxsm pack restore</c> left in the trash.</summary>
/// <param name="Path">Where it was to go back to.</param>
/// <param name="Reason">Why it stayed.</param>
public sealed record PackRestoreSkipReport(string Path, string Reason);

/// <summary>What <c>xxsm pack restore</c> put back.</summary>
/// <param name="GameId">The game.</param>
/// <param name="Restored">Everything that came back.</param>
/// <param name="Skipped">What stayed in the trash, and why.</param>
/// <param name="Complete">Whether everything came back.</param>
public sealed record PackRestoreReport(
    string GameId,
    IReadOnlyList<string> Restored,
    IReadOnlyList<PackRestoreSkipReport> Skipped,
    bool Complete);

/// <summary>What <c>xxsm pack keep</c> changed.</summary>
/// <param name="GameId">The game.</param>
/// <param name="PackVersion">The version.</param>
/// <param name="Kept">Whether it is kept now.</param>
/// <param name="KeptVersions">Every version of the game that is kept now.</param>
/// <param name="Message">The same, in words.</param>
public sealed record PackKeepReport(string GameId, string PackVersion, bool Kept, IReadOnlyList<string> KeptVersions, string Message);

/// <summary>What <c>xxsm pack prune</c> did, or would do.</summary>
/// <param name="DryRun">Whether nothing was moved.</param>
/// <param name="Items">Each old version, and what happened to it.</param>
/// <param name="Leftovers">Each folder an interrupted install left behind, and what happened to it.</param>
public sealed record PackPruneReport(bool DryRun, IReadOnlyList<PackPruneItemReport> Items, IReadOnlyList<PackLeftoverReport> Leftovers);

/// <summary>One folder an interrupted install left behind.</summary>
/// <param name="Path">The folder.</param>
/// <param name="Status"><c>wouldRemove</c>, <c>removed</c> or <c>failed</c>.</param>
/// <param name="Message">Why it could not go, in the operating system's words.</param>
public sealed record PackLeftoverReport(string Path, string Status, string? Message);

/// <summary>One old version.</summary>
/// <param name="GameId">The game.</param>
/// <param name="PackVersion">The version.</param>
/// <param name="Status"><c>wouldRemove</c>, <c>removed</c> or <c>failed</c>.</param>
/// <param name="RecordPath">The record <c>xxsm pack restore</c> takes to put it back, when it went.</param>
/// <param name="Message">Why it could not go, in the operating system's words.</param>
public sealed record PackPruneItemReport(string GameId, string PackVersion, string Status, string? RecordPath, string? Message);
