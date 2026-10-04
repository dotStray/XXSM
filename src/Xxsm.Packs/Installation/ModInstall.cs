using Xxsm.Core.Archives;
using Xxsm.Core.Diagnostics;
using Xxsm.Core.Mods;
using Xxsm.Packs.Characters;
using Xxsm.Packs.Sorting;

namespace Xxsm.Packs.Installation;

/// <summary>One mod an install source turned out to contain, with the sorter's answer for it.</summary>
public sealed record InstallCandidate
{
    /// <summary>How many entries <see cref="Files"/> will carry at most.</summary>
    public const int FileListLimit = 1000;

    /// <summary>Where the mod's files are right now: a staging directory, or the folder itself.</summary>
    public required string SourcePath { get; init; }

    /// <summary>Where it sat inside the archive, <c>/</c>-separated; empty when the source root is the mod.</summary>
    public required string RelativePath { get; init; }

    /// <summary>The name the installed mod will be given unless the user changes it.</summary>
    public required string Name { get; init; }

    /// <summary>How many files it holds, at any depth.</summary>
    public required int FileCount { get; init; }

    /// <summary>The files it holds, relative, <c>/</c>-separated and in path order; bounded by a limit.</summary>
    public required IReadOnlyList<string> Files { get; init; }

    /// <summary>How many bytes they come to.</summary>
    public required long Bytes { get; init; }

    /// <summary>The hashes found in it and what the sorter made of them.</summary>
    public required LearnedFromMod Learned { get; init; }

    /// <summary>The variant the sorter proposes, or null; it then goes to <c>Others</c> unless one is picked.</summary>
    public required string? SuggestedVariantId { get; init; }

    /// <summary>The folder name the proposal lands in, relative to the Mods folder.</summary>
    public required string SuggestedFolderName { get; init; }

    /// <summary>A short plain-language account of why, for the confirm step.</summary>
    public required string Reason { get; init; }

    /// <summary>The mod's own thumbnail inside the source, absolute, or null when it has none.</summary>
    public string? PreviewPath { get; init; }

    /// <summary>Whether the sorter identified a character at all.</summary>
    public bool WasIdentified => SuggestedVariantId is { Length: > 0 };

    /// <summary>The folders this mod is made of, relative to it, when a source's folders were read as parts of one mod;
    /// empty otherwise.</summary>
    public IReadOnlyList<string> IncludedParts { get; init; } = [];

    /// <summary>Whether <see cref="Files"/> stops short of <see cref="FileCount"/>.</summary>
    public bool FileListIsTruncated => FileCount > Files.Count;

    /// <summary>Whether the outfit within the family is a guess rather than a hash match.</summary>
    public bool VariantIsUncertain =>
        Learned.Decision.MemberDecidedByName || Learned.Decision.IsDefaultVariantFallback;
}

/// <summary>What the user decided about one candidate. Metadata is written only when some is given.</summary>
/// <param name="Candidate">The candidate this is about.</param>
/// <param name="VariantId">The variant to file it under, or null for the suggested one.</param>
/// <param name="Name">The folder name to install it as, or null for the candidate's own.</param>
/// <param name="DisplayName">Its <c>customName</c>; null, or the landed folder's name, leaves it unset.</param>
/// <param name="Author">Its <c>author</c>. Null leaves it unset.</param>
/// <param name="ModUrl">Its <c>modUrl</c>. Null leaves it unset.</param>
/// <param name="Notes">Its <c>notes</c>. Null leaves it unset.</param>
/// <param name="Version">Its <c>version</c>. Null leaves it unset.</param>
/// <param name="Description">Its <c>description</c>. Null leaves it unset.</param>
/// <param name="GameBanana">Its <c>gameBanana</c> block, which update checks compare against; null for a
/// mod not from GameBanana.</param>
/// <param name="PreviewImage">A picture given on the install screen, stored in the mod's <c>.xxsm/</c>; null
/// keeps the mod's own.</param>
public sealed record InstallChoice(
    InstallCandidate Candidate,
    string? VariantId = null,
    string? Name = null,
    string? DisplayName = null,
    string? Author = null,
    string? ModUrl = null,
    string? Notes = null,
    PreviewImageSource? PreviewImage = null,
    string? Version = null,
    string? Description = null,
    ModGameBananaInfo? GameBanana = null);

/// <summary>Whether added mods arrive switched on or off.</summary>
public enum InstallSwitching
{
    /// <summary>Each mod is added switched off.</summary>
    Off,

    /// <summary>The one mod is added switched on, and the other mods in its character folder are switched off.</summary>
    OnlyThis,

    /// <summary>Each mod is added as it came, which is switched on unless its folder says otherwise.</summary>
    AsItIs,
}

/// <summary>One way to read a source: as one mod, or as separate mods.</summary>
/// <param name="IsOneMod">Whether the source's folders are read as parts of one mod.</param>
/// <param name="Candidates">The mods this reading installs.</param>
/// <param name="StrandedFiles">Files in the source that none of them holds, relative to its root; left behind.</param>
public sealed record InstallGrouping(
    bool IsOneMod,
    IReadOnlyList<InstallCandidate> Candidates,
    IReadOnlyList<string> StrandedFiles);

/// <summary>What an install source contains, before anything is copied. Dispose to delete its staging.</summary>
public sealed class InstallPlan : IDisposable
{
    private readonly ExtractedArchive? _archive;
    private bool _disposed;

    internal InstallPlan(
        string source,
        string modsDirectory,
        ExtractedArchive? archive,
        InstallGrouping suggested,
        InstallGrouping? alternative,
        string? groupingReason,
        IReadOnlyList<Diagnostic> diagnostics,
        string? targetVariantId)
    {
        Source = source;
        ModsDirectory = modsDirectory;
        _archive = archive;
        Suggested = suggested;
        Alternative = alternative;
        GroupingReason = groupingReason;
        Diagnostics = diagnostics;
        TargetVariantId = targetVariantId;
    }

    /// <summary>The folder or archive the user pointed at.</summary>
    public string Source { get; }

    /// <summary>The character the install was aimed at, or null; such installs count as filed by hand.</summary>
    public string? TargetVariantId { get; }

    /// <summary>The Mods folder everything will be installed under.</summary>
    public string ModsDirectory { get; }

    /// <summary>How XXSM reads the source: one mod, or separate mods.</summary>
    public InstallGrouping Suggested { get; }

    /// <summary>The other way to read it, or null when there is no other: a source that is one mod, or one folder.</summary>
    public InstallGrouping? Alternative { get; }

    /// <summary>Why <see cref="Suggested"/> reads it that way, or null when there is no <see cref="Alternative"/>.</summary>
    public string? GroupingReason { get; }

    /// <summary>The mods <see cref="Suggested"/> installs, in path order.</summary>
    public IReadOnlyList<InstallCandidate> Candidates => Suggested.Candidates;

    /// <summary>Files in the source that <see cref="Suggested"/> leaves behind, relative to its root.</summary>
    public IReadOnlyList<string> StrandedFiles => Suggested.StrandedFiles;

    /// <summary>Anything noticed while reading the source. Never fatal.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>Whether the source was an archive rather than a folder.</summary>
    public bool IsArchive => _archive is not null;

    /// <summary>Whether anything installable was found.</summary>
    public bool HasCandidates => Candidates.Count > 0;

    /// <summary>Deletes the staging directory, for an archive source.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _archive?.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>What installing one candidate did.</summary>
/// <param name="Choice">The choice that was applied.</param>
/// <param name="DestinationFolderName">The character folder it went into, or was going to.</param>
/// <param name="Result">What the copy did, or null when it failed.</param>
/// <param name="Error">Why it failed, or null when it succeeded.</param>
/// <param name="MetadataError">Why <c>.xxsm/mod.json</c> could not be written, or null; the mod is installed
/// either way.</param>
public sealed record InstallOutcome(
    InstallChoice Choice,
    string DestinationFolderName,
    ModOperationResult? Result,
    string? Error,
    string? MetadataError = null)
{
    /// <summary>Whether the mod was installed.</summary>
    public bool Succeeded => Result is { Changed: true };

    /// <summary>The installed mod's path, for landing the user on what they just added.</summary>
    public string? InstalledPath => Result?.ToPath;
}

/// <summary>The result of applying an install.</summary>
public sealed record InstallResult
{
    /// <summary>One outcome per choice that was handed in.</summary>
    public required IReadOnlyList<InstallOutcome> Outcomes { get; init; }

    /// <summary>How many mods were installed.</summary>
    public int InstalledCount => Outcomes.Count(outcome => outcome.Succeeded);

    /// <summary>The ones that failed, with the reason on each.</summary>
    public IReadOnlyList<InstallOutcome> Failures => [.. Outcomes.Where(outcome => outcome.Error is not null)];

    /// <summary>The other mods switched off for <see cref="InstallSwitching.OnlyThis"/>, journalled for an undo; null
    /// when that was not asked for or no other mod was on.</summary>
    public ModSwitchRunResult? SwitchedOff { get; init; }

    /// <summary>The ones that installed but whose metadata could not be written.</summary>
    public IReadOnlyList<InstallOutcome> MetadataFailures =>
        [.. Outcomes.Where(outcome => outcome.MetadataError is not null)];
}

/// <summary>The mod install flow: read a source, propose where each mod goes, copy the confirmed ones.</summary>
public interface IModInstaller
{
    /// <summary>Reads a folder or an archive and proposes what to do with it.</summary>
    /// <param name="source">A mod folder, a folder of mods, or an archive.</param>
    /// <param name="data">The merged game data, for the sorter's proposals.</param>
    /// <param name="modsDirectory">The Mods folder everything would be installed under.</param>
    /// <param name="targetVariantId">A character the user already chose; overrides the sorter for every
    /// candidate.</param>
    /// <param name="settings">Sorting thresholds. Defaults to <see cref="SortSettings.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the read. A cancelled plan leaves nothing behind.</param>
    /// <returns>What was found. Dispose it when the install is done or abandoned.</returns>
    /// <exception cref="Xxsm.Core.ModOperationException">The source does not exist, or an archive could not be
    /// unpacked.</exception>
    Task<InstallPlan> PlanAsync(
        string source,
        Merge.GameData data,
        string modsDirectory,
        string? targetVariantId = null,
        SortSettings? settings = null,
        CancellationToken cancellationToken = default);

    /// <summary>Copies the confirmed mods into the Mods folder; one failure does not stop the rest.</summary>
    /// <param name="plan">The plan the choices came from.</param>
    /// <param name="choices">The candidates to install, with any per-row override of target or name.</param>
    /// <param name="data">The merged game data, for resolving a chosen variant to its folder.</param>
    /// <param name="switching">Whether the mods arrive switched on or off.</param>
    /// <param name="cancellationToken">Cancels between mods. Mods already installed stay installed.</param>
    /// <exception cref="Xxsm.Core.ModOperationException"><see cref="InstallSwitching.OnlyThis"/> with other than one
    /// mod, or with one going to <c>Others</c>.</exception>
    Task<InstallResult> ApplyAsync(
        InstallPlan plan,
        IReadOnlyList<InstallChoice> choices,
        Merge.GameData data,
        InstallSwitching switching = InstallSwitching.AsItIs,
        CancellationToken cancellationToken = default);

    /// <summary>Whether <see cref="InstallSwitching.OnlyThis"/> makes sense: one mod, going to a character.</summary>
    /// <param name="choices">The mods about to be installed.</param>
    /// <param name="data">The merged game data.</param>
    bool CanSwitchOnlyThis(IReadOnlyList<InstallChoice> choices, Merge.GameData data);
}
