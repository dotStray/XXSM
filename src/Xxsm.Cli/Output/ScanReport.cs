namespace Xxsm.Cli.Output;

/// <summary>The machine-readable payload of <c>xxsm scan</c>.</summary>
/// <param name="GameId">The game scanned.</param>
/// <param name="DisplayName">The game's display name.</param>
/// <param name="PackVersion">The active pack version, or null when none is installed.</param>
/// <param name="PackDirectory">Where the active pack is installed, or null.</param>
/// <param name="VariantCount">How many variants there are in total.</param>
/// <param name="FamilyCount">How many distinct families.</param>
/// <param name="SkinCount">How many variants are alternate outfits.</param>
/// <param name="HiddenCount">How many the user has hidden.</param>
/// <param name="CustomisedCount">How many the user has edited or created.</param>
/// <param name="HashesPendingCount">How many have no hashes yet.</param>
/// <param name="HashEntryCount">How many hash entries across every variant.</param>
/// <param name="Variants">Every variant.</param>
/// <param name="Diagnostics">Everything the loader and merge noticed.</param>
/// <param name="Mods">What is on disk when a Mods folder was given; null when only the pack was scanned.</param>
public sealed record ScanReport(
    string GameId,
    string DisplayName,
    string? PackVersion,
    string? PackDirectory,
    int VariantCount,
    int FamilyCount,
    int SkinCount,
    int HiddenCount,
    int CustomisedCount,
    int HashesPendingCount,
    int HashEntryCount,
    IReadOnlyList<ScanVariant> Variants,
    IReadOnlyList<ScanDiagnostic> Diagnostics,
    ModsInventoryReport? Mods = null);

/// <summary>One variant in a scan report.</summary>
/// <param name="InternalName">The stable id, which is also its folder name.</param>
/// <param name="DisplayName">The name shown to users.</param>
/// <param name="FamilyId">The family this belongs to.</param>
/// <param name="BaseCharacterId">The base character, when this is an alternate outfit.</param>
/// <param name="IsDefaultVariant">Whether this is the family's fallback.</param>
/// <param name="Origin">Whether this came from the pack, was edited, or was user-created.</param>
/// <param name="IsLocked">Whether pack updates are blocked for it.</param>
/// <param name="Hidden">Whether the user has hidden it.</param>
/// <param name="HashCount">How many hashes it has.</param>
/// <param name="HashesPending">Whether it is awaiting hashes.</param>
/// <param name="ModFilesName">The prefix the filename fallback matches.</param>
/// <param name="HasImage">Whether it has a portrait.</param>
/// <param name="Attributes">Its attribute values, keyed by attribute id.</param>
public sealed record ScanVariant(
    string InternalName,
    string DisplayName,
    string FamilyId,
    string? BaseCharacterId,
    bool IsDefaultVariant,
    string Origin,
    bool IsLocked,
    bool Hidden,
    int HashCount,
    bool HashesPending,
    string ModFilesName,
    bool HasImage,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Attributes);

/// <summary>One diagnostic in a scan report.</summary>
/// <param name="Severity">How much it matters.</param>
/// <param name="Code">The stable code.</param>
/// <param name="Message">Plain language.</param>
/// <param name="File">The file it was found in.</param>
/// <param name="Subject">The variant or key it relates to.</param>
/// <param name="Paths">The folders or files it is about, the one to act on first.</param>
public sealed record ScanDiagnostic(
    string Severity,
    string Code,
    string Message,
    string? File,
    string? Subject,
    IReadOnlyList<string>? Paths = null);

/// <summary>The machine-readable payload of <c>xxsm pack import</c>.</summary>
/// <param name="GameId">The game.</param>
/// <param name="PackVersion">The version installed.</param>
/// <param name="Directory">Where it was installed.</param>
/// <param name="VariantCount">How many variants it contains.</param>
/// <param name="HashEntryCount">How many hash entries it contains.</param>
/// <param name="Diagnostics">Everything the loader noticed while verifying it.</param>
/// <param name="SkippedUpdates">How many edited characters the pack's changes were withheld from.</param>
public sealed record PackImportReport(
    string GameId,
    string PackVersion,
    string Directory,
    int VariantCount,
    int HashEntryCount,
    IReadOnlyList<ScanDiagnostic> Diagnostics,
    int SkippedUpdates);

/// <summary>The machine-readable payload of <c>xxsm pack list</c>.</summary>
public sealed record PackListReport(IReadOnlyList<PackListEntry> Packs);

/// <summary>One installed pack version.</summary>
/// <param name="GameId">The game.</param>
/// <param name="PackVersion">The version.</param>
/// <param name="Directory">Where it is installed.</param>
/// <param name="IsActive">Whether this is the version XXSM uses for that game.</param>
/// <param name="Contents">What the version in use holds; null for the others.</param>
public sealed record PackListEntry(
    string GameId, string PackVersion, string Directory, bool IsActive, Xxsm.Packs.Loading.PackContents? Contents = null);
