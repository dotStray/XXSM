namespace Xxsm.Cli.Output;

/// <summary>One game's settings, as <c>xxsm config show</c> reports them.</summary>
/// <param name="GameId">The game.</param>
/// <param name="ModsDirectory">Its Mods folder, or null when none is saved.</param>
/// <param name="ModsDirectoryExists">Whether that folder is there right now.</param>
/// <param name="SkinDisplayMode">How its families are shown in the grid.</param>
/// <param name="PinnedCharacters">Internal names pinned to the top of the grid.</param>
public sealed record ConfigGameReport(
    string GameId,
    string? ModsDirectory,
    bool ModsDirectoryExists,
    string SkinDisplayMode,
    IReadOnlyList<string> PinnedCharacters);

/// <summary>The auto-sort constants, both what is set and what is in force.</summary>
/// <param name="AmbiguityThreshold">How many variants a hash may span before it is ignored.</param>
/// <param name="MinScore">The score a family must reach to be accepted.</param>
/// <param name="MarginRatio">How far the winner must beat the runner-up.</param>
/// <param name="DefaultVariantConfidenceCeiling">The cap applied to a fallback member.</param>
/// <param name="IsDefault">Whether the user has overridden anything at all.</param>
public sealed record ConfigSortReport(
    int AmbiguityThreshold,
    int MinScore,
    double MarginRatio,
    double DefaultVariantConfidenceCeiling,
    bool IsDefault);

/// <summary>What XXSM is allowed to do with GameBanana.</summary>
/// <param name="Enabled">Whether XXSM may contact the site at all. Off by default.</param>
/// <param name="FetchOnInstall">Whether a mod installed from an address is filled in from its page.</param>
/// <param name="AddFromUrl">Whether the desktop app offers <em>From GameBanana…</em>.</param>
/// <param name="PasteStartsLookup">Whether a paste on a character page offers to add that mod.</param>
/// <param name="PasteIntoExisting">What a look-up on a mod you already have does.</param>
/// <param name="DownloadPictures">Whether a preview picture is fetched with the words.</param>
/// <param name="CheckForUpdates">Whether the background update check runs.</param>
/// <param name="CheckIntervalHours">How many hours between checks, as it is in force.</param>
/// <param name="CacheHours">How long a fetched page stays usable from disk, as it is in force.</param>
/// <param name="KeepDownloadsDays">How long a finished download stays on the list.</param>
/// <param name="DownloadHistory">How many downloads the list keeps at most.</param>
/// <param name="SecondsBetweenRequests">The wait between two requests, as it is in force.</param>
/// <param name="ApiBaseUrl">The API address in force.</param>
public sealed record ConfigGameBananaReport(
    bool Enabled,
    bool FetchOnInstall,
    bool AddFromUrl,
    bool PasteStartsLookup,
    string PasteIntoExisting,
    bool DownloadPictures,
    bool CheckForUpdates,
    double CheckIntervalHours,
    double CacheHours,
    double KeepDownloadsDays,
    int DownloadHistory,
    double SecondsBetweenRequests,
    string ApiBaseUrl);

/// <summary>Everything <c>xxsm config show</c> reports.</summary>
/// <param name="SettingsPath">The file the settings live in.</param>
/// <param name="PackPreferencesPath">The file the registries and pins live in.</param>
/// <param name="Theme">The chosen colour scheme.</param>
/// <param name="FirstRunCompleted">Whether the desktop first-run flow has been completed.</param>
/// <param name="LastGameId">The game the app last had selected.</param>
/// <param name="ShowPackStudio">Whether the desktop app shows Pack Studio in its rail.</param>
/// <param name="CheckForAppUpdates">Whether the desktop app asks at start and daily if a newer XXSM is out.</param>
/// <param name="ColumnWidths">The column widths the user has set in the desktop app's tables.</param>
/// <param name="Registries">The registries in force, compiled-in default included.</param>
/// <param name="Sort">The auto-sort constants in force.</param>
/// <param name="GameBanana">What XXSM may do with GameBanana.</param>
/// <param name="Games">Per-game settings, in game-id order.</param>
/// <param name="Directories">Where XXSM keeps its own files on this machine.</param>
public sealed record ConfigReport(
    string SettingsPath,
    string PackPreferencesPath,
    string Theme,
    bool FirstRunCompleted,
    string? LastGameId,
    bool ShowPackStudio,
    bool CheckForAppUpdates,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> ColumnWidths,
    IReadOnlyList<string> Registries,
    ConfigSortReport Sort,
    ConfigGameBananaReport GameBanana,
    IReadOnlyList<ConfigGameReport> Games,
    IReadOnlyList<ConfigDirectoryReport> Directories);

/// <summary>One of XXSM's own directories.</summary>
public sealed record ConfigDirectoryReport(string Purpose, string Path);

/// <summary>What <c>xxsm config reset</c> did, or would do.</summary>
/// <param name="DryRun">Whether nothing was moved.</param>
/// <param name="Directories">XXSM's own folders, which the items come from.</param>
/// <param name="Kept">What was left in place inside them: the logs, and any Mods folder.</param>
/// <param name="Items">Each file or folder, and what happened to it.</param>
public sealed record ConfigResetReport(
    bool DryRun,
    IReadOnlyList<string> Directories,
    IReadOnlyList<string> Kept,
    IReadOnlyList<ConfigResetItemReport> Items);

/// <summary>One item of a reset.</summary>
/// <param name="Path">Where it was.</param>
/// <param name="Status"><c>wouldMove</c>, <c>moved</c> or <c>failed</c>.</param>
/// <param name="TrashedPath">Where it is now, when it moved.</param>
/// <param name="Message">Why it could not move, in the operating system's words.</param>
public sealed record ConfigResetItemReport(string Path, string Status, string? TrashedPath, string? Message);

/// <summary>What <c>xxsm version</c> prints with <c>--json</c>.</summary>
/// <param name="Version">The running version.</param>
/// <param name="Latest">The newest release's version, when <c>--check</c> asked; null otherwise.</param>
/// <param name="Newer">Whether that release is newer than the running one; null without <c>--check</c>.</param>
/// <param name="Page">The release's page, where the downloads are; null without <c>--check</c>.</param>
public sealed record VersionReport(string Version, string? Latest, bool? Newer, string? Page);
