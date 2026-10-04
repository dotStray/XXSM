using Serilog;
using Xxsm.Core.Io;

namespace Xxsm.Core.Mods;

/// <summary>The default <see cref="IModFileOperations"/>. Every change is logged with both paths.</summary>
public sealed class ModFileOperations(ITrashService trash, IVolumeResolver volumes, ILogger logger) : IModFileOperations
{
    private readonly ITrashService _trash = trash;
    private readonly IVolumeResolver _volumes = volumes;
    private readonly ILogger _logger = logger.ForContext<ModFileOperations>();

    /// <inheritdoc />
    public Task<ModOperationResult> SetEnabledAsync(
        string modFolder, bool enabled, CancellationToken cancellationToken = default)
    {
        var source = RequireDirectory(modFolder, "mod folder");
        cancellationToken.ThrowIfCancellationRequested();

        var currentName = Path.GetFileName(source);
        var parent = Path.GetDirectoryName(source)
                     ?? throw new ModOperationException(
                         $"'{PathDisplay.Show(source)}' has no parent folder, so it cannot be a mod.", source);

        var targetName = enabled
            ? ModsFolderLayout.StripDisabledPrefix(currentName)
            : ModsFolderLayout.AddDisabledPrefix(currentName);

        var kind = enabled ? ModOperationKind.Enable : ModOperationKind.Disable;

        if (string.Equals(currentName, targetName, StringComparison.Ordinal))
        {
            return Task.FromResult(new ModOperationResult
            {
                Kind = kind,
                FromPath = source,
                ToPath = source,
                Changed = false,
                Method = ModMoveMethod.None,
            });
        }

        var target = PathComparer.Normalize(Path.Combine(parent, targetName));

        // Refused, not numbered: X beside DISABLED_X is two mods.
        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new ModOperationException(
                $"Cannot {(enabled ? "enable" : "disable")} '{currentName}': " +
                $"'{targetName}' already exists in '{PathDisplay.Show(parent)}'. " +
                "Rename or remove one of them first — XXSM will not overwrite it.",
                target);
        }

        if (PathComparer.IsCaseCollision(parent, targetName, out var existing))
        {
            throw new ModOperationException(
                $"Cannot {(enabled ? "enable" : "disable")} '{currentName}': that would create " +
                $"'{targetName}' beside the existing '{PathDisplay.Show(existing)}', which differ only by " +
                "capitalisation. 3DMigoto under Wine would see them as one folder. " +
                "Rename one of them first.",
                target);
        }

        try
        {
            Directory.Move(source, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Never falls back to copy-then-delete: a half-copied mod is worse than a failed rename.
            throw new ModOperationException(
                $"Could not {(enabled ? "enable" : "disable")} '{currentName}': {ex.Message}",
                source,
                ex);
        }

        _logger.Information(
            "{Operation} mod: {From} -> {To}", kind, source, target);

        return Task.FromResult(new ModOperationResult
        {
            Kind = kind,
            FromPath = source,
            ToPath = target,
            Changed = true,
            Method = ModMoveMethod.Rename,
        });
    }

    /// <inheritdoc />
    public async Task<ModOperationResult> MoveAsync(
        string modFolder,
        string destinationParent,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        var source = RequireDirectory(modFolder, "mod folder");
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationParent);
        cancellationToken.ThrowIfCancellationRequested();

        var currentName = Path.GetFileName(source);

        // A folder keeps the name it already has; only a new name is checked.
        var folderName = string.IsNullOrWhiteSpace(name) || string.Equals(name, currentName, StringComparison.Ordinal)
            ? currentName
            : RequireUsableName(name, source);
        var parent = EnsureParentDirectory(destinationParent);

        // The same folder on disk, not the same spelling: ext4 keeps ganyu and Ganyu apart.
        if (PathComparer.AreSameOnDisk(Path.GetDirectoryName(source), parent) &&
            string.Equals(folderName, currentName, StringComparison.Ordinal))
        {
            return new ModOperationResult
            {
                Kind = ModOperationKind.Move,
                FromPath = source,
                ToPath = source,
                Changed = false,
                Method = ModMoveMethod.None,
            };
        }

        // A mod folder named like its character folder: the character folder is built around it.
        if (PathComparer.AreSameOnDisk(source, parent))
        {
            return MoveBeneathItself(source, folderName);
        }

        // The reverse, as an undo does: moving up onto the character folder built around it.
        var wanted = PathComparer.Normalize(Path.Combine(parent, folderName));

        if (PathComparer.AreSameOnDisk(Path.GetDirectoryName(source), wanted))
        {
            return MoveOntoItsOwnParent(source, wanted, folderName);
        }

        var (target, disambiguatedFrom) = ResolveFreeName(parent, folderName);

        if (PathComparer.AreEqual(_volumes.GetVolumeRoot(source), _volumes.GetVolumeRoot(parent)))
        {
            try
            {
                Directory.Move(source, target);

                _logger.Information("Moved mod: {From} -> {To}", source, target);

                return new ModOperationResult
                {
                    Kind = ModOperationKind.Move,
                    FromPath = source,
                    ToPath = target,
                    Changed = true,
                    Method = ModMoveMethod.Rename,
                    DisambiguatedFromName = disambiguatedFrom,
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ModOperationException(
                    $"Could not move '{folderName}' into '{PathDisplay.Show(parent)}': {ex.Message}",
                    source,
                    ex);
            }
        }

        // Across filesystems: copy, verify, and only then trash the original; never leave both.
        await CopyDirectoryAsync(source, target, cancellationToken).ConfigureAwait(false);
        VerifyOrRemoveCopy(source, target);

        TrashResult trashed;

        try
        {
            // No fallback folder: one beside the original, in a character folder, would still be loaded.
            trashed = await _trash.TrashAsync(source, fallbackRoot: null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ModOperationException or OperationCanceledException)
        {
            TryRemovePartialCopy(target);

            if (ex is OperationCanceledException)
            {
                throw;
            }

            throw new ModOperationException(
                $"'{folderName}' was copied to '{PathDisplay.Show(parent)}', but the original could not be moved to the " +
                $"trash: {ex.Message} The copy was removed again so the mod is not there twice; the " +
                "original is where it was.",
                source,
                ex);
        }

        _logger.Information(
            "Moved mod across filesystems: {From} -> {To}, original trashed to {Trashed}",
            source,
            target,
            trashed.TrashedPath);

        return new ModOperationResult
        {
            Kind = ModOperationKind.Move,
            FromPath = source,
            ToPath = target,
            Changed = true,
            Method = ModMoveMethod.CopyVerifyTrash,
            DisambiguatedFromName = disambiguatedFrom,
            Trash = trashed,
        };
    }

    /// <inheritdoc />
    public Task<ModOperationResult> RenameAsync(
        string modFolder, string newName, CancellationToken cancellationToken = default)
    {
        var source = RequireDirectory(modFolder, "mod folder");
        var currentName = Path.GetFileName(source);
        var parent = ParentOf(source);
        var targetName = TargetName(source, currentName, newName);

        cancellationToken.ThrowIfCancellationRequested();

        if (string.Equals(currentName, targetName, StringComparison.Ordinal))
        {
            return Task.FromResult(new ModOperationResult
            {
                Kind = ModOperationKind.Rename,
                FromPath = source,
                ToPath = source,
                Changed = false,
                Method = ModMoveMethod.None,
            });
        }

        // A taken name stops here: the caller proposes a free one and asks first.
        if (FindSiblingCollision(parent, currentName, targetName) is { } existing)
        {
            var free = ModNames.Propose(targetName, candidate =>
                FindSiblingCollision(parent, currentName, candidate));

            throw new ModOperationException(
                $"Cannot rename '{ModsFolderLayout.StripDisabledPrefix(currentName)}' to " +
                $"'{ModsFolderLayout.StripDisabledPrefix(targetName)}': " +
                $"'{ModsFolderLayout.StripDisabledPrefix(existing)}' is already here. " +
                $"'{ModsFolderLayout.StripDisabledPrefix(free.Name)}' is free.",
                PathComparer.Join(parent, targetName));
        }

        var target = PathComparer.Normalize(Path.Combine(parent, targetName));

        try
        {
            Directory.Move(source, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException(
                $"Could not rename '{currentName}' to '{targetName}': {ex.Message}",
                source,
                ex);
        }

        _logger.Information("Renamed mod: {From} -> {To}", source, target);

        return Task.FromResult(new ModOperationResult
        {
            Kind = ModOperationKind.Rename,
            FromPath = source,
            ToPath = target,
            Changed = true,
            Method = ModMoveMethod.Rename,
        });
    }

    /// <inheritdoc />
    public Task<ModNameProposal> ProposeFolderNameAsync(
        string modFolder, string newName, CancellationToken cancellationToken = default)
    {
        var source = RequireDirectory(modFolder, "mod folder");
        var currentName = Path.GetFileName(source);
        var parent = ParentOf(source);
        var targetName = TargetName(source, currentName, newName);

        cancellationToken.ThrowIfCancellationRequested();

        var proposal = ModNames.Propose(
            targetName, candidate => FindSiblingCollision(parent, currentName, candidate));

        return Task.FromResult(proposal with
        {
            Wanted = ModsFolderLayout.StripDisabledPrefix(proposal.Wanted),
            Name = ModsFolderLayout.StripDisabledPrefix(proposal.Name),
            CollidesWith = proposal.CollidesWith is { } collision
                ? ModsFolderLayout.StripDisabledPrefix(collision)
                : null,
        });
    }

    /// <summary>The folder name for a typed name, keeping the mod's enabled state as it is.</summary>
    private static string TargetName(string source, string currentName, string newName)
    {
        ArgumentNullException.ThrowIfNull(newName);

        var bare = ModsFolderLayout.StripDisabledPrefix(newName.Trim());

        if (ModsFolderLayout.DescribeUnusableFolderName(bare) is { } problem)
        {
            throw new ModOperationException(problem, source);
        }

        return ModsFolderLayout.IsDisabled(currentName)
            ? ModsFolderLayout.AddDisabledPrefix(bare)
            : bare;
    }

    /// <summary>The sibling a candidate name would clash with, ignoring the mod itself and <c>DISABLED_</c>.</summary>
    private string? FindSiblingCollision(string parent, string ownName, string candidate)
    {
        if (string.Equals(ownName, candidate, StringComparison.Ordinal))
        {
            return null;
        }

        var wanted = ModsFolderLayout.StripDisabledPrefix(candidate);

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(parent))
            {
                var name = Path.GetFileName(entry);

                if (name.Length == 0 || string.Equals(name, ownName, StringComparison.Ordinal))
                {
                    continue;
                }

                // Ignoring case: ganyu beside Ganyu is one folder to Wine.
                if (PathComparer.AreNamesEqual(ModsFolderLayout.StripDisabledPrefix(name), wanted))
                {
                    return name;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(
                ex, "Could not list {Parent} to check whether {Name} is free", parent, candidate);
        }

        return null;
    }

    private static string ParentOf(string modFolder) =>
        Path.GetDirectoryName(modFolder)
        ?? throw new ModOperationException(
            $"'{PathDisplay.Show(modFolder)}' has no parent folder, so it cannot be a mod.", modFolder);

    /// <summary>Checks a folder name XXSM is about to create; not for a name a moved folder already has.</summary>
    private static string RequireUsableName(string name, string source)
    {
        var bare = ModsFolderLayout.StripDisabledPrefix(name.Trim()).Trim();

        if (ModsFolderLayout.DescribeUnusableFolderName(bare) is { } problem)
        {
            throw new ModOperationException(problem, source);
        }

        return name;
    }

    /// <inheritdoc />
    public async Task<ModOperationResult> InstallAsync(
        string sourceFolder,
        string destinationParent,
        string? name = null,
        bool keepExistingName = false,
        CancellationToken cancellationToken = default)
    {
        var source = RequireDirectory(sourceFolder, "folder to install");
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationParent);
        cancellationToken.ThrowIfCancellationRequested();

        // Checked before the destination is made, so a refused install leaves no empty folder behind.
        var requested = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(source) : name;
        var wanted = keepExistingName ? requested : RequireUsableName(requested, source);

        var parent = EnsureParentDirectory(destinationParent);

        if (PathComparer.IsSameOrUnder(source, parent))
        {
            throw new ModOperationException(
                $"Cannot install '{PathDisplay.Show(source)}' into '{PathDisplay.Show(parent)}': the destination is inside the " +
                "folder being installed, which would copy for ever.",
                source);
        }

        var (target, disambiguatedFrom) = ResolveFreeName(parent, wanted);

        await CopyDirectoryAsync(source, target, cancellationToken).ConfigureAwait(false);
        VerifyOrRemoveCopy(source, target);

        _logger.Information("Installed mod: {From} -> {To}", source, target);

        return new ModOperationResult
        {
            Kind = ModOperationKind.Install,
            FromPath = source,
            ToPath = target,
            Changed = true,
            Method = ModMoveMethod.Copy,
            DisambiguatedFromName = disambiguatedFrom,
        };
    }

    /// <inheritdoc />
    public async Task<ModOperationResult> DeleteAsync(
        string modFolder, string modsDirectory, CancellationToken cancellationToken = default)
    {
        var source = RequireDirectory(modFolder, "mod folder");
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);

        var trashed = await _trash.TrashAsync(source, ModsFolderLayout.TrashFallbackFor(modsDirectory), cancellationToken).ConfigureAwait(false);

        _logger.Information(
            "Deleted mod: {From} -> {To} (via {Method})",
            source,
            trashed.TrashedPath,
            trashed.Method);

        return new ModOperationResult
        {
            Kind = ModOperationKind.Delete,
            FromPath = source,
            ToPath = trashed.TrashedPath,
            Changed = true,
            Method = ModMoveMethod.Rename,
            Trash = trashed,
        };
    }

    /// <inheritdoc />
    public async Task<ModOperationResult> DeleteEmptyCharacterFolderAsync(
        string characterFolder, string modsDirectory, CancellationToken cancellationToken = default)
    {
        var source = RequireDirectory(characterFolder, "character folder");
        var root = RequireDirectory(modsDirectory, "Mods folder");
        var name = Path.GetFileName(source);

        if (!PathComparer.AreEqual(ParentOf(source), root))
        {
            throw new ModOperationException(
                $"'{PathDisplay.Show(source)}' is not a character folder directly inside your Mods folder, so it was left alone.",
                source);
        }

        if (ModsFolderLayout.LooksLikeModFolder(source))
        {
            throw new ModOperationException(
                $"'{name}' is a mod rather than a character folder, so it was left alone. Delete it as a mod instead.",
                source);
        }

        // Asked of the disk now: a mod may have been installed here since the scan.
        string? firstMod;

        try
        {
            firstMod = Directory.EnumerateDirectories(source)
                .Select(child => Path.GetFileName(child))
                .FirstOrDefault(child => !ModsFolderLayout.IsReservedEntry(child));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException($"Could not look inside '{name}': {ex.Message}", source, ex);
        }

        if (firstMod is not null)
        {
            throw new ModOperationException(
                $"'{name}' has a mod in it now ('{ModsFolderLayout.StripDisabledPrefix(firstMod)}'), " +
                "so it was left alone.",
                source);
        }

        var trashed = await _trash.TrashAsync(source, ModsFolderLayout.TrashFallbackFor(root), cancellationToken).ConfigureAwait(false);

        _logger.Information(
            "Deleted empty character folder: {From} -> {To} (via {Method})",
            source,
            trashed.TrashedPath,
            trashed.Method);

        return new ModOperationResult
        {
            Kind = ModOperationKind.Delete,
            FromPath = source,
            ToPath = trashed.TrashedPath,
            Changed = true,
            Method = ModMoveMethod.Rename,
            Trash = trashed,
        };
    }

    /// <inheritdoc />
    public Task<TrashMoveOutResult> MoveTrashOutAsync(
        string trashFolder, string modsDirectory, CancellationToken cancellationToken = default)
    {
        var root = RequireDirectory(modsDirectory, "Mods folder");
        var source = RequireDirectory(trashFolder, "trash folder");

        if (!string.Equals(Path.GetFileName(source), FreedesktopTrashService.FallbackDirectoryName, StringComparison.Ordinal)
            || !UntrustedLocation.IsSameOrUnderExactly(root, source)
            || string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(source), StringComparison.Ordinal))
        {
            throw new ModOperationException(
                $"'{PathDisplay.Show(source)}' is not an XXSM trash folder inside your Mods folder, so it was left alone.", source);
        }

        var beside = ModsFolderLayout.TrashFallbackFor(root)
            ?? throw new ModOperationException(
                $"Your Mods folder '{PathDisplay.Show(root)}' is at the top of its disk, so there is no folder beside it to move " +
                "the trash to. It was left alone.",
                source);
        var destination = PathComparer.Normalize(Path.Combine(beside, FreedesktopTrashService.FallbackDirectoryName));
        var items = CountTrashItems(source);

        if (!Path.Exists(destination))
        {
            try
            {
                Directory.Move(source, destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ModOperationException(
                    $"Could not move '{PathDisplay.Show(source)}' to '{PathDisplay.Show(destination)}': {ex.Message}", source, ex);
            }

            _logger.Information("Moved the trash out of the Mods folder: {From} -> {To}", source, destination);

            return Task.FromResult(new TrashMoveOutResult(source, destination, items, []));
        }

        var moved = 0;
        var problems = new List<string>();
        var sourceFiles = Path.Combine(source, "files");
        var sourceInfo = Path.Combine(source, "info");
        var targetFiles = Path.Combine(destination, "files");
        var targetInfo = Path.Combine(destination, "info");

        try
        {
            Directory.CreateDirectory(targetFiles);
            Directory.CreateDirectory(targetInfo);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException(
                $"Could not make the trash folder '{PathDisplay.Show(destination)}': {ex.Message}", destination, ex);
        }

        foreach (var item in EntriesOf(sourceFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (MoveTrashItem(item, sourceInfo, targetFiles, targetInfo) is { } problem)
            {
                problems.Add(problem);
            }
            else
            {
                moved++;
            }
        }

        // XXSM's own bookkeeping folders, and only once empty: a non-recursive delete refuses otherwise.
        foreach (var folder in new[] { sourceFiles, sourceInfo, source })
        {
            try
            {
                if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                {
                    Directory.Delete(folder);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Warning(ex, "Could not remove the emptied trash folder {Folder}", folder);
            }
        }

        _logger.Information(
            "Moved the trash out of the Mods folder: {From} -> {To}, {Moved} items, {Problems} left",
            source,
            destination,
            moved,
            problems.Count);

        return Task.FromResult(new TrashMoveOutResult(source, destination, moved, problems));
    }

    /// <summary>Moves one trashed item and its record into another trash; returns why it could not, or null.</summary>
    private string? MoveTrashItem(string item, string sourceInfo, string targetFiles, string targetInfo)
    {
        const string InfoExtension = ".trashinfo";
        var name = Path.GetFileName(item);
        var record = Path.Combine(sourceInfo, name + InfoExtension);
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);

        for (var attempt = 0; attempt < 10_000; attempt++)
        {
            var candidate = attempt == 0
                ? name
                : $"{stem}_{attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)}{extension}";
            var to = Path.Combine(targetFiles, candidate);
            var toRecord = Path.Combine(targetInfo, candidate + InfoExtension);

            if (Path.Exists(to) || Path.Exists(toRecord))
            {
                continue;
            }

            try
            {
                if (File.Exists(record))
                {
                    // Claims the name the way the trash does: a record already there refuses.
                    File.Move(record, toRecord, overwrite: false);
                }

                try
                {
                    if (Directory.Exists(item))
                    {
                        Directory.Move(item, to);
                    }
                    else
                    {
                        File.Move(item, to, overwrite: false);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (File.Exists(toRecord))
                    {
                        File.Move(toRecord, record, overwrite: false);
                    }

                    return $"'{name}' could not be moved: {ex.Message}";
                }

                _logger.Information("Moved a deleted item out of the Mods folder: {From} -> {To}", item, to);
                return null;
            }
            catch (IOException) when (Path.Exists(toRecord))
            {
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"'{name}' could not be moved: {ex.Message}";
            }
        }

        return $"'{name}' could not be moved: no free name for it in '{PathDisplay.Show(targetFiles)}'.";
    }

    private static int CountTrashItems(string trash) => EntriesOf(Path.Combine(trash, "files")).Count;

    private static List<string> EntriesOf(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? [.. Directory.EnumerateFileSystemEntries(folder)] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException($"Could not look inside '{PathDisplay.Show(folder)}': {ex.Message}", folder, ex);
        }
    }

    /// <inheritdoc />
    public async Task<ModOperationResult> RestoreAsync(
        TrashResult trashed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trashed);

        var restored = await _trash.RestoreAsync(trashed, cancellationToken).ConfigureAwait(false);

        _logger.Information("Restored mod: {From} -> {To}", trashed.TrashedPath, restored);

        return new ModOperationResult
        {
            Kind = ModOperationKind.Restore,
            FromPath = trashed.TrashedPath,
            ToPath = restored,
            Changed = true,
            Method = ModMoveMethod.Rename,
            Trash = trashed,
        };
    }

    /// <summary>Moves a mod into a character folder of its own name, which one rename cannot do.</summary>
    /// <remarks>Parked in a dot-named working folder between two renames; a failed second rename undoes the
    /// first.</remarks>
    private ModOperationResult MoveBeneathItself(string source, string folderName)
    {
        var grandparent = Path.GetDirectoryName(source)
                          ?? throw new ModOperationException(
                              $"'{PathDisplay.Show(source)}' has no parent folder, so it cannot be a mod.", source);

        var staging = PathComparer.Normalize(Path.Combine(grandparent, ModsFolderLeftovers.MoveStagingName()));
        var parked = Path.Combine(staging, Path.GetFileName(source));

        try
        {
            Directory.CreateDirectory(staging);
            Directory.Move(source, parked);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RemoveStaging(staging);

            throw new ModOperationException(
                $"Could not move '{folderName}' aside to make room for the character folder " +
                $"of the same name: {ex.Message}",
                source,
                ex);
        }

        var target = PathComparer.Normalize(Path.Combine(source, folderName));

        try
        {
            Directory.CreateDirectory(source);
            Directory.Move(parked, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RestoreFromStaging(parked, source);
            RemoveStaging(staging);

            throw new ModOperationException(
                $"Could not move '{folderName}' into '{PathDisplay.Show(source)}': {ex.Message}",
                source,
                ex);
        }

        RemoveStaging(staging);

        _logger.Information("Moved mod: {From} -> {To}", source, target);

        return new ModOperationResult
        {
            Kind = ModOperationKind.Move,
            FromPath = source,
            ToPath = target,
            Changed = true,
            Method = ModMoveMethod.Rename,
        };
    }

    /// <summary>Moves a mod up onto the character folder built around it, only when nothing else is in it.</summary>
    private ModOperationResult MoveOntoItsOwnParent(string source, string wanted, string folderName)
    {
        var siblings = Directory.GetFileSystemEntries(wanted);

        if (siblings.Length != 1)
        {
            throw new ModOperationException(
                $"Cannot move '{folderName}' out onto '{PathDisplay.Show(wanted)}': that folder also holds " +
                $"{siblings.Length - 1} other item(s), which would be lost. Move them out first.",
                source);
        }

        var grandparent = Path.GetDirectoryName(wanted)
                          ?? throw new ModOperationException(
                              $"'{PathDisplay.Show(wanted)}' has no parent folder.", source);

        var staging = PathComparer.Normalize(Path.Combine(grandparent, ModsFolderLeftovers.MoveStagingName()));
        var parked = Path.Combine(staging, Path.GetFileName(source));

        try
        {
            Directory.CreateDirectory(staging);
            Directory.Move(source, parked);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RemoveStaging(staging);

            throw new ModOperationException(
                $"Could not move '{folderName}' out of '{PathDisplay.Show(wanted)}': {ex.Message}",
                source,
                ex);
        }

        try
        {
            // Non-recursive: it was just emptied, and anything still inside must stay.
            Directory.Delete(wanted, recursive: false);
            Directory.Move(parked, wanted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RestoreFromStaging(parked, source);
            RemoveStaging(staging);

            throw new ModOperationException(
                $"Could not put '{folderName}' back at '{PathDisplay.Show(wanted)}': {ex.Message}",
                source,
                ex);
        }

        RemoveStaging(staging);
        _logger.Information("Moved mod: {From} -> {To}", source, wanted);

        return new ModOperationResult
        {
            Kind = ModOperationKind.Move,
            FromPath = source,
            ToPath = wanted,
            Changed = true,
            Method = ModMoveMethod.Rename,
        };
    }

    /// <summary>Removes a move's working folder once the mod is out of it; a folder still holding it stays.</summary>
    private void RemoveStaging(string staging)
    {
        try
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(ex, "Could not remove the move's working folder {Folder}", staging);
        }
    }

    /// <summary>Puts a staged mod back after a failed second rename. Best effort, during an exception.</summary>
    private void RestoreFromStaging(string staging, string source)
    {
        try
        {
            if (Directory.Exists(source) && Directory.GetFileSystemEntries(source).Length == 0)
            {
                Directory.Delete(source, recursive: false);
            }

            if (Directory.Exists(staging))
            {
                // The failure may have removed the mod's own parent.
                var parent = Path.GetDirectoryName(source);

                if (parent is { Length: > 0 })
                {
                    Directory.CreateDirectory(parent);
                }

                Directory.Move(staging, source);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error(
                ex,
                "Could not put {Staging} back to {Source} after a failed move. The mod is " +
                "intact but is sitting under a hidden staging name",
                staging,
                source);
        }
    }

    /// <summary>A free name in the parent, adding <c> (2)</c>, <c> (3)</c> … when the wanted one is taken.</summary>
    /// <returns>The full target path, and the name given up, or null when the wanted name was free.</returns>
    private static (string Target, string? DisambiguatedFrom) ResolveFreeName(string parent, string wanted)
    {
        var proposal = ModNames.Propose(wanted, candidate => FindCollision(parent, candidate));
        var target = PathComparer.Normalize(Path.Combine(parent, proposal.Name));

        return (target, proposal.IsFree ? null : proposal.Wanted);
    }

    /// <summary>The name in the parent a candidate clashes with, exactly or by case; null when free.</summary>
    private static string? FindCollision(string parent, string candidate)
    {
        var path = PathComparer.Normalize(Path.Combine(parent, candidate));

        if (Directory.Exists(path) || File.Exists(path))
        {
            return candidate;
        }

        return PathComparer.IsCaseCollision(parent, candidate, out var existing) ? existing : null;
    }

    private static string RequireDirectory(string path, string what)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!PathComparer.TryResolveExisting(path, out var resolved) || !Directory.Exists(resolved))
        {
            throw new ModOperationException($"The {what} '{PathDisplay.Show(path)}' does not exist.", path);
        }

        return PathComparer.Normalize(resolved);
    }

    /// <summary>Creates the destination character folder, refusing one that clashes with a sibling by case.</summary>
    private static string EnsureParentDirectory(string destinationParent)
    {
        if (PathComparer.TryResolveExisting(destinationParent, out var existing) &&
            Directory.Exists(existing))
        {
            return PathComparer.Normalize(existing);
        }

        var normalized = PathComparer.Normalize(destinationParent);
        var name = Path.GetFileName(normalized);
        var grandparent = Path.GetDirectoryName(normalized);

        if (grandparent is { Length: > 0 } &&
            PathComparer.IsCaseCollision(grandparent, name, out var collidesWith))
        {
            throw new ModOperationException(
                $"Cannot create '{name}': '{collidesWith}' already exists beside it and the " +
                "two differ only by capitalisation. 3DMigoto under Wine would see them as " +
                "one folder. Rename the existing one first.",
                normalized);
        }

        try
        {
            Directory.CreateDirectory(normalized);
            return normalized;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException(
                $"Could not create the folder '{PathDisplay.Show(normalized)}': {ex.Message}", normalized, ex);
        }
    }

    private async Task CopyDirectoryAsync(
        string source, string target, CancellationToken cancellationToken)
    {
        try
        {
            await DirectoryCopy.CopyAsync(source, target, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // XXSM's own partial output, not user content.
            TryRemovePartialCopy(target);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryRemovePartialCopy(target);

            throw new ModOperationException(
                $"Could not copy '{PathDisplay.Show(source)}' to '{PathDisplay.Show(target)}': {ex.Message}. " +
                "Nothing has been removed from the original.",
                source,
                ex);
        }
    }

    /// <summary>Checks a copy XXSM just made, and removes it when it does not match.</summary>
    private void VerifyOrRemoveCopy(string source, string target)
    {
        try
        {
            VerifyCopy(source, target);
        }
        catch (ModOperationException)
        {
            TryRemovePartialCopy(target);
            throw;
        }
    }

    /// <summary>Checks a copy against its original: <see cref="DirectoryCopy.Verify"/>.</summary>
    internal static void VerifyCopy(string source, string target) => DirectoryCopy.Verify(source, target);

    /// <summary>Removes a failed copy: XXSM's own output at a path that was free, never user content.</summary>
    private void TryRemovePartialCopy(string target)
    {
        try
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logged, not rethrown: the real error is the one to report.
            _logger.Warning(ex, "Could not clean up the partial copy at {Path}", target);
        }
    }
}
