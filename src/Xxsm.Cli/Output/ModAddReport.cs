namespace Xxsm.Cli.Output;

/// <summary>What <c>xxsm mod add</c> found and did.</summary>
/// <param name="Source">The folder or archive that was read.</param>
/// <param name="IsArchive">Whether it was an archive.</param>
/// <param name="ModsDirectory">The Mods folder it would install into.</param>
/// <param name="Applied">Whether anything was actually copied.</param>
/// <param name="Candidates">Every mod found, with where it would go.</param>
/// <param name="StrandedFiles">Files in the source belonging to no mod.</param>
/// <param name="Outcomes">What happened to each mod that was installed.</param>
/// <param name="Notes">Anything noticed while reading the source.</param>
public sealed record ModAddReport(
    string Source,
    bool IsArchive,
    string ModsDirectory,
    bool Applied,
    IReadOnlyList<InstallCandidateReport> Candidates,
    IReadOnlyList<string> StrandedFiles,
    IReadOnlyList<InstallOutcomeReport> Outcomes,
    IReadOnlyList<string> Notes);

/// <summary>One mod an install source contains.</summary>
/// <param name="Name">The name it would be installed as.</param>
/// <param name="RelativePath">Where it sits inside the source. Empty for the source itself.</param>
/// <param name="VariantId">The character proposed, or null when nothing identified it.</param>
/// <param name="FolderName">The folder under the Mods directory it would land in.</param>
/// <param name="Reason">Why, in plain language.</param>
/// <param name="HashCount">How many hashes were found in it.</param>
/// <param name="FileCount">How many files it holds.</param>
/// <param name="Bytes">How many bytes they come to.</param>
/// <param name="Identified">Whether the sorter identified a character.</param>
/// <param name="VariantIsUncertain">Whether the outfit is a guess rather than a hash match.</param>
/// <param name="PreviewPath">The mod's own thumbnail, or null.</param>
/// <param name="Selected">Whether this run would install it.</param>
/// <param name="Files">The files it holds, relative to its own root, up to the listing limit.</param>
public sealed record InstallCandidateReport(
    string Name,
    string RelativePath,
    string? VariantId,
    string FolderName,
    string Reason,
    int HashCount,
    int FileCount,
    long Bytes,
    bool Identified,
    bool VariantIsUncertain,
    string? PreviewPath,
    bool Selected,
    IReadOnlyList<string> Files);

/// <summary>What installing one mod did.</summary>
/// <param name="Name">The mod's name.</param>
/// <param name="FolderName">The character folder it went into.</param>
/// <param name="InstalledPath">Where it landed, or null when it failed.</param>
/// <param name="Succeeded">Whether it was installed.</param>
/// <param name="Error">Why it failed, or null.</param>
/// <param name="MetadataError">Why its <c>.xxsm/mod.json</c> could not be written, or null; the mod is
/// installed either way.</param>
public sealed record InstallOutcomeReport(
    string Name,
    string FolderName,
    string? InstalledPath,
    bool Succeeded,
    string? Error,
    string? MetadataError);
