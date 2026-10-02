using Serilog;
using Xxsm.Core.Io;

namespace Xxsm.Core.Mods;

/// <summary>What a leftover in a Mods folder is now.</summary>
public enum LeftoverKind
{
    /// <summary>Nothing only here: an update's copy beside its mod, or an empty working folder.</summary>
    Spare,

    /// <summary>A mod's only copy, under a working name after an interrupted update or move. Can be put back.</summary>
    OnlyCopy,

    /// <summary>A move's working folder holding something XXSM did not put there. Left to be looked at.</summary>
    Unrecognised,
}

/// <summary>A folder an interrupted update or move left in a Mods folder, and what it is now.</summary>
/// <param name="Path">The leftover folder, normalised.</param>
/// <param name="Kind">What it is now.</param>
/// <param name="Destination">For <see cref="LeftoverKind.OnlyCopy"/>, where putting it back moves the mod; otherwise
/// null.</param>
/// <param name="Description">One sentence for a person, naming the folder and the mod.</param>
public sealed record ModsFolderLeftover(string Path, LeftoverKind Kind, string? Destination, string Description);

/// <summary>A leftover moved back to the mod's own name.</summary>
/// <param name="From">The mod folder under its working name.</param>
/// <param name="To">Where it is now.</param>
public sealed record LeftoverPutBack(string From, string To);

/// <summary>A leftover that was left where it was, and why.</summary>
/// <param name="Path">The folder asked about.</param>
/// <param name="Reason">Why, in words to show as they are, including the system's own where there are any.</param>
public sealed record LeftoverProblem(string Path, string Reason);

/// <summary>What tidying some leftovers did.</summary>
/// <param name="Trashed">What went to the trash, each with the record that puts it back.</param>
/// <param name="PutBack">What was given its mod's name again.</param>
/// <param name="Problems">What was left alone, and why.</param>
public sealed record LeftoverTidyResult(
    IReadOnlyList<TrashResult> Trashed, IReadOnlyList<LeftoverPutBack> PutBack, IReadOnlyList<LeftoverProblem> Problems);

/// <summary>Tidies what an interrupted update or move left in a Mods folder, on a person's word only.</summary>
/// <remarks>Each folder is examined again when asked, since the scan that listed it may be old.</remarks>
public interface IModsFolderLeftovers
{
    /// <summary>Moves spare leftovers to the trash, as a deleted mod goes.</summary>
    /// <param name="paths">Leftover folders, as the scan reported them.</param>
    /// <param name="modsDirectory">The Mods folder they are in.</param>
    /// <param name="cancellationToken">Stops between folders.</param>
    Task<LeftoverTidyResult> TrashSpareCopiesAsync(
        IReadOnlyList<string> paths, string modsDirectory, CancellationToken cancellationToken = default);

    /// <summary>Gives each leftover that is the only copy of its mod that mod's own name again.</summary>
    /// <param name="paths">Leftover folders, as the scan reported them.</param>
    /// <param name="modsDirectory">The Mods folder they are in.</param>
    /// <param name="cancellationToken">Stops between folders.</param>
    Task<LeftoverTidyResult> PutBackAsync(
        IReadOnlyList<string> paths, string modsDirectory, CancellationToken cancellationToken = default);
}

/// <summary>The working names of updates and moves in a Mods folder, and the default tidier.</summary>
/// <remarks>
/// An update copies in as <c>DISABLED_&lt;name&gt;.xxsm-update-&lt;id&gt;</c>; a move parks a mod as
/// <c>.xxsm-move-&lt;id&gt;/&lt;name&gt;</c>. Only a name written exactly so, in capitals with a 32-digit id, is one.
/// </remarks>
public sealed class ModsFolderLeftovers(IModFileOperations files, ILogger logger) : IModsFolderLeftovers
{
    private const string UpdateMarker = ".xxsm-update-";
    private const string MovePrefix = ".xxsm-move-";
    private const int IdLength = 32;

    private readonly IModFileOperations _files = files;
    private readonly ILogger _logger = logger.ForContext<ModsFolderLeftovers>();

    /// <summary>The name an update copies a mod's new version in under, beside the mod.</summary>
    /// <param name="modFolderName">The mod folder's name as it is on disk, <c>DISABLED_</c> and all.</param>
    /// <returns>A name the game does not load and no other update can have chosen.</returns>
    public static string UpdateCopyName(string modFolderName)
    {
        ArgumentException.ThrowIfNullOrEmpty(modFolderName);

        return $"{ModsFolderLayout.DisabledPrefix}{modFolderName}{UpdateMarker}{Guid.NewGuid():n}";
    }

    /// <summary>The name of a move's working folder, which the mod is parked inside under its own name.</summary>
    /// <returns>A hidden name no other move can have chosen.</returns>
    public static string MoveStagingName() => $"{MovePrefix}{Guid.NewGuid():n}";

    /// <summary>Reads the mod's folder name back out of an update copy's name.</summary>
    /// <param name="name">A folder's own name.</param>
    /// <param name="modFolderName">The mod folder's name, when <paramref name="name"/> is an update copy's.</param>
    /// <returns>Whether it is exactly as <see cref="UpdateCopyName"/> writes one.</returns>
    public static bool TryReadUpdateCopyName(string name, out string modFolderName)
    {
        ArgumentNullException.ThrowIfNull(name);
        modFolderName = string.Empty;

        if (!name.StartsWith(ModsFolderLayout.DisabledPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var marker = name.LastIndexOf(UpdateMarker, StringComparison.Ordinal);

        if (marker <= ModsFolderLayout.DisabledPrefix.Length || !IsId(name.AsSpan(marker + UpdateMarker.Length)))
        {
            return false;
        }

        modFolderName = name[ModsFolderLayout.DisabledPrefix.Length..marker];
        return true;
    }

    /// <summary>Whether a folder's name is one an update or a move works under.</summary>
    /// <param name="name">A folder's own name.</param>
    /// <returns><c>true</c> for an update copy's or a move's working folder's name.</returns>
    public static bool IsLeftoverName(string name) =>
        TryReadUpdateCopyName(name, out _) || IsMoveStagingName(name);

    /// <summary>Looks at a folder that may be a leftover and says what it is now.</summary>
    /// <param name="path">A folder in a Mods folder or one of its character folders.</param>
    /// <returns>What it is, or null when it is not a leftover; unreadable is <see
    /// cref="LeftoverKind.Unrecognised"/>.</returns>
    public static ModsFolderLeftover? Examine(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var folder = PathComparer.Normalize(Path.TrimEndingDirectorySeparator(path));
        var name = Path.GetFileName(folder);
        var parent = Path.GetDirectoryName(folder);

        if (parent is null || !IsLeftoverName(name) || !Directory.Exists(folder) || new DirectoryInfo(folder).LinkTarget is not null)
        {
            return null;
        }

        try
        {
            return TryReadUpdateCopyName(name, out var modName)
                ? ExamineUpdateCopy(folder, parent, modName)
                : ExamineMoveStaging(folder, parent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ModsFolderLeftover(
                folder,
                LeftoverKind.Unrecognised,
                null,
                $"XXSM left '{PathDisplay.Show(folder)}' when an update or a move was cut off, and could not read it: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<LeftoverTidyResult> TrashSpareCopiesAsync(
        IReadOnlyList<string> paths, string modsDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var root = RequireModsDirectory(modsDirectory);
        var trashed = new List<TrashResult>();
        var problems = new List<LeftoverProblem>();

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Recheck(path, root) is not { } leftover)
            {
                problems.Add(NotALeftover(path));
                continue;
            }

            if (leftover.Kind != LeftoverKind.Spare)
            {
                problems.Add(new LeftoverProblem(
                    leftover.Path,
                    leftover.Kind == LeftoverKind.OnlyCopy
                        ? $"'{PathDisplay.Show(leftover.Path)}' is now the only copy of its mod, so it was not moved to the trash. Put it back instead."
                        : $"XXSM cannot tell what '{PathDisplay.Show(leftover.Path)}' holds, so it was left alone. Look inside it."));
                continue;
            }

            try
            {
                var result = await _files.DeleteAsync(leftover.Path, root, cancellationToken).ConfigureAwait(false);
                trashed.Add(result.Trash ?? throw new ModOperationException($"'{PathDisplay.Show(leftover.Path)}' was not moved to the trash.", leftover.Path));
                _logger.Information("Moved a spare leftover to the trash: {From} -> {To}", leftover.Path, result.ToPath);
            }
            catch (ModOperationException ex)
            {
                problems.Add(new LeftoverProblem(leftover.Path, ex.Message));
            }
        }

        return new LeftoverTidyResult(trashed, [], problems);
    }

    /// <inheritdoc />
    public Task<LeftoverTidyResult> PutBackAsync(
        IReadOnlyList<string> paths, string modsDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var root = RequireModsDirectory(modsDirectory);
        var putBack = new List<LeftoverPutBack>();
        var problems = new List<LeftoverProblem>();

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Recheck(path, root) is not { } leftover)
            {
                problems.Add(NotALeftover(path));
                continue;
            }

            if (leftover is not { Kind: LeftoverKind.OnlyCopy, Destination: { } destination })
            {
                problems.Add(new LeftoverProblem(
                    leftover.Path,
                    leftover.Kind == LeftoverKind.Spare
                        ? $"'{PathDisplay.Show(leftover.Path)}' is a spare copy: the mod it belongs to is still there, so it was not put back. Move it to the trash instead."
                        : $"XXSM cannot tell where what is in '{PathDisplay.Show(leftover.Path)}' belongs, so it was left alone. Look inside it."));
                continue;
            }

            if (PutBack(leftover, destination) is { } problem)
            {
                problems.Add(problem);
            }
            else
            {
                putBack.Add(new LeftoverPutBack(leftover.Path, destination));
            }
        }

        return Task.FromResult(new LeftoverTidyResult([], putBack, problems));
    }

    private static ModsFolderLeftover ExamineUpdateCopy(string folder, string parent, string modName)
    {
        var bare = ModsFolderLayout.StripDisabledPrefix(modName);

        // The mod it copies, on or off: switching it since changed only its prefix.
        foreach (var sibling in Directory.EnumerateDirectories(parent))
        {
            var siblingName = Path.GetFileName(sibling);

            if (!IsLeftoverName(siblingName) &&
                string.Equals(ModsFolderLayout.StripDisabledPrefix(siblingName), bare, StringComparison.Ordinal))
            {
                return new ModsFolderLeftover(
                    folder,
                    LeftoverKind.Spare,
                    null,
                    $"'{Path.GetFileName(folder)}' is a spare copy of '{siblingName}' that XXSM left when an update was cut off. " +
                    "The game does not load it; it only takes up space.");
            }
        }

        return new ModsFolderLeftover(
            folder,
            LeftoverKind.OnlyCopy,
            PathComparer.Normalize(Path.Combine(parent, modName)),
            $"'{Path.GetFileName(folder)}' is the only copy of '{bare}': an update was cut off after the old version went to " +
            "the trash, so the new version is waiting under this name, and the game does not load it. Put back gives it its name again.");
    }

    private static ModsFolderLeftover ExamineMoveStaging(string folder, string parent)
    {
        var entries = Directory.GetFileSystemEntries(folder);

        if (entries.Length == 0)
        {
            return new ModsFolderLeftover(
                folder, LeftoverKind.Spare, null, $"'{PathDisplay.Show(folder)}' is an empty folder XXSM left when a move was cut off.");
        }

        if (entries is [var only] && Directory.Exists(only) && new DirectoryInfo(only).LinkTarget is null)
        {
            var modName = Path.GetFileName(only);
            var beside = Path.Combine(parent, modName);

            // Cut off before the character folder was made, it goes back; after, on into that folder.
            var destination = !Path.Exists(beside) && !PathComparer.IsCaseCollision(parent, modName, out _)
                ? beside
                : Directory.Exists(beside) && !Path.Exists(Path.Combine(beside, modName)) && !PathComparer.IsCaseCollision(beside, modName, out _)
                    ? Path.Combine(beside, modName)
                    : null;

            if (destination is not null)
            {
                return new ModsFolderLeftover(
                    folder,
                    LeftoverKind.OnlyCopy,
                    PathComparer.Normalize(destination),
                    $"The mod '{modName}' was left inside '{PathDisplay.Show(folder)}' when a move was cut off, so XXSM does not show it. " +
                    $"Put back moves it to '{PathComparer.Normalize(destination)}'.");
            }
        }

        return new ModsFolderLeftover(
            folder,
            LeftoverKind.Unrecognised,
            null,
            $"XXSM left '{PathDisplay.Show(folder)}' when a move was cut off, and cannot tell where what is in it belongs. XXSM does not " +
            "show what is in it; look inside.");
    }

    private LeftoverProblem? PutBack(ModsFolderLeftover leftover, string destination)
    {
        var isMove = IsMoveStagingName(Path.GetFileName(leftover.Path));
        var source = isMove ? Path.Combine(leftover.Path, Path.GetFileName(destination)) : leftover.Path;
        var parent = Path.GetDirectoryName(destination)!;
        var name = Path.GetFileName(destination);

        if (Path.Exists(destination))
        {
            return new LeftoverProblem(
                leftover.Path, $"'{PathDisplay.Show(destination)}' exists now, so '{PathDisplay.Show(leftover.Path)}' was left where it is. XXSM will not overwrite it.");
        }

        if (PathComparer.IsCaseCollision(parent, name, out var existing))
        {
            return new LeftoverProblem(
                leftover.Path,
                $"'{name}' would sit beside '{PathDisplay.Show(existing)}', which differs only by capitals, and the game under Wine sees " +
                $"the two as one folder. '{PathDisplay.Show(leftover.Path)}' was left where it is; rename one of them first.");
        }

        try
        {
            Directory.Move(source, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LeftoverProblem(leftover.Path, $"Could not move '{PathDisplay.Show(source)}' to '{PathDisplay.Show(destination)}': {ex.Message}");
        }

        _logger.Information("Put a leftover back: {From} -> {To}", source, destination);

        if (isMove)
        {
            try
            {
                // XXSM's own working folder, empty now; a non-recursive delete refuses anything else.
                Directory.Delete(leftover.Path, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Warning(ex, "Could not remove the emptied working folder {Folder}", leftover.Path);
            }
        }

        return null;
    }

    /// <summary>Checks again that a folder to tidy is still a leftover in Mods or a character folder.</summary>
    private static ModsFolderLeftover? Recheck(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var folder = PathComparer.Normalize(Path.GetFullPath(path));
        var parent = Path.GetDirectoryName(folder);
        var grandparent = parent is null ? null : Path.GetDirectoryName(parent);
        var inside = UntrustedLocation.IsSameOrUnderExactly(root, folder) &&
                     (IsExactly(parent, root) || IsExactly(grandparent, root));

        return inside ? Examine(folder) : null;
    }

    private static bool IsExactly(string? path, string root) =>
        path is not null &&
        string.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(root), StringComparison.Ordinal);

    private static LeftoverProblem NotALeftover(string path) =>
        new(path, $"'{PathDisplay.Show(path)}' is not something XXSM left in your Mods folder, or it is gone already, so it was left alone.");

    private static string RequireModsDirectory(string modsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);

        if (!PathComparer.TryResolveExisting(modsDirectory, out var root) || !Directory.Exists(root))
        {
            throw new ModOperationException($"The Mods folder '{PathDisplay.Show(modsDirectory)}' does not exist.", modsDirectory);
        }

        return PathComparer.Normalize(Path.GetFullPath(root));
    }

    private static bool IsMoveStagingName(string name) =>
        name.StartsWith(MovePrefix, StringComparison.Ordinal) && IsId(name.AsSpan(MovePrefix.Length));

    private static bool IsId(ReadOnlySpan<char> text)
    {
        if (text.Length != IdLength)
        {
            return false;
        }

        foreach (var character in text)
        {
            if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }
}
