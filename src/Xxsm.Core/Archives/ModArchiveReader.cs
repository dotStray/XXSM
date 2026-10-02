using Serilog;
using SharpCompress.Archives;
using SharpCompress.Common;
using Xxsm.Core.Diagnostics;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;

namespace Xxsm.Core.Archives;

/// <summary>The default <see cref="IModArchiveReader"/>, over SharpCompress.</summary>
public sealed class ModArchiveReader(IAppPaths paths, ILogger logger) : IModArchiveReader
{
    private readonly IAppPaths _paths = paths;
    private readonly ILogger _logger = logger.ForContext<ModArchiveReader>();

    /// <summary>The folder under the cache an archive is unpacked into for installing.</summary>
    public const string StagingFolderName = "install";

    /// <inheritdoc />
    public bool IsSupportedArchive(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && IModArchiveReader.SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<ExtractedArchive> ExtractAsync(
        string archivePath,
        ArchiveBounds? bounds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        cancellationToken.ThrowIfCancellationRequested();

        var limits = bounds ?? ArchiveBounds.Default;

        var resolved = PathComparer.TryResolveExisting(archivePath, out var found)
            ? found
            : PathComparer.Normalize(Path.GetFullPath(archivePath));

        if (Directory.Exists(resolved))
        {
            throw new ModOperationException(
                $"'{PathDisplay.Show(resolved)}' is a folder, not an archive. Install it directly instead.", resolved);
        }

        if (!File.Exists(resolved))
        {
            throw new ModOperationException($"There is no file at '{PathDisplay.Show(resolved)}'.", resolved);
        }

        var staging = PathComparer.Normalize(Path.Combine(
            _paths.CacheDirectory, StagingFolderName, Guid.NewGuid().ToString("N")));

        Directory.CreateDirectory(staging);

        var diagnostics = new List<Diagnostic>();

        try
        {
            var (entries, bytes, reached) = await Task
                .Run(() => Unpack(resolved, staging, limits, diagnostics, cancellationToken), cancellationToken)
                .ConfigureAwait(false);

            var roots = FindModRoots(staging, diagnostics);
            var stranded = FindStrandedFiles(staging, roots, diagnostics);

            var archive = new ExtractedArchive(
                resolved,
                ArchiveNameOf(resolved),
                staging,
                roots,
                stranded,
                entries,
                bytes,
                reached,
                diagnostics,
                _logger);

            _logger.Information(
                "Unpacked {Archive} to {Staging}: {Entries} entries, {Bytes} bytes, " +
                "{Mods} mods found, {Stranded} files belonging to none",
                resolved,
                staging,
                entries,
                bytes,
                roots.Count,
                stranded.Count);

            return archive;
        }
        catch
        {
            OwnScratch.TryDeleteFolder(staging, _logger);
            throw;
        }
    }

    /// <summary>Unpacks into the staging directory by <see cref="ArchiveUnpacker"/>'s rules.</summary>
    private static (int Entries, long Bytes, ArchiveLimit Reached) Unpack(
        string archivePath,
        string staging,
        ArchiveBounds bounds,
        List<Diagnostic> diagnostics,
        CancellationToken cancellationToken) =>
        ArchiveUnpacker.Unpack(archivePath, staging, bounds, diagnostics, cancellationToken);

    /// <summary>Every self-contained mod in the staging tree, outermost first; not searched inside a match.</summary>
    private static List<ArchiveModRoot> FindModRoots(string staging, List<Diagnostic> diagnostics)
    {
        var roots = new List<ArchiveModRoot>();

        if (IsModRoot(staging))
        {
            roots.Add(Root(staging, staging, diagnostics));
            return roots;
        }

        Descend(staging);

        return roots;

        void Descend(string directory)
        {
            foreach (var child in Directories(directory, diagnostics))
            {
                var name = Path.GetFileName(child);

                if (ModsFolderLayout.IsReservedEntry(name) || name.StartsWith('.') || name.StartsWith("__"))
                {
                    continue;
                }

                if (IsModRoot(child))
                {
                    roots.Add(Root(staging, child, diagnostics));
                    continue;
                }

                Descend(child);
            }
        }
    }

    /// <summary>Whether an unpacked directory is a mod worth offering: mod-shaped, with mod content.</summary>
    private static bool IsModRoot(string directory) =>
        ModsFolderLayout.LooksLikeModFolder(directory) && ModsFolderLayout.ContainsModContent(directory);

    private static ArchiveModRoot Root(string staging, string path, List<Diagnostic> diagnostics)
    {
        var relative = PathComparer.AreEqual(staging, path)
            ? string.Empty
            : PathComparer.Normalize(Path.GetRelativePath(staging, path));

        var (files, bytes) = Measure(path, diagnostics);

        return new ArchiveModRoot(
            relative,
            PathComparer.Normalize(path),
            relative.Length == 0 ? string.Empty : Path.GetFileName(path),
            files,
            bytes);
    }

    /// <summary>Files inside the staging tree that no mod root covers.</summary>
    private static List<string> FindStrandedFiles(
        string staging, List<ArchiveModRoot> roots, List<Diagnostic> diagnostics)
    {
        if (roots.Exists(root => root.IsArchiveRoot))
        {
            return [];
        }

        var stranded = new List<string>();

        foreach (var file in Files(staging, diagnostics))
        {
            if (!roots.Exists(root => PathComparer.IsSameOrUnder(root.Path, file)))
            {
                stranded.Add(PathComparer.Normalize(Path.GetRelativePath(staging, file)));
            }
        }

        stranded.Sort(PathComparer.Instance);
        return stranded;
    }

    private static (int Files, long Bytes) Measure(string directory, List<Diagnostic> diagnostics)
    {
        var files = 0;
        var bytes = 0L;

        foreach (var file in Files(directory, diagnostics))
        {
            files++;

            try
            {
                bytes += new FileInfo(file).Length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A size that cannot be read counts as nothing rather than stopping the summary.
                bytes += 0;
            }
        }

        return (files, bytes);
    }

    private static IEnumerable<string> Files(string directory, List<Diagnostic> diagnostics)
    {
        try
        {
            return FileTree.Files(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Unreadable(directory, exception));
            return [];
        }
    }

    private static List<string> Directories(string directory, List<Diagnostic> diagnostics)
    {
        try
        {
            return Directory.EnumerateDirectories(directory).Order(PathComparer.Instance).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Unreadable(directory, exception));
            return [];
        }
    }

    private static Diagnostic Unreadable(string directory, Exception exception) => new(
        DiagnosticSeverity.Warning,
        ArchiveDiagnosticCodes.UnreadableEntry,
        $"Could not list '{PathDisplay.Show(directory)}': {exception.Message}");

    /// <summary>The archive's name without its extension, or two: <c>CoolMod.tar.gz</c> gives <c>CoolMod</c>.</summary>
    private static string ArchiveNameOf(string archivePath)
    {
        var name = Path.GetFileNameWithoutExtension(archivePath);

        return Path.GetExtension(name) is { Length: > 1 } inner
               && IModArchiveReader.SupportedExtensions.Contains(inner, StringComparer.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(name)
            : name;
    }
}

/// <summary>Stable codes for problems found unpacking an archive.</summary>
public static class ArchiveDiagnosticCodes
{
    /// <summary>An entry could not be unpacked, or a directory could not be listed.</summary>
    public const string UnreadableEntry = "archive.entry.unreadable";

    /// <summary>An <see cref="ArchiveBounds"/> limit stopped the extraction short.</summary>
    public const string LimitReached = "archive.limit";

    /// <summary>An entry was left out: a link, a repeated or case-twin name, or an impossible file name.</summary>
    public const string SkippedEntry = "archive.entry.skipped";
}
