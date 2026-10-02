using Xxsm.Core.Profiles;
using Xxsm.Packs.Importing;

namespace Xxsm.Cli.Output;

/// <summary>One profile, as <c>xxsm profile list</c> and <c>show</c> report it.</summary>
internal sealed record ProfileReport(
    string Id,
    string Name,
    bool ReadOnly,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int EnabledCount,
    IReadOnlyList<ProfileEntryReport>? Enabled);

/// <summary>One switched-on mod in a profile.</summary>
internal sealed record ProfileEntryReport(string? Id, string Path, string? Name);

/// <summary><c>xxsm profile list</c>.</summary>
internal sealed record ProfileListReport(
    string ModsDirectory,
    string ProfilesDirectory,
    IReadOnlyList<ProfileReport> Profiles,
    IReadOnlyList<ProfileFileProblem> Problems);

/// <summary><c>xxsm profile save</c> and <c>update</c>.</summary>
internal sealed record ProfileSaveReport(ProfileReport Profile, IReadOnlyList<string> RecordedByFolder);

/// <summary><c>xxsm profile add</c> and <c>remove</c>: what changed in the profile.</summary>
internal sealed record ProfileEditReport(
    ProfileReport Profile,
    IReadOnlyList<ProfileEntryReport> Changed,
    int AlreadyThere,
    IReadOnlyList<string> RecordedByFolder,
    IReadOnlyList<string> NotInProfile);

/// <summary><c>xxsm profile delete</c>.</summary>
internal sealed record ProfileDeleteReport(string Id, string Name, string TrashedPath, string RestoreRecord);

/// <summary>One mod a profile switches.</summary>
internal sealed record ProfileSwitchReport(string Mod, bool Enable, string? Error);

/// <summary>A mod the profile names more than once.</summary>
internal sealed record ProfileCopyReport(string Path, string Chosen, IReadOnlyList<string> Others);

/// <summary><c>xxsm profile apply</c>.</summary>
internal sealed record ProfileApplyReport(
    string Profile,
    bool Applied,
    string? RunId,
    int EnableCount,
    int DisableCount,
    int UnchangedCount,
    IReadOnlyList<ProfileSwitchReport> Switches,
    IReadOnlyList<ProfileEntryReport> Missing,
    IReadOnlyList<ProfileCopyReport> Copies,
    int FailureCount);

/// <summary><c>xxsm mod disable-all</c>.</summary>
internal sealed record DisableAllReport(
    bool Applied,
    string? RunId,
    int DisableCount,
    int AlreadyOffCount,
    IReadOnlyList<ProfileSwitchReport> Switches,
    int FailureCount);

/// <summary>One run in <c>xxsm switches list</c>.</summary>
internal sealed record SwitchRunReport(
    string RunId,
    DateTimeOffset At,
    string Source,
    string? Label,
    int SwitchCount,
    int UndoneCount,
    bool FullyUndone);

/// <summary><c>xxsm switches list</c>.</summary>
internal sealed record SwitchRunsReport(string ModsDirectory, string JournalPath, IReadOnlyList<SwitchRunReport> Runs);

/// <summary>One switch put back, or not, by <c>xxsm switches undo</c>.</summary>
internal sealed record SwitchUndoRowReport(string From, string To, bool Restored, string? Reason);

/// <summary><c>xxsm switches undo</c>.</summary>
internal sealed record SwitchUndoReport(string RunId, int RestoredCount, int SkippedCount, IReadOnlyList<SwitchUndoRowReport> Rows);

/// <summary>One character in <c>xxsm mod randomise</c>.</summary>
internal sealed record RandomiseRowReport(string Character, string Chosen, string ChosenName, IReadOnlyList<string> SwitchedOff);

/// <summary>A mod the randomiser could not switch.</summary>
internal sealed record RandomiseFailureReport(string Mod, string Error);

/// <summary><c>xxsm mod randomise</c>.</summary>
internal sealed record RandomiseReport(
    bool Applied,
    string? RunId,
    IReadOnlyList<RandomiseRowReport> Rows,
    int EnabledCount,
    int DisabledCount,
    IReadOnlyList<RandomiseFailureReport> Failures);

/// <summary>One line of a key binding in <c>xxsm mod keys</c>.</summary>
internal sealed record ModKeyFieldReport(string Kind, string Name, int Line, string Value);

/// <summary>One key binding in <c>xxsm mod keys</c>.</summary>
internal sealed record ModKeySectionReport(string File, string Section, string? Type, IReadOnlyList<ModKeyFieldReport> Fields);

/// <summary><c>xxsm mod keys</c>.</summary>
internal sealed record ModKeysReport(string Mod, bool Changed, IReadOnlyList<ModKeySectionReport> Sections, IReadOnlyList<string> Problems);

/// <summary><c>xxsm mod export</c>.</summary>
internal sealed record ModExportReport(
    string ModsDirectory,
    string? Folder,
    int ModCount,
    int SkippedDisabledCount,
    int FileCount,
    long Bytes,
    bool EnabledOnly,
    bool SkipMetadata,
    bool OneFolder,
    string Switch,
    int RenamedCount);

/// <summary>One mod in <c>xxsm mod import</c>.</summary>
internal sealed record ModImportRowReport(
    string Mod,
    string Source,
    IReadOnlyList<string> Fills,
    string? Tag,
    string? Character,
    string? HashCharacter,
    bool FollowTag,
    string? Problem);

/// <summary>A mod <c>xxsm mod import</c> could not write.</summary>
internal sealed record ModImportFailureReport(string Mod, string Error);

/// <summary><c>xxsm mod import</c>.</summary>
internal sealed record ModImportReport(
    string ModsDirectory,
    bool Applied,
    int JasmCount,
    int XxModManagerCount,
    IReadOnlyList<ModImportRowReport> Rows,
    IReadOnlyList<UnmatchedCharacterTag> UnmatchedTags,
    int WrittenCount,
    IReadOnlyList<ModImportFailureReport> Failures);
