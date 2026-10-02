using System.Text.Json;
using Serilog;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Core.Serialization;

namespace Xxsm.Core.Profiles;

/// <summary>The default <see cref="IProfileService"/>: one JSON file per profile in <c>.xxsm/profiles/</c>.</summary>
public sealed class ProfileService(
    IModRepository repository,
    IModConfigStore configs,
    IModSwitcher switcher,
    ITrashService trash,
    TimeProvider time,
    ILogger logger) : IProfileService
{
    /// <summary>The folder inside the Mods folder's state directory that holds the profiles.</summary>
    public const string DirectoryName = "profiles";

    private const string Extension = ".json";
    private const string BackupSuffix = ".bak";
    private const int MaximumNameLength = 100;

    private readonly IModRepository _repository = repository;
    private readonly IModConfigStore _configs = configs;
    private readonly IModSwitcher _switcher = switcher;
    private readonly ITrashService _trash = trash;
    private readonly TimeProvider _time = time;
    private readonly ILogger _logger = logger.ForContext<ProfileService>();

    /// <inheritdoc />
    public string GetProfilesDirectory(string modsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);

        return PathComparer.Normalize(
            Path.Combine(modsDirectory, ModsFolderLayout.StateDirectoryName, DirectoryName));
    }

    /// <inheritdoc />
    public async Task<ProfileList> ListAsync(string modsDirectory, CancellationToken cancellationToken = default)
    {
        var directory = GetProfilesDirectory(modsDirectory);

        if (!PathComparer.TryResolveExisting(directory, out var resolved) || !Directory.Exists(resolved))
        {
            return new ProfileList([], []);
        }

        string[] files;

        try
        {
            files = Directory.GetFiles(resolved, "*" + Extension);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException(
                $"Could not list the profiles in '{PathDisplay.Show(resolved)}': {ex.Message}", resolved, ex);
        }

        var profiles = new List<ModProfile>(files.Length);
        var problems = new List<ProfileFileProblem>();

        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                profiles.Add(await ReadFileAsync(file, cancellationToken).ConfigureAwait(false));
            }
            catch (ProfileException ex)
            {
                problems.Add(new ProfileFileProblem(file, ex.Message));
                _logger.Warning(ex, "Could not read the profile {Path}", file);
            }
        }

        return new ProfileList(
            [.. profiles.OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)],
            problems);
    }

    /// <inheritdoc />
    public async Task<ProfileSaveResult> SaveAsync(
        string modsDirectory, string name, CancellationToken cancellationToken = default)
    {
        var root = Root(modsDirectory);
        var trimmed = ValidName(name);
        var existing = await ListAsync(root, cancellationToken).ConfigureAwait(false);

        RefuseTakenName(existing, trimmed, exceptId: null);

        var (entries, byFolder) = await CaptureAsync(root, cancellationToken).ConfigureAwait(false);

        return await CreateAsync(root, trimmed, entries, byFolder, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ProfileSaveResult> SaveEmptyAsync(
        string modsDirectory, string name, CancellationToken cancellationToken = default)
    {
        var root = Root(modsDirectory);
        var trimmed = ValidName(name);
        var existing = await ListAsync(root, cancellationToken).ConfigureAwait(false);

        RefuseTakenName(existing, trimmed, exceptId: null);

        return await CreateAsync(root, trimmed, [], [], cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProfileSaveResult> CreateAsync(
        string root,
        string trimmed,
        IReadOnlyList<ModProfileEntry> entries,
        IReadOnlyList<string> byFolder,
        CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();

        var profile = new ModProfile
        {
            SchemaVersion = ModProfile.CurrentSchemaVersion,
            Id = Guid.NewGuid().ToString("n")[..12],
            Name = trimmed,
            CreatedAt = now,
            UpdatedAt = now,
            Enabled = entries,
        };

        await WriteAsync(root, profile, cancellationToken).ConfigureAwait(false);

        _logger.Information(
            "Saved profile {Name} ({Id}) with {Count} switched-on mods in {ModsDirectory}",
            profile.Name,
            profile.Id,
            entries.Count,
            root);

        return new ProfileSaveResult(profile, byFolder);
    }

    /// <inheritdoc />
    public async Task<ProfileSaveResult> UpdateAsync(
        string modsDirectory, string profileId, CancellationToken cancellationToken = default)
    {
        var root = Root(modsDirectory);
        var profile = await FindAsync(root, profileId, cancellationToken).ConfigureAwait(false);

        RefuseReadOnly(profile, "saved over");

        var (entries, byFolder) = await CaptureAsync(root, cancellationToken).ConfigureAwait(false);

        var updated = profile with
        {
            SchemaVersion = ModProfile.CurrentSchemaVersion,
            UpdatedAt = _time.GetUtcNow(),
            Enabled = entries,
        };

        await WriteAsync(root, updated, cancellationToken).ConfigureAwait(false);

        _logger.Information(
            "Saved over profile {Name} ({Id}): {Before} switched-on mods before, {After} now",
            profile.Name,
            profile.Id,
            profile.Enabled.Count,
            entries.Count);

        return new ProfileSaveResult(updated, byFolder);
    }

    /// <inheritdoc />
    public async Task<ModProfile> RenameAsync(
        string modsDirectory, string profileId, string name, CancellationToken cancellationToken = default)
    {
        var root = Root(modsDirectory);
        var trimmed = ValidName(name);
        var existing = await ListAsync(root, cancellationToken).ConfigureAwait(false);
        var profile = Find(existing, profileId);

        RefuseReadOnly(profile, "renamed");
        RefuseTakenName(existing, trimmed, exceptId: profile.Id);

        var renamed = profile with { Name = trimmed };

        await WriteAsync(root, renamed, cancellationToken).ConfigureAwait(false);

        _logger.Information("Renamed profile {Id} from {Before} to {After}", profile.Id, profile.Name, trimmed);

        return renamed;
    }

    /// <inheritdoc />
    public async Task<ModProfile> SetReadOnlyAsync(
        string modsDirectory, string profileId, bool isReadOnly, CancellationToken cancellationToken = default)
    {
        var root = Root(modsDirectory);
        var profile = await FindAsync(root, profileId, cancellationToken).ConfigureAwait(false);

        if (profile.ReadOnly == isReadOnly)
        {
            return profile;
        }

        var changed = profile with { ReadOnly = isReadOnly };

        await WriteAsync(root, changed, cancellationToken).ConfigureAwait(false);

        _logger.Information(
            "Profile {Name} ({Id}) is {State} now",
            profile.Name,
            profile.Id,
            isReadOnly ? "read-only" : "editable");

        return changed;
    }

    /// <inheritdoc />
    public async Task<TrashResult> DeleteAsync(
        string modsDirectory, string profileId, CancellationToken cancellationToken = default)
    {
        var root = Root(modsDirectory);
        var profile = await FindAsync(root, profileId, cancellationToken).ConfigureAwait(false);

        RefuseReadOnly(profile, "deleted");

        var path = PathOf(root, profile.Id);
        var trashed = await _trash.TrashAsync(path, ModsFolderLayout.TrashFallbackFor(root), cancellationToken).ConfigureAwait(false);

        _logger.Information(
            "Deleted profile {Name} ({Id}): {Path} moved to {Trashed}",
            profile.Name,
            profile.Id,
            path,
            trashed.TrashedPath);

        return trashed;
    }

    /// <inheritdoc />
    public async Task RestoreAsync(TrashResult trashed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trashed);

        var restored = await _trash.RestoreAsync(trashed, cancellationToken).ConfigureAwait(false);

        _logger.Information("Restored profile {Path} from {Trashed}", restored, trashed.TrashedPath);
    }

    /// <inheritdoc />
    public async Task<ProfileApplyPlan> PlanApplyAsync(
        string modsDirectory, string profileId, CancellationToken cancellationToken = default)
    {
        var root = Root(modsDirectory);
        var profile = await FindAsync(root, profileId, cancellationToken).ConfigureAwait(false);
        var inventory = await _repository.ScanAsync(root, cancellationToken).ConfigureAwait(false);
        var mods = inventory.AllMods.ToList();
        var (_, wanted, missing, copies) = Match(root, profile.Enabled, mods);

        var switches = mods
            .Where(mod => wanted.Contains(mod.Path) != mod.IsEnabled)
            .OrderBy(mod => mod.Path, StringComparer.Ordinal)
            .Select(mod => new ProfileSwitch(mod, wanted.Contains(mod.Path)))
            .ToList();

        return new ProfileApplyPlan
        {
            ModsDirectory = root,
            Profile = profile,
            Switches = switches,
            Missing = missing,
            Copies = copies,
            UnchangedCount = mods.Count - switches.Count,
        };
    }

    /// <summary>Which mod each entry means: by id, then by path; among copies, the one at its path first.</summary>
    /// <returns>Each entry's mod or null, the mods wanted on, the entries naming nothing, and those with
    /// copies.</returns>
    private static (InstalledMod?[] Chosen, HashSet<string> Wanted, List<ModProfileEntry> Missing, List<ProfileCopy> Copies) Match(
        string root, IReadOnlyList<ModProfileEntry> entries, IReadOnlyList<InstalledMod> mods)
    {
        var byId = mods
            .Where(mod => mod.Config?.Id is { Length: > 0 })
            .GroupBy(mod => mod.Config!.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        var byPath = mods
            .GroupBy(mod => IdentityPath(root, mod), PathComparer.Instance)
            .ToDictionary(group => group.Key, group => group.ToList(), PathComparer.Instance);

        // Exact on purpose: ext4 keeps Klee/Red and klee/Red apart, and wanting one must not keep the other on.
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        var missing = new List<ModProfileEntry>();
        var copies = new List<ProfileCopy>();

        var candidatesOf = entries
            .Select(entry =>
                entry.Id is { Length: > 0 } id && byId.TryGetValue(id, out var withId) ? withId
                : byPath.TryGetValue(entry.Path, out var atPath) ? atPath
                : [])
            .ToList();

        // The switched-on copy first: choosing X and DISABLED_X the other way round plans two clashing renames.
        var chosenOf = new InstalledMod?[entries.Count];

        // Entries still at their recorded path first, so a moved copy cannot take another entry's mod.
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            chosenOf[index] = Preferred(candidatesOf[index]
                .Where(mod => !wanted.Contains(mod.Path) &&
                              PathComparer.AreEqual(IdentityPath(root, mod), entry.Path)));

            if (chosenOf[index] is { } chosen)
            {
                wanted.Add(chosen.Path);
            }
        }

        for (var index = 0; index < entries.Count; index++)
        {
            if (chosenOf[index] is not null)
            {
                continue;
            }

            var entry = entries[index];
            var candidates = candidatesOf[index];

            if (candidates.Count == 0)
            {
                missing.Add(entry);
                continue;
            }

            if (Preferred(candidates.Where(mod => !wanted.Contains(mod.Path))) is not { } chosen)
            {
                continue;
            }

            wanted.Add(chosen.Path);
            chosenOf[index] = chosen;

            if (candidates.Count > 1)
            {
                copies.Add(new ProfileCopy(entry, chosen, [.. candidates.Where(mod => !ReferenceEquals(mod, chosen))]));
            }
        }

        return (chosenOf, wanted, missing, copies);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProfileContents>> ReadContentsAsync(
        string modsDirectory, ModsInventory? inventory = null, CancellationToken cancellationToken = default)
    {
        var root = Root(modsDirectory);
        var list = await ListAsync(root, cancellationToken).ConfigureAwait(false);
        inventory ??= await _repository.ScanAsync(root, cancellationToken).ConfigureAwait(false);
        var mods = inventory.AllMods.ToList();

        return
        [
            .. list.Profiles.Select(profile =>
            {
                var chosen = Match(root, profile.Enabled, mods).Chosen;

                return new ProfileContents(
                    profile,
                    [
                        .. profile.Enabled.Select((entry, index) => new ProfileMember(
                            entry,
                            chosen[index] ?? profile.Enabled.Take(index)
                                .Select((earlier, at) => (earlier, at))
                                .Where(pair => SameEntry(pair.earlier, entry))
                                .Select(pair => chosen[pair.at])
                                .FirstOrDefault(mod => mod is not null))),
                    ]);
            }),
        ];
    }

    /// <inheritdoc />
    public async Task<ProfileEditResult> AddModsAsync(
        string modsDirectory, string profileId, IReadOnlyList<string> modFolders, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modFolders);

        var root = Root(modsDirectory);
        var profile = await FindAsync(root, profileId, cancellationToken).ConfigureAwait(false);

        RefuseReadOnly(profile, "changed");

        var inventory = await _repository.ScanAsync(root, cancellationToken).ConfigureAwait(false);
        var mods = inventory.AllMods.ToList();
        var already = Match(root, profile.Enabled, mods).Wanted;
        var added = new List<ModProfileEntry>();
        var byFolder = new List<string>();
        var alreadyThere = 0;

        foreach (var folder in modFolders.Distinct(PathComparer.Instance))
        {
            var mod = mods.FirstOrDefault(candidate => PathComparer.AreEqual(candidate.Path, folder))
                      ?? throw new ProfileException($"There is no mod at '{PathDisplay.Show(folder)}' in this Mods folder.");

            if (already.Contains(mod.Path))
            {
                alreadyThere++;
                continue;
            }

            var (entry, recordedByFolder) = await IdentifyAsync(root, mod, cancellationToken).ConfigureAwait(false);
            added.Add(entry with { AddedAt = _time.GetUtcNow() });
            already.Add(mod.Path);

            if (recordedByFolder)
            {
                byFolder.Add(mod.Path);
            }
        }

        if (added.Count == 0)
        {
            return new ProfileEditResult(profile, [], byFolder, alreadyThere);
        }

        var updated = profile with
        {
            SchemaVersion = ModProfile.CurrentSchemaVersion,
            UpdatedAt = _time.GetUtcNow(),
            Enabled = [.. profile.Enabled, .. added],
        };

        await WriteAsync(root, updated, cancellationToken).ConfigureAwait(false);

        _logger.Information(
            "Added {Count} mods to profile {Name} ({Id}): {Mods}",
            added.Count,
            profile.Name,
            profile.Id,
            added.Select(entry => entry.Path));

        return new ProfileEditResult(updated, added, byFolder, alreadyThere);
    }

    /// <inheritdoc />
    public async Task<ProfileEditResult> AddEntriesAsync(
        string modsDirectory, string profileId, IReadOnlyList<ModProfileEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var root = Root(modsDirectory);
        var profile = await FindAsync(root, profileId, cancellationToken).ConfigureAwait(false);

        RefuseReadOnly(profile, "changed");

        var added = entries.Where(entry => !profile.Enabled.Any(existing => SameEntry(existing, entry))).ToList();

        if (added.Count == 0)
        {
            return new ProfileEditResult(profile, [], [], entries.Count);
        }

        var updated = profile with
        {
            SchemaVersion = ModProfile.CurrentSchemaVersion,
            UpdatedAt = _time.GetUtcNow(),
            Enabled = [.. profile.Enabled, .. added],
        };

        await WriteAsync(root, updated, cancellationToken).ConfigureAwait(false);

        _logger.Information(
            "Put {Count} entries back in profile {Name} ({Id}): {Mods}",
            added.Count,
            profile.Name,
            profile.Id,
            added.Select(entry => entry.Path));

        return new ProfileEditResult(updated, added, [], entries.Count - added.Count);
    }

    /// <inheritdoc />
    public async Task<ProfileEditResult> RemoveEntriesAsync(
        string modsDirectory, string profileId, IReadOnlyList<ModProfileEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var root = Root(modsDirectory);
        var profile = await FindAsync(root, profileId, cancellationToken).ConfigureAwait(false);

        RefuseReadOnly(profile, "changed");

        var removed = profile.Enabled.Where(existing => entries.Any(entry => SameEntry(existing, entry))).ToList();

        if (removed.Count == 0)
        {
            return new ProfileEditResult(profile, [], [], 0);
        }

        var updated = profile with
        {
            SchemaVersion = ModProfile.CurrentSchemaVersion,
            UpdatedAt = _time.GetUtcNow(),
            Enabled = [.. profile.Enabled.Where(existing => !removed.Contains(existing))],
        };

        await WriteAsync(root, updated, cancellationToken).ConfigureAwait(false);

        _logger.Information(
            "Removed {Count} mods from profile {Name} ({Id}): {Mods}",
            removed.Count,
            profile.Name,
            profile.Id,
            removed.Select(entry => entry.Path));

        return new ProfileEditResult(updated, removed, [], 0);
    }

    /// <inheritdoc />
    public async Task<ProfileEditResult> ReplaceEntryAsync(
        string modsDirectory, string profileId, ModProfileEntry entry, string modFolder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(modFolder);

        var root = Root(modsDirectory);
        var profile = await FindAsync(root, profileId, cancellationToken).ConfigureAwait(false);

        RefuseReadOnly(profile, "changed");

        var inventory = await _repository.ScanAsync(root, cancellationToken).ConfigureAwait(false);
        var mods = inventory.AllMods.ToList();
        var mod = mods.FirstOrDefault(candidate => PathComparer.AreEqual(candidate.Path, modFolder))
                  ?? throw new ProfileException($"There is no mod at '{PathDisplay.Show(modFolder)}' in this Mods folder.");

        if (Match(root, profile.Enabled, mods).Wanted.Contains(mod.Path))
        {
            throw new ProfileException($"'{profile.Name}' has {mod.DisplayName} already.");
        }

        var (replacement, recordedByFolder) = await IdentifyAsync(root, mod, cancellationToken).ConfigureAwait(false);
        replacement = replacement with { AddedAt = _time.GetUtcNow() };

        var updated = await SwapAsync(root, profile, entry, replacement, cancellationToken).ConfigureAwait(false);

        return new ProfileEditResult(updated, [replacement], recordedByFolder ? [mod.Path] : [], 0);
    }

    /// <inheritdoc />
    public async Task<ProfileEditResult> SwapEntryAsync(
        string modsDirectory, string profileId, ModProfileEntry current, ModProfileEntry previous, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(previous);

        var root = Root(modsDirectory);
        var profile = await FindAsync(root, profileId, cancellationToken).ConfigureAwait(false);

        RefuseReadOnly(profile, "changed");

        var updated = await SwapAsync(root, profile, current, previous, cancellationToken).ConfigureAwait(false);

        return new ProfileEditResult(updated, [previous], [], 0);
    }

    /// <summary>Writes the profile with one entry in another's place, where it was.</summary>
    private async Task<ModProfile> SwapAsync(
        string root, ModProfile profile, ModProfileEntry old, ModProfileEntry replacement, CancellationToken cancellationToken)
    {
        var index = profile.Enabled.ToList().FindIndex(existing => SameEntry(existing, old));

        if (index < 0)
        {
            throw new ProfileException($"'{profile.Name}' has no entry for {old.Name ?? old.Path} any more.");
        }

        var entries = profile.Enabled.ToList();
        entries[index] = replacement;

        var updated = profile with
        {
            SchemaVersion = ModProfile.CurrentSchemaVersion,
            UpdatedAt = _time.GetUtcNow(),
            Enabled = entries,
        };

        await WriteAsync(root, updated, cancellationToken).ConfigureAwait(false);

        _logger.Information(
            "Replaced {Old} with {New} in profile {Name} ({Id})",
            old.Path,
            replacement.Path,
            profile.Name,
            profile.Id);

        return updated;
    }

    /// <summary>Whether two entries are one: the same id, or both without one and at the same path.</summary>
    private static bool SameEntry(ModProfileEntry a, ModProfileEntry b) =>
        a.Id is { Length: > 0 } || b.Id is { Length: > 0 }
            ? string.Equals(a.Id, b.Id, StringComparison.Ordinal) && PathComparer.AreEqual(a.Path, b.Path)
            : PathComparer.AreEqual(a.Path, b.Path);

    /// <summary>The switched-on copy first, then the first by path, whatever the disk order.</summary>
    private static InstalledMod? Preferred(IEnumerable<InstalledMod> candidates) =>
        candidates
            .OrderByDescending(mod => mod.IsEnabled)
            .ThenBy(mod => mod.Path, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <inheritdoc />
    public Task<ModSwitchRunResult> ApplyAsync(ProfileApplyPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return _switcher.ApplyAsync(
            plan.ModsDirectory,
            [.. plan.Switches.Select(change => new ModSwitch(change.Mod.Path, change.Enable))],
            plan.IsAllOff ? ModSwitchSource.AllOff : ModSwitchSource.Profile,
            plan.IsAllOff ? null : plan.Profile.Name,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ProfileApplyPlan> PlanAllOffAsync(string modsDirectory, CancellationToken cancellationToken = default)
    {
        var root = Root(modsDirectory);
        var inventory = await _repository.ScanAsync(root, cancellationToken).ConfigureAwait(false);
        var mods = inventory.AllMods.ToList();

        var switches = mods
            .Where(mod => mod.IsEnabled)
            .OrderBy(mod => mod.Path, StringComparer.Ordinal)
            .Select(mod => new ProfileSwitch(mod, false))
            .ToList();

        return new ProfileApplyPlan
        {
            ModsDirectory = root,
            Profile = new ModProfile(),
            Switches = switches,
            Missing = [],
            Copies = [],
            UnchangedCount = mods.Count - switches.Count,
            IsAllOff = true,
        };
    }

    private async Task<(IReadOnlyList<ModProfileEntry> Entries, IReadOnlyList<string> ByFolder)> CaptureAsync(
        string root, CancellationToken cancellationToken)
    {
        var inventory = await _repository.ScanAsync(root, cancellationToken).ConfigureAwait(false);
        var entries = new List<ModProfileEntry>();
        var byFolder = new List<string>();

        foreach (var mod in inventory.AllMods.Where(mod => mod.IsEnabled).OrderBy(mod => mod.Path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (entry, recordedByFolder) = await IdentifyAsync(root, mod, cancellationToken).ConfigureAwait(false);
            entries.Add(entry);

            if (recordedByFolder)
            {
                byFolder.Add(mod.Path);
            }
        }

        return (entries, byFolder);
    }

    /// <summary>A profile entry for a mod, giving the mod an id first if it has none.</summary>
    private async Task<(ModProfileEntry Entry, bool RecordedByFolder)> IdentifyAsync(
        string root, InstalledMod mod, CancellationToken cancellationToken)
    {
        var id = mod.Config?.Id is { Length: > 0 } known ? known : null;

        if (id is null && mod.ConfigError is null)
        {
            try
            {
                // Minting the id writes the mod's .xxsm/mod.json, so a rename or move keeps it in the profile.
                id = (await _configs.UpdateAsync(mod.Path, config => config, cancellationToken)
                    .ConfigureAwait(false)).Id;
            }
            catch (ModOperationException ex)
            {
                _logger.Warning(ex, "Could not give {Mod} an id; the profile records its folder", mod.Path);
            }
        }

        var entry = new ModProfileEntry
        {
            Id = id,
            Path = IdentityPath(root, mod),
            Name = mod.DisplayName,
        };

        return (entry, id is null);
    }

    /// <summary>A mod's path relative to the Mods folder, without its disabled prefix.</summary>
    private static string IdentityPath(string root, InstalledMod mod)
    {
        var parent = Path.GetDirectoryName(mod.Path) ?? root;
        var enabledPath = Path.Combine(parent, mod.Name);

        return PathComparer.TryGetRelativePath(root, enabledPath) ?? PathComparer.Normalize(enabledPath);
    }

    private async Task<ModProfile> FindAsync(string root, string profileId, CancellationToken cancellationToken) =>
        Find(await ListAsync(root, cancellationToken).ConfigureAwait(false), profileId);

    private static ModProfile Find(ProfileList list, string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        return list.Profiles.FirstOrDefault(profile => string.Equals(profile.Id, profileId, StringComparison.Ordinal))
               ?? list.Profiles.FirstOrDefault(profile => string.Equals(profile.Name, profileId, StringComparison.CurrentCultureIgnoreCase))
               ?? throw new ProfileException($"There is no profile '{profileId}' for this Mods folder.");
    }

    private static void RefuseReadOnly(ModProfile profile, string action)
    {
        if (profile.ReadOnly)
        {
            throw new ProfileException(
                $"'{profile.Name}' is read-only, so it cannot be {action}. Make it editable first.");
        }
    }

    private static void RefuseTakenName(ProfileList existing, string name, string? exceptId)
    {
        var taken = existing.Profiles.FirstOrDefault(profile =>
            !string.Equals(profile.Id, exceptId, StringComparison.Ordinal) &&
            string.Equals(profile.Name, name, StringComparison.CurrentCultureIgnoreCase));

        if (taken is not null)
        {
            throw new ProfileException($"There is already a profile called '{taken.Name}'.");
        }
    }

    private static string ValidName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            throw new ProfileException("A profile needs a name.");
        }

        if (trimmed.Length > MaximumNameLength)
        {
            throw new ProfileException($"A profile's name can be at most {MaximumNameLength} characters long.");
        }

        if (trimmed.Any(char.IsControl))
        {
            throw new ProfileException("A profile's name cannot contain line breaks or tabs.");
        }

        return trimmed;
    }

    private static string Root(string modsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);

        if (!PathComparer.TryResolveExisting(modsDirectory, out var resolved) || !Directory.Exists(resolved))
        {
            throw new ModOperationException(
                $"The Mods folder '{PathDisplay.Show(modsDirectory)}' does not exist.", modsDirectory);
        }

        return PathComparer.Normalize(resolved);
    }

    private string PathOf(string root, string id) =>
        PathComparer.Normalize(Path.Combine(GetProfilesDirectory(root), id + Extension));

    private static async Task<ModProfile> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        string json;

        try
        {
            json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProfileException($"Could not read the profile '{PathDisplay.Show(path)}': {ex.Message}", ex);
        }

        ModProfile? profile;

        try
        {
            profile = JsonSerializer.Deserialize(json, CoreJsonContext.Default.ModProfile);
        }
        catch (JsonException ex)
        {
            throw new ProfileException(
                $"'{PathDisplay.Show(path)}' is not a readable profile: {ex.Message}. It has been left as it is; " +
                $"a backup of the previous version may be at '{PathDisplay.Show(path)}{BackupSuffix}'.",
                ex);
        }

        if (profile is null || profile.Id.Length == 0 || profile.Name.Length == 0)
        {
            throw new ProfileException($"'{PathDisplay.Show(path)}' is not a profile: it has no id or no name. It has been left as it is.");
        }

        if (profile.SchemaVersion > ModProfile.CurrentSchemaVersion)
        {
            throw new ProfileException(
                $"'{PathDisplay.Show(path)}' was saved by a newer XXSM (format {profile.SchemaVersion}). " +
                "Update XXSM to use it; it has been left as it is.");
        }

        var fileId = Path.GetFileNameWithoutExtension(path);

        if (!string.Equals(profile.Id, fileId, StringComparison.Ordinal))
        {
            throw new ProfileException(
                $"'{PathDisplay.Show(path)}' says its id is '{profile.Id}', but it is named '{fileId}'. " +
                "Rename the file to match, or change the id; it has been left as it is.");
        }

        return profile;
    }

    private async Task WriteAsync(string root, ModProfile profile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = PathOf(root, profile.Id);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var json = JsonSerializer.Serialize(profile, CoreJsonContext.Default.ModProfile);

            await AtomicFile.WriteAllTextAsync(
                    path, json, new AtomicWriteOptions { BackupPath = path + BackupSuffix, Durable = true }, cancellationToken)
                .ConfigureAwait(false);

            _logger.Information("Wrote profile {Name} to {Path}", profile.Name, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException(
                $"Could not save the profile to '{PathDisplay.Show(path)}': {ex.Message}. The previous version has not been changed.",
                path,
                ex);
        }
    }
}
