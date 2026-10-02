using System.Globalization;
using System.Text;
using Serilog;

namespace Xxsm.Core.Io;

/// <summary>An <see cref="ITrashService"/> by the freedesktop.org Trash specification, with a fallback.</summary>
/// <remarks>
/// On the home disk: the home trash, then the fallback folder. On another disk: that disk's volume trash, then the
/// fallback folder, then the home trash. The <c>.trashinfo</c> record is created exclusively before the item moves.
/// </remarks>
public sealed class FreedesktopTrashService(
    IAppPaths paths,
    IVolumeResolver volumes,
    IUserIdProvider userIds,
    TimeProvider clock,
    ILogger logger) : ITrashService
{
    private const string FilesSubdirectory = "files";
    private const string InfoSubdirectory = "info";
    private const string InfoExtension = ".trashinfo";
    /// <summary>The name of the fallback folder: <c>.xxsm-trash</c>.</summary>
    public const string FallbackDirectoryName = ".xxsm-trash";

    /// <summary>The most suffixed names tried before giving up on a colliding filename.</summary>
    private const int MaxNameAttempts = 10_000;

    private readonly IAppPaths _paths = paths;
    private readonly IVolumeResolver _volumes = volumes;
    private readonly IUserIdProvider _userIds = userIds;
    private readonly TimeProvider _clock = clock;
    private readonly ILogger _logger = logger.ForContext<FreedesktopTrashService>();

    /// <inheritdoc />
    public async Task<TrashResult> TrashAsync(
        string path,
        string? fallbackRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (fallbackRoot is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fallbackRoot);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var source = PathComparer.TryResolveExisting(path, out var resolved)
            ? resolved
            : PathComparer.Normalize(Path.GetFullPath(path));

        var isDirectory = Directory.Exists(source);
        if (!isDirectory && !File.Exists(source))
        {
            throw new ModOperationException(
                $"Cannot move '{PathDisplay.Show(source)}' to the trash because it does not exist.",
                source);
        }

        var deletedAt = _clock.GetLocalNow();
        var failures = new List<string>();

        foreach (var candidate in EnumerateTrashRoots(source, fallbackRoot, failures))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await TryTrashIntoAsync(source, isDirectory, candidate, deletedAt, failures, cancellationToken)
                .ConfigureAwait(false);

            if (result is not null)
            {
                _logger.Information(
                    "Trashed {Method} {Source} -> {Destination} (info {InfoFile})",
                    result.Method,
                    result.OriginalPath,
                    result.TrashedPath,
                    result.InfoFilePath);

                return result;
            }
        }

        throw new ModOperationException(
            $"Could not move '{PathDisplay.Show(source)}' to the trash. Nothing was deleted. Reasons tried: " +
            string.Join("; ", failures),
            source);
    }

    /// <inheritdoc />
    public Task<string> RestoreAsync(TrashResult trashed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trashed);
        cancellationToken.ThrowIfCancellationRequested();

        var from = trashed.TrashedPath;
        var to = trashed.OriginalPath;
        var isDirectory = Directory.Exists(from);

        if (!isDirectory && !File.Exists(from))
        {
            throw new ModOperationException(
                $"'{Path.GetFileName(to)}' is no longer in the trash at '{from}'. It may have been " +
                "restored already, or the trash emptied.",
                from);
        }

        if (Path.Exists(to))
        {
            throw new ModOperationException(
                $"Something is already at '{PathDisplay.Show(to)}', so '{Path.GetFileName(to)}' was left in the trash " +
                "rather than put over it. Move or rename what is there, then try again.",
                to);
        }

        // A name differing only by case is in the way too: under Wine the game sees one.
        if (Path.GetDirectoryName(to) is { Length: > 0 } folder
            && Directory.Exists(folder)
            && PathComparer.IsCaseCollision(folder, Path.GetFileName(to), out var existing))
        {
            throw new ModOperationException(
                $"'{PathDisplay.Show(existing)}' is already in '{PathDisplay.Show(folder)}', and differs from '{Path.GetFileName(to)}' only by capitals, " +
                "which the game under Wine sees as one folder. It was left in the trash; rename what is there, then try again.",
                to);
        }

        try
        {
            if (Path.GetDirectoryName(to) is { Length: > 0 } parent)
            {
                Directory.CreateDirectory(parent);
            }

            if (isDirectory)
            {
                Directory.Move(from, to);
            }
            else
            {
                File.Move(from, to, overwrite: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException(
                $"Could not put '{Path.GetFileName(to)}' back from the trash: {ex.Message}", to, ex);
        }

        OwnScratch.TryDeleteFile(trashed.InfoFilePath, null);

        _logger.Information("Restored {Trashed} -> {Original} (info {InfoFile})", from, to, trashed.InfoFilePath);

        return Task.FromResult(PathComparer.Normalize(to));
    }

    /// <inheritdoc />
    public async Task<TrashResult> ReadRecordAsync(string infoFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(infoFilePath);

        var info = PathComparer.Normalize(Path.GetFullPath(infoFilePath));

        if (!info.EndsWith(InfoExtension, StringComparison.Ordinal))
        {
            throw new ModOperationException(
                $"'{PathDisplay.Show(info)}' is not a trash record. Give the .trashinfo file that deleting the mod reported.",
                info);
        }

        if (!File.Exists(info))
        {
            throw new ModOperationException(
                $"There is no trash record at '{PathDisplay.Show(info)}'. The mod may have been put back already, or the trash emptied.",
                info);
        }

        string text;

        try
        {
            text = await File.ReadAllTextAsync(info, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException($"Could not read the trash record '{PathDisplay.Show(info)}': {ex.Message}", info, ex);
        }

        string? recorded = null;
        string? deleted = null;

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("Path=", StringComparison.Ordinal))
            {
                recorded = Uri.UnescapeDataString(line["Path=".Length..]);
            }
            else if (line.StartsWith("DeletionDate=", StringComparison.Ordinal))
            {
                deleted = line["DeletionDate=".Length..];
            }
        }

        var infoDirectory = Path.GetDirectoryName(info)!;
        var root = PathComparer.Normalize(Path.GetDirectoryName(infoDirectory)!);
        var rootName = Path.GetFileName(root);
        var parentName = Path.GetFileName(Path.GetDirectoryName(root) ?? string.Empty);

        if (recorded is not { Length: > 0 })
        {
            throw new ModOperationException($"The trash record '{PathDisplay.Show(info)}' does not say where the item came from.", info);
        }

        string original;

        if (recorded.StartsWith('/'))
        {
            original = recorded;
        }
        else
        {
            var top = rootName.StartsWith(".Trash-", StringComparison.Ordinal)
                ? Path.GetDirectoryName(root)
                : string.Equals(parentName, ".Trash", StringComparison.Ordinal)
                    ? Path.GetDirectoryName(Path.GetDirectoryName(root))
                    : null;

            original = top is { Length: > 0 }
                ? Path.Combine(top, recorded)
                : throw new ModOperationException(
                    $"The trash record '{PathDisplay.Show(info)}' gives a relative path, and '{PathDisplay.Show(root)}' is not a volume trash " +
                    "it could be relative to.",
                    info);
        }

        var method = PathComparer.AreEqual(root, _paths.HomeTrashDirectory)
            ? TrashMethod.HomeTrash
            : string.Equals(rootName, FallbackDirectoryName, StringComparison.Ordinal)
                ? TrashMethod.FallbackFolder
                : TrashMethod.VolumeTrash;

        var at = DateTime.TryParseExact(
            deleted, "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? new DateTimeOffset(local, _clock.LocalTimeZone.GetUtcOffset(local))
            : _clock.GetLocalNow();

        var name = Path.GetFileName(info)[..^InfoExtension.Length];

        return new TrashResult(
            PathComparer.Normalize(original),
            PathComparer.Normalize(Path.Combine(root, FilesSubdirectory, name)),
            info,
            method,
            at);
    }

    /// <summary>The trash roots to try, in order, probed lazily.</summary>
    private IEnumerable<TrashRoot> EnumerateTrashRoots(string source, string? fallbackRoot, List<string> failures)
    {
        var homeTrash = _paths.HomeTrashDirectory;
        var sourceVolume = _volumes.GetVolumeRoot(source);
        var homeVolume = _volumes.GetVolumeRoot(homeTrash);

        // Exact comparison on purpose: mount points differing by case are two disks.
        var onHomeVolume = string.Equals(sourceVolume, homeVolume, StringComparison.Ordinal);

        TrashRoot? fallback = fallbackRoot is null
            ? null
            : new TrashRoot(
                PathComparer.Normalize(Path.Combine(Path.GetFullPath(fallbackRoot), FallbackDirectoryName)),
                TrashMethod.FallbackFolder,
                TopDirectory: null);

        if (onHomeVolume)
        {
            yield return new TrashRoot(homeTrash, TrashMethod.HomeTrash, TopDirectory: null);

            if (fallback is { } besideHome)
            {
                yield return besideHome;
            }

            yield break;
        }

        var volumeTrash = ResolveVolumeTrash(sourceVolume, failures);
        if (volumeTrash is not null)
        {
            yield return new TrashRoot(volumeTrash, TrashMethod.VolumeTrash, sourceVolume);
        }

        if (fallback is { } besideItem)
        {
            yield return besideItem;
        }

        yield return new TrashRoot(homeTrash, TrashMethod.HomeTrash, TopDirectory: null, AcrossDisks: true);
    }

    /// <summary>The volume trash for a mount point, as the specification picks it.</summary>
    private string? ResolveVolumeTrash(string volumeRoot, List<string> failures)
    {
        var uid = _userIds.TryGetUserId();
        if (uid is null)
        {
            failures.Add("volume trash unavailable: the current user id could not be determined");
            return null;
        }

        var shared = Path.Combine(volumeRoot, ".Trash");
        if (Directory.Exists(shared) && IsUsableSharedTrash(shared))
        {
            return PathComparer.Normalize(Path.Combine(shared, uid.Value.ToString(CultureInfo.InvariantCulture)));
        }

        var own = PathComparer.Normalize(
            Path.Combine(volumeRoot, $".Trash-{uid.Value.ToString(CultureInfo.InvariantCulture)}"));

        // A .Trash-uid that is a link is refused, not followed.
        if (new DirectoryInfo(own).LinkTarget is not null)
        {
            failures.Add($"volume trash at '{PathDisplay.Show(own)}' is a link, which the trash specification says to refuse");
            return null;
        }

        return own;
    }

    private static bool IsUsableSharedTrash(string sharedTrash)
    {
        try
        {
            var info = new DirectoryInfo(sharedTrash);

            // A linked or non-sticky .Trash is refused, as the specification requires.
            if (info.LinkTarget is not null)
            {
                return false;
            }

            if (OperatingSystem.IsWindows())
            {
                return false;
            }

            return File.GetUnixFileMode(sharedTrash).HasFlag(UnixFileMode.StickyBit);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Trashes into one root; null, with the reason recorded, when this root cannot be used.</summary>
    private static async Task<TrashResult?> TryTrashIntoAsync(
        string source,
        bool isDirectory,
        TrashRoot root,
        DateTimeOffset deletedAt,
        List<string> failures,
        CancellationToken cancellationToken)
    {
        var filesDirectory = Path.Combine(root.Path, FilesSubdirectory);
        var infoDirectory = Path.Combine(root.Path, InfoSubdirectory);

        try
        {
            // A trash is readable by its owner alone.
            if (!Directory.Exists(root.Path) && !OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(root.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            Directory.CreateDirectory(filesDirectory);
            Directory.CreateDirectory(infoDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add($"{root.Method} at '{PathDisplay.Show(root.Path)}': {ex.Message}");
            return null;
        }

        var claim = TryClaimName(source, filesDirectory, infoDirectory, failures, root);
        if (claim is null)
        {
            return null;
        }

        var (destination, infoPath, infoStream) = claim.Value;

        Exception? writeFailure = null;
        try
        {
            await WriteInfoAsync(infoStream, source, root, deletedAt).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            writeFailure = ex;
        }
        finally
        {
            await infoStream.DisposeAsync().ConfigureAwait(false);
        }

        if (writeFailure is not null)
        {
            OwnScratch.TryDeleteFile(infoPath, null);
            failures.Add(
                $"{root.Method} at '{PathDisplay.Show(root.Path)}': could not write the trash record: {writeFailure.Message}");
            return null;
        }

        try
        {
            if (isDirectory)
            {
                await MoveDirectoryAsync(source, destination, root.AcrossDisks, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                File.Move(source, destination, overwrite: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // Nothing was deleted: release the claimed name.
            OwnScratch.TryDeleteFile(infoPath, null);

            if (ex is OperationCanceledException)
            {
                throw;
            }

            failures.Add($"{root.Method} at '{PathDisplay.Show(root.Path)}': {ex.Message}");
            return null;
        }

        return new TrashResult(
            source,
            PathComparer.Normalize(destination),
            PathComparer.Normalize(infoPath),
            root.Method,
            deletedAt);
    }

    /// <summary>Moves a folder into a trash: by rename, or across disks by a checked copy, then removal.</summary>
    /// <exception cref="IOException">Nothing was removed: the rename failed, or the copy did and is gone
    /// again.</exception>
    /// <exception cref="ModOperationException">The whole folder is in the trash, but some of the original could not be
    /// removed.</exception>
    private static async Task MoveDirectoryAsync(
        string source, string destination, bool acrossDisks, CancellationToken cancellationToken)
    {
        try
        {
            Directory.Move(source, destination);
            return;
        }
        catch (IOException) when (acrossDisks && Directory.Exists(source) && !Path.Exists(destination))
        {
        }

        try
        {
            await DirectoryCopy.CopyAsync(source, destination, cancellationToken).ConfigureAwait(false);
            DirectoryCopy.Verify(source, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ModOperationException or OperationCanceledException)
        {
            RemoveOwnCopy(destination);

            if (ex is OperationCanceledException)
            {
                throw;
            }

            throw new IOException($"could not copy it across disks ({ex.Message}); the original is untouched", ex);
        }

        try
        {
            // Removed only once a checked copy of all of it is in the trash.
            Directory.Delete(source, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException(
                $"'{Path.GetFileName(source)}' was copied whole to the trash at '{PathDisplay.Show(destination)}', but some of " +
                $"it could not be removed from '{PathDisplay.Show(source)}': {ex.Message} What is left there is still where it " +
                "was; remove it once you have looked.",
                source,
                ex);
        }
    }

    /// <summary>Removes a copy this service made moments ago and could not finish: its own output.</summary>
    private static void RemoveOwnCopy(string destination)
    {
        try
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Claims a free name by creating its <c>.trashinfo</c> exclusively.</summary>
    private static (string Destination, string InfoPath, FileStream InfoStream)? TryClaimName(
        string source,
        string filesDirectory,
        string infoDirectory,
        List<string> failures,
        TrashRoot root)
    {
        var baseName = Path.GetFileName(source.TrimEnd('/'));
        if (string.IsNullOrEmpty(baseName))
        {
            failures.Add($"{root.Method} at '{PathDisplay.Show(root.Path)}': '{PathDisplay.Show(source)}' has no file name");
            return null;
        }

        var stem = Path.GetFileNameWithoutExtension(baseName);
        var extension = Path.GetExtension(baseName);

        for (var attempt = 0; attempt < MaxNameAttempts; attempt++)
        {
            var candidate = attempt == 0
                ? baseName
                : $"{stem}_{attempt.ToString(CultureInfo.InvariantCulture)}{extension}";

            var destination = Path.Combine(filesDirectory, candidate);
            var infoPath = Path.Combine(infoDirectory, candidate + InfoExtension);

            // Both halves must be free, or the move would overwrite a stale entry.
            if (Path.Exists(destination))
            {
                continue;
            }

            try
            {
                var stream = new FileStream(
                    infoPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None);

                return (destination, infoPath, stream);
            }
            catch (IOException) when (File.Exists(infoPath))
            {
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{root.Method} at '{PathDisplay.Show(root.Path)}': {ex.Message}");
                return null;
            }
        }

        failures.Add(
            $"{root.Method} at '{PathDisplay.Show(root.Path)}': no free name for '{baseName}' after " +
            $"{MaxNameAttempts.ToString(CultureInfo.InvariantCulture)} attempts");
        return null;
    }

    private static async Task WriteInfoAsync(
        FileStream stream,
        string source,
        TrashRoot root,
        DateTimeOffset deletedAt)
    {
        var recordedPath = root.TopDirectory is { } top
            ? PathComparer.TryGetRelativePath(top, source) ?? source
            : source;

        var builder = new StringBuilder()
            .Append("[Trash Info]\n")
            .Append("Path=").Append(EncodePath(recordedPath)).Append('\n')
            .Append("DeletionDate=")
            .Append(deletedAt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture))
            .Append('\n');

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        await stream.WriteAsync(bytes).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>Percent-encodes a path per segment, as the trash specification requires.</summary>
    internal static string EncodePath(string path)
    {
        var rooted = path.StartsWith('/');
        var encoded = string.Join(
            '/',
            path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

        return rooted ? "/" + encoded : encoded;
    }

    private readonly record struct TrashRoot(string Path, TrashMethod Method, string? TopDirectory, bool AcrossDisks = false);
}
