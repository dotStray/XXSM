using Serilog;
using Xxsm.Core.Diagnostics;
using Xxsm.Core.Io;

namespace Xxsm.Core.Archives;

/// <summary>One self-contained mod found inside an archive.</summary>
/// <param name="RelativePath">Where it sits inside the archive, with <c>/</c> separators; empty when the root is the
/// mod.</param>
/// <param name="Path">Its absolute path in the staging directory.</param>
/// <param name="Name">The name to offer: the folder's own, or the archive's when the archive root is the mod.</param>
/// <param name="FileCount">How many files are in it, at any depth.</param>
/// <param name="Bytes">How many bytes they come to.</param>
public sealed record ArchiveModRoot(
    string RelativePath,
    string Path,
    string Name,
    int FileCount,
    long Bytes)
{
    /// <summary>Whether this is the archive's own root rather than a folder inside it.</summary>
    public bool IsArchiveRoot => RelativePath.Length == 0;
}

/// <summary>An archive unpacked into a temporary directory, and what is in it. Disposing deletes it.</summary>
public sealed class ExtractedArchive : IDisposable
{
    private readonly ILogger _logger;
    private bool _disposed;

    internal ExtractedArchive(
        string archivePath,
        string archiveName,
        string stagingDirectory,
        IReadOnlyList<ArchiveModRoot> modRoots,
        IReadOnlyList<string> strandedFiles,
        int entryCount,
        long bytes,
        ArchiveLimit limitsReached,
        IReadOnlyList<Diagnostic> diagnostics,
        ILogger logger)
    {
        ArchivePath = archivePath;
        ArchiveName = archiveName;
        StagingDirectory = stagingDirectory;
        ModRoots = modRoots;
        StrandedFiles = strandedFiles;
        EntryCount = entryCount;
        Bytes = bytes;
        LimitsReached = limitsReached;
        Diagnostics = diagnostics;
        _logger = logger;
    }

    /// <summary>The archive that was read.</summary>
    public string ArchivePath { get; }

    /// <summary>The archive's file name without its extension; the sorter's archive-name signal.</summary>
    public string ArchiveName { get; }

    /// <summary>The temporary directory the archive was unpacked into.</summary>
    public string StagingDirectory { get; }

    /// <summary>Every self-contained mod in it, outermost first, then alphabetically. May be empty.</summary>
    public IReadOnlyList<ArchiveModRoot> ModRoots { get; }

    /// <summary>Files that belong to no mod, relative to <see cref="StagingDirectory"/>.</summary>
    public IReadOnlyList<string> StrandedFiles { get; }

    /// <summary>How many entries were written.</summary>
    public int EntryCount { get; }

    /// <summary>How many bytes were written.</summary>
    public long Bytes { get; }

    /// <summary>Which bounds stopped the extraction short, if any.</summary>
    public ArchiveLimit LimitsReached { get; }

    /// <summary>Anything odd noticed on the way. Never fatal.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>Whether anything installable was found.</summary>
    public bool HasMods => ModRoots.Count > 0;

    /// <summary>Whether the whole archive was read.</summary>
    public bool IsComplete => LimitsReached == ArchiveLimit.None;

    /// <summary>Deletes the staging directory.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        OwnScratch.TryDeleteFolder(StagingDirectory, _logger);

        GC.SuppressFinalize(this);
    }

    /// <summary>Finds a root by the path it sits at inside the archive.</summary>
    /// <param name="relativePath">The path, as <see cref="ArchiveModRoot.RelativePath"/> spells it.</param>
    /// <returns>The root, or null.</returns>
    public ArchiveModRoot? Find(string? relativePath) =>
        relativePath is null
            ? null
            : ModRoots.FirstOrDefault(root => PathComparer.AreEqual(root.RelativePath, relativePath));
}
