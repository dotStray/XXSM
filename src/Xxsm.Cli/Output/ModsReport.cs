namespace Xxsm.Cli.Output;

/// <summary>What a Mods folder contains, as <c>xxsm scan --mods</c> reports it.</summary>
/// <param name="ModsDirectory">The folder that was scanned.</param>
/// <param name="VariantFolderCount">How many character folders it holds.</param>
/// <param name="ModCount">How many mods, filed and unfiled.</param>
/// <param name="EnabledModCount">How many of those 3DMigoto will load.</param>
/// <param name="UnfiledModCount">How many are loose at the top of the folder.</param>
/// <param name="UnknownFolderCount">How many character folders the pack does not know.</param>
/// <param name="Folders">Every character folder, with its mods.</param>
/// <param name="UnfiledMods">Mods sitting loose at the top of the folder.</param>
/// <param name="Diagnostics">Anything odd noticed while reading.</param>
public sealed record ModsInventoryReport(
    string ModsDirectory,
    int VariantFolderCount,
    int ModCount,
    int EnabledModCount,
    int UnfiledModCount,
    int UnknownFolderCount,
    IReadOnlyList<ModsFolderReport> Folders,
    IReadOnlyList<ModReport> UnfiledMods,
    IReadOnlyList<ScanDiagnostic> Diagnostics);

/// <summary>One character folder on disk.</summary>
/// <param name="Name">The folder's name, exactly as it is on disk.</param>
/// <param name="KnownToPack">Whether a variant of this name exists in the pack; unknown is not an error.</param>
/// <param name="ModCount">How many mods it holds.</param>
/// <param name="EnabledCount">How many of those are enabled.</param>
/// <param name="Mods">The mods themselves.</param>
public sealed record ModsFolderReport(
    string Name,
    bool KnownToPack,
    int ModCount,
    int EnabledCount,
    IReadOnlyList<ModReport> Mods);

/// <summary>One mod on disk.</summary>
/// <param name="Name">The folder name without any <c>DISABLED_</c> prefix.</param>
/// <param name="DisplayName">The user's name for it, when they have given one.</param>
/// <param name="FolderName">The folder name exactly as it is on disk.</param>
/// <param name="Path">The mod's absolute path.</param>
/// <param name="Enabled">Whether 3DMigoto will load it.</param>
/// <param name="VariantOverride">The variant a person filed it under, when they have.</param>
/// <param name="Author">The mod's author, from its own details.</param>
/// <param name="ModUrl">Where it came from.</param>
/// <param name="SortedBy">Which tier of the sorter last placed it.</param>
/// <param name="ConfigError">Why its details could not be read, when they could not.</param>
public sealed record ModReport(
    string Name,
    string DisplayName,
    string FolderName,
    string Path,
    bool Enabled,
    string? VariantOverride,
    string? Author,
    string? ModUrl,
    string? SortedBy,
    string? ConfigError);

/// <summary>What one <c>xxsm mod</c> operation did.</summary>
/// <param name="Operation">enable, disable, rename, move, install or delete.</param>
/// <param name="From">Where the mod was.</param>
/// <param name="To">Where it is now, or where in the trash it went.</param>
/// <param name="Changed">Whether anything actually happened.</param>
/// <param name="Method">How it moved: none, rename, copy or copyVerifyTrash.</param>
/// <param name="RenamedFrom">The name it had to give up, when the destination was taken.</param>
/// <param name="TrashMethod">Which trash location a deletion used.</param>
/// <param name="TrashRecord">For a delete, the <c>.trashinfo</c> record for <c>xxsm mod restore</c>.</param>
/// <param name="FiledUnder">For a move or install, the character it is now filed under by hand, or
/// null.</param>
/// <param name="FilingError">Why that filing could not be remembered, in the operating system's words.</param>
/// <param name="PreviousFiling">For a move by hand, the character it was filed under before, or null; what
/// <c>xxsm mod move-back --filed-under</c> takes.</param>
public sealed record ModOperationReport(
    string Operation,
    string From,
    string To,
    bool Changed,
    string Method,
    string? RenamedFrom,
    string? TrashMethod,
    string? TrashRecord = null,
    string? FiledUnder = null,
    string? FilingError = null,
    string? PreviousFiling = null);

/// <summary>What <c>xxsm mod forget-filing</c> did.</summary>
/// <param name="Folder">The mod folder, which is not moved.</param>
/// <param name="Forgot">Whether a filing was forgotten; false when there was none, and nothing was
/// written.</param>
public sealed record ModFilingForgetReport(string Folder, bool Forgot);

/// <summary>What <c>xxsm mod set-preview</c> did.</summary>
/// <param name="Folder">The mod folder.</param>
/// <param name="Image">The stored picture, relative to the mod folder, as <c>imagePath</c> now declares it.</param>
/// <param name="Path">The stored picture's absolute path.</param>
public sealed record ModPreviewSetReport(string Folder, string Image, string Path);

/// <summary>What <c>xxsm mod clear-preview</c> did.</summary>
/// <param name="Folder">The mod folder.</param>
/// <param name="Cleared">Whether it had a picture to remove; false is not a failure.</param>
public sealed record ModPreviewClearReport(string Folder, bool Cleared);

/// <summary>What <c>xxsm mod set-details</c> left on the mod.</summary>
/// <param name="Folder">The mod folder.</param>
/// <param name="Author">Who made it, or <c>null</c> for nobody recorded.</param>
/// <param name="Version">Its version, or <c>null</c>.</param>
/// <param name="Description">What it is, or <c>null</c>.</param>
/// <param name="Notes">The user's own notes, or <c>null</c>.</param>
/// <param name="Changed">Whether anything was different from what was already there.</param>
public sealed record ModDetailsReport(
    string Folder,
    string? Author,
    string? Version,
    string? Description,
    string? Notes,
    bool Changed);

/// <summary>What <c>xxsm mod set-name</c> did.</summary>
/// <param name="Folder">The mod folder, which is not touched.</param>
/// <param name="FolderName">The folder's own name on disk, for contrast with the label.</param>
/// <param name="DisplayName">The name XXSM now shows, or <c>null</c> once it is cleared.</param>
/// <param name="Changed">Whether the label was different from what was already there.</param>
public sealed record ModDisplayNameReport(
    string Folder,
    string FolderName,
    string? DisplayName,
    bool Changed);

/// <summary>What <c>xxsm mod propose-name</c> found.</summary>
/// <param name="Wanted">The name that was asked about.</param>
/// <param name="Name">A name nothing else is using.</param>
/// <param name="Free">Whether the wanted name was itself free.</param>
/// <param name="CollidesWith">The existing name it clashes with, when it is not.</param>
public sealed record ModNameProposalReport(
    string Wanted,
    string Name,
    bool Free,
    string? CollidesWith);
