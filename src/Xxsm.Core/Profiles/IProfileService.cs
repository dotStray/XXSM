using Xxsm.Core.Io;
using Xxsm.Core.Mods;

namespace Xxsm.Core.Profiles;

/// <summary>Saves, lists and applies profiles: named sets of switched-on mods, kept per Mods folder.</summary>
public interface IProfileService
{
    /// <summary>Where a Mods folder's profiles are kept. Nothing is created.</summary>
    /// <param name="modsDirectory">The Mods folder.</param>
    /// <returns>The absolute path of <c>.xxsm/profiles/</c>.</returns>
    string GetProfilesDirectory(string modsDirectory);

    /// <summary>Reads every profile saved for a Mods folder.</summary>
    /// <param name="modsDirectory">The Mods folder.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The profiles in name order, and every profile file that could not be read.</returns>
    Task<ProfileList> ListAsync(string modsDirectory, CancellationToken cancellationToken = default);

    /// <summary>Saves which mods are on now as a new profile, giving any mod without an id one.</summary>
    /// <param name="modsDirectory">The Mods folder.</param>
    /// <param name="name">What to call it. Must not be blank or already taken, ignoring case.</param>
    /// <param name="cancellationToken">Cancels before the profile is written.</param>
    /// <returns>The profile, and the switched-on mods it could only record by folder.</returns>
    /// <exception cref="ProfileException">The name is blank or taken.</exception>
    /// <exception cref="ModOperationException">The Mods folder could not be read, or the profile could not be
    /// written.</exception>
    Task<ProfileSaveResult> SaveAsync(string modsDirectory, string name, CancellationToken cancellationToken = default);

    /// <summary>Saves a new profile with no mods, which switches every mod off when applied.</summary>
    /// <param name="modsDirectory">The Mods folder.</param>
    /// <param name="name">What to call it. Must not be blank or already taken, ignoring case.</param>
    /// <param name="cancellationToken">Cancels before the profile is written.</param>
    /// <returns>The profile.</returns>
    /// <exception cref="ProfileException">The name is blank or taken.</exception>
    /// <exception cref="ModOperationException">The profile could not be written.</exception>
    Task<ProfileSaveResult> SaveEmptyAsync(string modsDirectory, string name, CancellationToken cancellationToken = default);

    /// <summary>Saves which mods are switched on now over an existing profile.</summary>
    /// <param name="modsDirectory">The Mods folder.</param>
    /// <param name="profileId">The profile to save over.</param>
    /// <param name="cancellationToken">Cancels before the profile is written.</param>
    /// <exception cref="ProfileException">There is no such profile, or it is read-only.</exception>
    /// <exception cref="ModOperationException">The Mods folder could not be read, or the profile could not be
    /// written.</exception>
    Task<ProfileSaveResult> UpdateAsync(string modsDirectory, string profileId, CancellationToken cancellationToken = default);

    /// <summary>Gives a profile a different name.</summary>
    /// <param name="modsDirectory">The Mods folder.</param>
    /// <param name="profileId">The profile.</param>
    /// <param name="name">The new name. Must not be blank or taken by another profile.</param>
    /// <param name="cancellationToken">Cancels before the profile is written.</param>
    /// <exception cref="ProfileException">There is no such profile, it is read-only, or the name is blank or
    /// taken.</exception>
    Task<ModProfile> RenameAsync(string modsDirectory, string profileId, string name, CancellationToken cancellationToken = default);

    /// <summary>Protects a profile, or stops protecting it.</summary>
    /// <param name="modsDirectory">The Mods folder.</param>
    /// <param name="profileId">The profile.</param>
    /// <param name="isReadOnly">Whether it should be read-only.</param>
    /// <param name="cancellationToken">Cancels before the profile is written.</param>
    /// <exception cref="ProfileException">There is no such profile.</exception>
    Task<ModProfile> SetReadOnlyAsync(string modsDirectory, string profileId, bool isReadOnly, CancellationToken cancellationToken = default);

    /// <summary>Moves a profile's file to the trash.</summary>
    /// <param name="modsDirectory">The Mods folder, also the visible fallback for the trash.</param>
    /// <param name="profileId">The profile.</param>
    /// <param name="cancellationToken">Cancels before anything is moved.</param>
    /// <exception cref="ProfileException">There is no such profile, or it is read-only.</exception>
    /// <exception cref="ModOperationException">Every trash location failed.</exception>
    Task<TrashResult> DeleteAsync(string modsDirectory, string profileId, CancellationToken cancellationToken = default);

    /// <summary>Puts a deleted profile back.</summary>
    /// <param name="trashed">The record its deletion produced.</param>
    /// <param name="cancellationToken">Cancels before anything is moved.</param>
    /// <exception cref="ModOperationException">It is no longer in the trash, or a profile file is at its path again.
    /// Nothing is overwritten.</exception>
    Task RestoreAsync(TrashResult trashed, CancellationToken cancellationToken = default);

    /// <summary>Works out what applying a profile would switch. Changes nothing.</summary>
    /// <param name="modsDirectory">The Mods folder.</param>
    /// <param name="profileId">The profile.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>Every mod that would be switched on or off, and every entry naming a mod not there.</returns>
    /// <exception cref="ProfileException">There is no such profile.</exception>
    /// <exception cref="ModOperationException">The Mods folder could not be read.</exception>
    Task<ProfileApplyPlan> PlanApplyAsync(string modsDirectory, string profileId, CancellationToken cancellationToken = default);

    /// <summary>Works out what switching every mod off would switch. Changes nothing, saves nothing.</summary>
    /// <param name="modsDirectory">The Mods folder.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>A plan with <see cref="ProfileApplyPlan.IsAllOff"/> set and an empty, unsaved profile.</returns>
    /// <exception cref="ModOperationException">The Mods folder could not be read.</exception>
    Task<ProfileApplyPlan> PlanAllOffAsync(string modsDirectory, CancellationToken cancellationToken = default);

    /// <summary>Makes the switches a plan lists, as one undoable run.</summary>
    /// <param name="plan">A plan from <see cref="PlanApplyAsync"/>.</param>
    /// <param name="cancellationToken">Cancels between mods; what was switched stays switched and undoable.</param>
    /// <exception cref="ModOperationException">The switch journal could not be written.</exception>
    Task<ModSwitchRunResult> ApplyAsync(ProfileApplyPlan plan, CancellationToken cancellationToken = default);

    /// <summary>Every profile, with the mod on disk each of its entries means. Changes nothing.</summary>
    /// <param name="modsDirectory">The Mods folder.</param>
    /// <param name="inventory">A scan of it to use, or null to scan it now.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="ModOperationException">The Mods folder could not be read.</exception>
    Task<IReadOnlyList<ProfileContents>> ReadContentsAsync(
        string modsDirectory, ModsInventory? inventory = null, CancellationToken cancellationToken = default);

    /// <summary>Puts mods in a profile without switching anything; one already in it is left as it is.</summary>
    Task<ProfileEditResult> AddModsAsync(
        string modsDirectory, string profileId, IReadOnlyList<string> modFolders, CancellationToken cancellationToken = default);

    /// <summary>Puts entries back in a profile, as they were: the Undo of <see cref="RemoveEntriesAsync"/>.</summary>
    /// <param name="modsDirectory">The Mods folder.</param>
    /// <param name="profileId">The profile.</param>
    /// <param name="entries">Entries the profile once had.</param>
    /// <param name="cancellationToken">Cancels before the profile is written.</param>
    /// <exception cref="ProfileException">There is no such profile, or it is read-only.</exception>
    Task<ProfileEditResult> AddEntriesAsync(
        string modsDirectory, string profileId, IReadOnlyList<ModProfileEntry> entries, CancellationToken cancellationToken = default);

    /// <summary>Takes entries out of a profile. Switches nothing; a mod whose entry goes is not touched.</summary>
    /// <param name="modsDirectory">The Mods folder.</param>
    /// <param name="profileId">The profile.</param>
    /// <param name="entries">Entries of the profile, as <see cref="ReadContentsAsync"/> gave them.</param>
    /// <param name="cancellationToken">Cancels before the profile is written.</param>
    /// <exception cref="ProfileException">There is no such profile, or it is read-only.</exception>
    Task<ProfileEditResult> RemoveEntriesAsync(
        string modsDirectory, string profileId, IReadOnlyList<ModProfileEntry> entries, CancellationToken cancellationToken = default);

    /// <summary>Puts another mod in an entry's place, for a mod that has gone. Switches nothing.</summary>
    Task<ProfileEditResult> ReplaceEntryAsync(
        string modsDirectory, string profileId, ModProfileEntry entry, string modFolder, CancellationToken cancellationToken = default);

    /// <summary>Puts one entry back where another is: the Undo of <see cref="ReplaceEntryAsync"/>.</summary>
    /// <param name="modsDirectory">The Mods folder.</param>
    /// <param name="profileId">The profile.</param>
    /// <param name="current">The entry there now.</param>
    /// <param name="previous">The entry to put back.</param>
    /// <param name="cancellationToken">Cancels before the profile is written.</param>
    /// <exception cref="ProfileException">There is no such profile or entry, or it is read-only.</exception>
    Task<ProfileEditResult> SwapEntryAsync(
        string modsDirectory, string profileId, ModProfileEntry current, ModProfileEntry previous, CancellationToken cancellationToken = default);
}

/// <summary>A profile and the mod each of its entries means.</summary>
/// <param name="Profile">The profile.</param>
/// <param name="Members">One per entry, in the profile's order.</param>
public sealed record ProfileContents(ModProfile Profile, IReadOnlyList<ProfileMember> Members)
{
    /// <summary>Whether the profile has this mod, switched on or off.</summary>
    /// <returns>True when an entry means it.</returns>
    public bool Contains(string modFolder) =>
        Members.Any(member => member.Mod is { } mod && PathComparer.AreEqual(mod.Path, modFolder));

    /// <summary>The entries that mean any of these mods.</summary>
    public IReadOnlyList<ModProfileEntry> EntriesFor(IEnumerable<string> modFolders) =>
    [
        .. Members
            .Where(member => member.Mod is { } mod && modFolders.Any(folder => PathComparer.AreEqual(mod.Path, folder)))
            .Select(member => member.Entry),
    ];
}

/// <summary>One entry of a profile, and the mod it means.</summary>
/// <param name="Entry">The entry as the profile records it.</param>
/// <param name="Mod">The mod on disk, or null when it is not in the Mods folder any more.</param>
public sealed record ProfileMember(ModProfileEntry Entry, InstalledMod? Mod)
{
    /// <summary>Whether the mod could not be found.</summary>
    public bool IsMissing => Mod is null;
}

/// <summary>The result of adding to or taking from a profile.</summary>
/// <param name="Profile">The profile as saved.</param>
/// <param name="Changed">The entries added or taken out; empty when nothing changed.</param>
/// <param name="RecordedByFolder">Added mods whose details could not be read, recorded by folder.</param>
/// <param name="AlreadyThere">How many of the mods asked for the profile had already.</param>
public sealed record ProfileEditResult(
    ModProfile Profile, IReadOnlyList<ModProfileEntry> Changed, IReadOnlyList<string> RecordedByFolder, int AlreadyThere);

/// <summary>Every profile saved for a Mods folder.</summary>
/// <param name="Profiles">The profiles that could be read, in name order.</param>
/// <param name="Problems">Profile files that could not be read, each with why.</param>
public sealed record ProfileList(IReadOnlyList<ModProfile> Profiles, IReadOnlyList<ProfileFileProblem> Problems);

/// <summary>A profile file that could not be read.</summary>
/// <param name="Path">The file.</param>
/// <param name="Message">Why, in words that can be shown as they are.</param>
public sealed record ProfileFileProblem(string Path, string Message);

/// <summary>The result of saving a profile.</summary>
/// <param name="Profile">What was saved.</param>
/// <param name="RecordedByFolder">Switched-on mods with unreadable details, recorded by folder; renaming one loses
/// it.</param>
public sealed record ProfileSaveResult(ModProfile Profile, IReadOnlyList<string> RecordedByFolder);

/// <summary>One mod a profile would switch.</summary>
/// <param name="Mod">The mod, as it is on disk now.</param>
/// <param name="Enable">Whether applying would switch it on (otherwise off).</param>
public sealed record ProfileSwitch(InstalledMod Mod, bool Enable);

/// <summary>A mod a profile names that was found more than once; the copy at the recorded path wins.</summary>
/// <param name="Entry">The profile's entry.</param>
/// <param name="Chosen">The copy that will be switched on.</param>
/// <param name="Others">The other copies, which will be switched off.</param>
public sealed record ProfileCopy(ModProfileEntry Entry, InstalledMod Chosen, IReadOnlyList<InstalledMod> Others);

/// <summary>What applying a profile would do.</summary>
public sealed record ProfileApplyPlan
{
    /// <summary>The Mods folder.</summary>
    public required string ModsDirectory { get; init; }

    /// <summary>The profile.</summary>
    public required ModProfile Profile { get; init; }

    /// <summary>Every mod that would be switched on or off, in name order.</summary>
    public required IReadOnlyList<ProfileSwitch> Switches { get; init; }

    /// <summary>Entries whose mod is not in the Mods folder any more.</summary>
    public required IReadOnlyList<ModProfileEntry> Missing { get; init; }

    /// <summary>Entries whose mod is there more than once.</summary>
    public required IReadOnlyList<ProfileCopy> Copies { get; init; }

    /// <summary>How many mods are already as the profile has them.</summary>
    public required int UnchangedCount { get; init; }

    /// <summary>Whether this switches every mod off rather than applying a saved profile.</summary>
    public bool IsAllOff { get; init; }

    /// <summary>How many mods it would switch on.</summary>
    public int EnableCount => Switches.Count(change => change.Enable);

    /// <summary>How many mods it would switch off.</summary>
    public int DisableCount => Switches.Count(change => !change.Enable);

    /// <summary>Whether applying it would change nothing.</summary>
    public bool IsEmpty => Switches.Count == 0;
}
