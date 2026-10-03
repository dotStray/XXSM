namespace Xxsm.Cli.Output;

/// <summary>One field a look-up offers to fill in, as <c>xxsm mod fetch</c> reports it.</summary>
/// <param name="Field">Which field: name, author, version, description or picture.</param>
/// <param name="Current">What the mod has now, or null when it has nothing.</param>
/// <param name="Proposed">What the page says, or null when it says nothing either.</param>
/// <param name="Selected">Whether this run would take it.</param>
/// <param name="Changes">Whether taking it would change anything at all.</param>
public sealed record ModFetchFieldReport(
    string Field,
    string? Current,
    string? Proposed,
    bool Selected,
    bool Changes);

/// <summary>What <c>xxsm mod fetch</c> found and did.</summary>
/// <param name="ModFolder">The mod folder that was looked up.</param>
/// <param name="ModId">The GameBanana mod id it was matched to.</param>
/// <param name="PageUrl">The mod's page.</param>
/// <param name="Name">The page's title.</param>
/// <param name="Author">Who submitted it.</param>
/// <param name="Version">The author's own version string.</param>
/// <param name="Summary">The opening line or two of the description.</param>
/// <param name="Applied">Whether anything was written.</param>
/// <param name="Fields">Every field, with what would change.</param>
/// <param name="Written">Which fields were written, when <paramref name="Applied"/> is true.</param>
/// <param name="PictureError">Why the picture could not be fetched, or null.</param>
public sealed record ModFetchReport(
    string ModFolder,
    long ModId,
    string PageUrl,
    string? Name,
    string? Author,
    string? Version,
    string? Summary,
    bool Applied,
    IReadOnlyList<ModFetchFieldReport> Fields,
    IReadOnlyList<string> Written,
    string? PictureError);

/// <summary>One mod's standing against its page, as <c>xxsm mod updates</c> reports it.</summary>
/// <param name="ModFolder">The mod folder.</param>
/// <param name="Name">What the mod is called.</param>
/// <param name="ModId">Its GameBanana mod id.</param>
/// <param name="PageUrl">Its page.</param>
/// <param name="InstalledVersion">The version recorded when it was installed or linked.</param>
/// <param name="LatestVersion">The version its page carries now, when it was asked.</param>
/// <param name="HasUpdate">Whether the page has changed since XXSM last agreed with it.</param>
/// <param name="Error">Why it could not be asked about, or null.</param>
public sealed record ModUpdateStatusReport(
    string ModFolder,
    string Name,
    long ModId,
    string PageUrl,
    string? InstalledVersion,
    string? LatestVersion,
    bool HasUpdate,
    string? Error);

/// <summary>What <c>xxsm mod updates</c> found.</summary>
/// <param name="ModsDirectory">The Mods folder that was read.</param>
/// <param name="Checked">Whether GameBanana was asked anything at all.</param>
/// <param name="Linked">How many mods are linked to a GameBanana page.</param>
/// <param name="NotDue">How many linked mods were not due to be asked about.</param>
/// <param name="Updates">Every mod with a newer version waiting.</param>
/// <param name="Statuses">Every mod that was looked at, update or not.</param>
public sealed record ModUpdatesReport(
    string ModsDirectory,
    bool Checked,
    int Linked,
    int NotDue,
    IReadOnlyList<ModUpdateStatusReport> Updates,
    IReadOnlyList<ModUpdateStatusReport> Statuses);

/// <summary>What <c>xxsm mod updates --accept</c> did.</summary>
/// <param name="ModFolder">The mod folder.</param>
/// <param name="Cleared">Whether there was a mark to clear.</param>
public sealed record ModUpdateAcceptReport(string ModFolder, bool Cleared);

/// <summary>One file an update changes, as <c>xxsm mod update</c> reports it.</summary>
/// <param name="Path">Its path inside the mod.</param>
/// <param name="Kind">added, replaced or removed.</param>
/// <param name="MayBeEdited">Whether it is an .ini written after the mod was added.</param>
public sealed record ModUpdateChangeReport(string Path, string Kind, bool MayBeEdited);

/// <summary>What <c>xxsm mod update</c> found and did.</summary>
/// <param name="ModFolder">The mod.</param>
/// <param name="InstalledVersion">The version installed before.</param>
/// <param name="NewVersion">The version on the page.</param>
/// <param name="FileId">The page's file the update came from.</param>
/// <param name="FileName">That file's name.</param>
/// <param name="Changes">Every file added, replaced or removed.</param>
/// <param name="Unchanged">How many files are the same.</param>
/// <param name="Applied">Whether the mod was replaced.</param>
/// <param name="TrashedTo">Where the previous version went, when it was.</param>
/// <param name="IniChangesKept">Whether the user's key and default changes were to be made again in the new version.</param>
/// <param name="CarriedIniChanges">What became of each of them; empty when none were carried or nothing was applied.</param>
public sealed record ModUpdatePlanReport(
    string ModFolder,
    string? InstalledVersion,
    string? NewVersion,
    long? FileId,
    string? FileName,
    IReadOnlyList<ModUpdateChangeReport> Changes,
    int Unchanged,
    bool Applied,
    string? TrashedTo,
    bool IniChangesKept,
    IReadOnlyList<ModIniCarriedReport> CarriedIniChanges);

/// <summary>One of the user's key or default changes, carried to a new version or not.</summary>
/// <param name="File">The INI.</param>
/// <param name="Section">Its section.</param>
/// <param name="Name">The line's name.</param>
/// <param name="Yours">The user's value.</param>
/// <param name="NewAuthor">The new version's own value; null when it has no such line.</param>
/// <param name="Outcome"><c>applied</c>, <c>clashed</c> (the user's value replaced a new one of the author's) or <c>missing</c>.</param>
public sealed record ModIniCarriedReport(string File, string Section, string Name, string Yours, string? NewAuthor, string Outcome);

/// <summary>What <c>xxsm mod link</c> left on the mod.</summary>
/// <param name="ModFolder">The mod folder.</param>
/// <param name="ModUrl">Its address now, or null for none.</param>
/// <param name="ModId">The GameBanana mod it is linked to, or null when it is not linked.</param>
public sealed record ModLinkReport(string ModFolder, string? ModUrl, long? ModId);

/// <summary>What <c>xxsm mod updates --pretend</c> did.</summary>
/// <param name="ModFolder">The mod's own folder.</param>
/// <param name="Marked">Whether it is now marked; false when it is not linked to a page.</param>
public sealed record ModUpdatePretendReport(string ModFolder, bool Marked);

/// <summary>One entry of the download list, as <c>xxsm mod downloads</c> reports it.</summary>
/// <param name="Id">The entry's own id, for <c>--remove</c>.</param>
/// <param name="ModId">The GameBanana mod id it came from.</param>
/// <param name="Name">What the mod is called.</param>
/// <param name="Author">Who submitted it.</param>
/// <param name="Version">The author's own version string.</param>
/// <param name="PageUrl">The mod's page.</param>
/// <param name="FileName">The file that was fetched.</param>
/// <param name="State">What became of it: running, ready, installed, cancelled, failed, blocked.</param>
/// <param name="Bytes">How many bytes arrived.</param>
/// <param name="TotalBytes">How many there are altogether, when the server said.</param>
/// <param name="ArchivePath">The archive on disk, while there is one.</param>
/// <param name="InstalledPath">Where the mod went, once it was installed.</param>
/// <param name="GameId">The game it was started for.</param>
/// <param name="StartedAt">When it started.</param>
/// <param name="FinishedAt">When it stopped, however it stopped.</param>
/// <param name="Error">Why it stopped, when something went wrong.</param>
public sealed record ModDownloadReport(
    string Id,
    long ModId,
    string? Name,
    string? Author,
    string? Version,
    string? PageUrl,
    string? FileName,
    string State,
    long Bytes,
    long? TotalBytes,
    string? ArchivePath,
    string? InstalledPath,
    string? GameId,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? Error);

/// <summary>What <c>xxsm mod downloads</c> found and did.</summary>
/// <param name="Path">Where the list is kept.</param>
/// <param name="KeptForDays">How long a finished download stays on it.</param>
/// <param name="Removed">How many entries this run took off it.</param>
/// <param name="Downloads">The entries, newest first.</param>
public sealed record ModDownloadsReport(
    string Path,
    double KeptForDays,
    int Removed,
    IReadOnlyList<ModDownloadReport> Downloads);
