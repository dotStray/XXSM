using Serilog;
using Xxsm.Core;
using Xxsm.Core.Archives;
using Xxsm.Core.GameBanana;
using Xxsm.Core.Ini;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Packs.Downloads;

namespace Xxsm.Packs.GameBanana;

/// <summary>What an update does to one file of a mod.</summary>
public enum ModUpdateChangeKind
{
    /// <summary>The new version has it and the installed one does not.</summary>
    Added,

    /// <summary>Both have it, and its contents differ.</summary>
    Replaced,

    /// <summary>The installed version has it and the new one does not.</summary>
    Removed,
}

/// <summary>One file an update adds, replaces or removes.</summary>
/// <param name="RelativePath">Its path inside the mod, with <c>/</c> separators.</param>
/// <param name="Kind">What happens to it.</param>
/// <param name="MayBeEdited">For a replaced <c>.ini</c>: whether it changed after XXSM added the mod.</param>
public sealed record ModUpdateFileChange(string RelativePath, ModUpdateChangeKind Kind, bool MayBeEdited = false);

/// <summary>The page an update would come from, read with nothing downloaded, so a file can be chosen.</summary>
public sealed class ModUpdateSource
{
    internal ModUpdateSource(string modFolder, ModConfig config, GameBananaMod page)
    {
        ModFolder = modFolder;
        Config = config;
        Page = page;
    }

    /// <summary>The mod being updated.</summary>
    public string ModFolder { get; }

    /// <summary>The page, as read just now.</summary>
    public GameBananaMod Page { get; }

    /// <summary>The version recorded for what is installed, when one was.</summary>
    public string? InstalledVersion => Config.Version;

    /// <summary>The page's file the mod was installed from, or null when unrecorded or gone.</summary>
    public GameBananaFile? InstalledFile => Config.GameBanana?.FileId is { } id
        ? Page.Files.FirstOrDefault(file => file.IdRow == id)
        : null;

    /// <summary>The file taken without asking: the one asked for, else the mod's own, else the only one.</summary>
    /// <param name="fileId">The file asked for, or null.</param>
    /// <exception cref="GameBananaException">The page has no such file or no files at all;
    /// <see cref="GameBananaFileChoiceException"/> when it has several and none settles it.</exception>
    public GameBananaFile FileFor(long? fileId)
    {
        if (fileId is not { } wanted)
        {
            return InstalledFile is { } installed && !Page.IsUnavailable ? installed : Page.FileToTake(null);
        }

        return Page.FileToTake(Page.Files.FirstOrDefault(file => file.IdRow == wanted)
            ?? throw new GameBananaException(
                $"That mod has no file {wanted}. Its files are: "
                + string.Join("; ", Page.Files.Select(GameBananaMod.Describe)),
                Page.ModId,
                Page.PageUrl));
    }

    internal ModConfig Config { get; }
}

/// <summary>An update, downloaded and compared with what is installed, waiting to be confirmed.</summary>
/// <remarks>Owns the unpacked archive, deleted on dispose; the archive itself stays on the download list.</remarks>
public sealed class ModUpdatePlan : IDisposable
{
    private bool _disposed;

    internal ModUpdatePlan(
        string modFolder,
        ModConfig config,
        GameBananaMod page,
        GameBananaFile file,
        string downloadId,
        ExtractedArchive extracted,
        ArchiveModRoot root,
        IReadOnlyList<ModUpdateFileChange> changes,
        int unchangedCount)
    {
        ModFolder = modFolder;
        Config = config;
        Page = page;
        File = file;
        DownloadId = downloadId;
        Extracted = extracted;
        Root = root;
        Changes = changes;
        UnchangedCount = unchangedCount;
    }

    /// <summary>Whether the user's key and default changes are made again in the new version's INIs. On unless unticked.</summary>
    public bool KeepIniChanges { get; set; } = true;

    /// <summary>The mod being updated.</summary>
    public string ModFolder { get; }

    /// <summary>What the mod is called.</summary>
    public string DisplayName => Config.CustomName is { Length: > 0 } name ? name : System.IO.Path.GetFileName(ModFolder);

    /// <summary>The version recorded for what is installed, when one was.</summary>
    public string? InstalledVersion => Config.Version;

    /// <summary>The version the page carries now, when it carries one.</summary>
    public string? NewVersion => Page.Version;

    /// <summary>When the page last changed.</summary>
    public DateTimeOffset? PageModified => Page.DateModified;

    /// <summary>The page, as read for this update.</summary>
    public GameBananaMod Page { get; }

    /// <summary>The file on the page this update comes from.</summary>
    public GameBananaFile File { get; }

    /// <summary>Every file on the page, for choosing a different one.</summary>
    public IReadOnlyList<GameBananaFile> Files => Page.Files;

    /// <summary>The download list's entry for the archive.</summary>
    public string DownloadId { get; }

    /// <summary>The folder inside the archive that becomes the mod.</summary>
    public ArchiveModRoot Root { get; }

    /// <summary>Whether the archive held several mods and <see cref="Root"/> was chosen as the likeliest.</summary>
    public bool RootWasChosen => Extracted.ModRoots.Count > 1;

    /// <summary>Every file added, replaced or removed, in path order.</summary>
    public IReadOnlyList<ModUpdateFileChange> Changes { get; }

    /// <summary>How many files are the same in both.</summary>
    public int UnchangedCount { get; }

    /// <summary>Whether a replaced <c>.ini</c> may hold an edit of the user's.</summary>
    public bool ReplacesAnEditedIni => Changes.Any(change => change.MayBeEdited);

    internal ModConfig Config { get; }

    internal ExtractedArchive Extracted { get; }

    internal bool IsDisposed => _disposed;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Extracted.Dispose();
    }
}

/// <summary>What applying an update did.</summary>
/// <param name="ModFolder">The mod, updated, at the same path as before.</param>
/// <param name="Previous">The previous version, in the trash, for <see cref="IModUpdater.UndoAsync"/>.</param>
/// <param name="Version">The version now installed, when the page named one.</param>
/// <param name="Added">How many files were added.</param>
/// <param name="Replaced">How many were replaced.</param>
/// <param name="Removed">How many were removed.</param>
/// <param name="Carried">What became of the user's key and default changes; null when they were not kept.</param>
public sealed record ModUpdateResult(
    string ModFolder, TrashResult Previous, string? Version, int Added, int Replaced, int Removed, IniCarryReport? Carried = null);

/// <summary>Replaces an installed mod with the newer version on its GameBanana page, when the user asks.</summary>
/// <remarks>The old version goes to the trash whole; the mod's <c>.xxsm</c> folder moves to the new one.</remarks>
public interface IModUpdater
{
    /// <summary>Reads the mod's page, and downloads nothing.</summary>
    /// <param name="modFolder">The mod to update. It must be linked to a GameBanana page.</param>
    /// <param name="cancellationToken">Cancels the look-up.</param>
    /// <exception cref="GameBananaException">The mod is not linked, or the page could not be read.</exception>
    Task<ModUpdateSource> ReadAsync(string modFolder, CancellationToken cancellationToken = default);

    /// <summary>Downloads one of the page's files and compares it with what is installed.</summary>
    /// <param name="source">The page, from <see cref="ReadAsync"/>.</param>
    /// <param name="gameId">The game, for the download list.</param>
    /// <param name="fileId">Which file, or null for the one the mod came from, or the only one.</param>
    /// <param name="downloading">Told about the download when one starts, so a screen can show it.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The plan, to show and then apply or dispose.</returns>
    /// <exception cref="GameBananaException">No file could be settled on, or the download failed;
    /// <see cref="GameBananaDownloadBlockedException"/> when the site wants a browser.</exception>
    /// <exception cref="ModOperationException">The archive could not be read, or holds no mod.</exception>
    Task<ModUpdatePlan> PlanAsync(
        ModUpdateSource source,
        string? gameId,
        long? fileId = null,
        Action<DownloadJob>? downloading = null,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the mod's page, downloads the file, and compares it with what is installed.</summary>
    /// <param name="modFolder">The mod to update. It must be linked to a GameBanana page.</param>
    /// <param name="gameId">The game, for the download list.</param>
    /// <param name="fileId">Which file, or null for the one the mod came from, or the only one.</param>
    /// <param name="downloading">Told about the download when one starts, so a screen can show it.</param>
    /// <param name="cancellationToken">Cancels the look-up and the download.</param>
    /// <returns>The plan, to show and then apply or dispose.</returns>
    /// <exception cref="GameBananaException">The mod is not linked, the page could not be read, no file could
    /// be settled on, or the download failed.</exception>
    /// <exception cref="ModOperationException">The archive could not be read, or holds no mod.</exception>
    Task<ModUpdatePlan> PlanAsync(
        string modFolder,
        string? gameId,
        long? fileId = null,
        Action<DownloadJob>? downloading = null,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces the installed version with the planned one.</summary>
    /// <param name="plan">The plan the user confirmed.</param>
    /// <param name="modsDirectory">The Mods folder, for the trash's fallback location.</param>
    /// <param name="cancellationToken">Cancels before anything is moved.</param>
    /// <exception cref="ModOperationException">The mod could not be trashed or replaced; the previous version is
    /// then already back.</exception>
    Task<ModUpdateResult> ApplyAsync(
        ModUpdatePlan plan, string modsDirectory, CancellationToken cancellationToken = default);

    /// <summary>Puts the previous version back, moving the updated one to the trash.</summary>
    /// <exception cref="ModOperationException">The previous version is gone from the trash (nothing moved), or
    /// could not be put back (the updated one is restored first).</exception>
    Task UndoAsync(ModUpdateResult result, string modsDirectory, CancellationToken cancellationToken = default);
}

/// <summary>The default <see cref="IModUpdater"/>.</summary>
public sealed class ModUpdater(
    IModConfigStore configs,
    IModFileOperations files,
    IGameBananaClient client,
    IDownloadManager downloads,
    IModArchiveReader archives,
    IGameBananaInstallSource source,
    IIniOriginalsService iniOriginals,
    ILogger logger,
    TimeProvider? time = null) : IModUpdater
{
    private const string MetadataFolder = ".xxsm";
    private const string OriginalsFolder = "originals";

    /// <summary>How long after XXSM added a mod a file can still be the author's own write.</summary>
    private static readonly TimeSpan InstallSlack = TimeSpan.FromMinutes(2);

    private readonly IModConfigStore _configs = configs;
    private readonly IModFileOperations _files = files;
    private readonly IGameBananaClient _client = client;
    private readonly IDownloadManager _downloads = downloads;
    private readonly IModArchiveReader _archives = archives;
    private readonly IGameBananaInstallSource _source = source;
    private readonly IIniOriginalsService _iniOriginals = iniOriginals;
    private readonly ILogger _logger = logger.ForContext<ModUpdater>();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<ModUpdateSource> ReadAsync(string modFolder, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modFolder);

        var config = await _configs.ReadAsync(modFolder, cancellationToken).ConfigureAwait(false);

        if (config?.GameBanana?.ModId is not (> 0 and var modId))
        {
            throw new GameBananaException(
                "This mod is not linked to a GameBanana page, so there is nothing to update it from. "
                + "Give it its GameBanana address first.");
        }

        // Asked afresh: a cached page may not have the new file on it.
        var page = await _client.GetModAsync(modId, refresh: true, cancellationToken).ConfigureAwait(false);

        return new ModUpdateSource(modFolder, config, page);
    }

    /// <inheritdoc />
    public async Task<ModUpdatePlan> PlanAsync(
        string modFolder,
        string? gameId,
        long? fileId = null,
        Action<DownloadJob>? downloading = null,
        CancellationToken cancellationToken = default)
    {
        var source = await ReadAsync(modFolder, cancellationToken).ConfigureAwait(false);

        return await PlanAsync(source, gameId, fileId, downloading, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ModUpdatePlan> PlanAsync(
        ModUpdateSource source,
        string? gameId,
        long? fileId = null,
        Action<DownloadJob>? downloading = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var modFolder = source.ModFolder;
        var config = source.Config;
        var page = source.Page;
        var modId = page.ModId;
        var file = source.FileFor(fileId);

        var contents = await DownloadAsync(page, file, gameId, downloading, cancellationToken).ConfigureAwait(false);

        var extracted = await _archives
            .ExtractAsync(contents.ArchivePath, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        try
        {
            if (!extracted.HasMods)
            {
                throw new ModOperationException(
                    $"{contents.File.File ?? "The file"} holds nothing that looks like a mod, so it cannot "
                    + "replace this one. Its page may have changed what it offers.",
                    contents.ArchivePath);
            }

            var installed = FilesIn(modFolder, skipMetadata: true);
            var root = ChooseRoot(extracted, installed, modFolder);
            var incoming = FilesIn(root.Path, skipMetadata: true);
            var changes = Compare(modFolder, root.Path, installed, incoming, config, out var unchanged);

            _logger.Information(
                "Planned an update of {Path} from GameBanana mod {ModId} file {FileId}: {Added} added, " +
                "{Replaced} replaced, {Removed} removed, {Unchanged} unchanged",
                modFolder,
                modId,
                contents.File.IdRow,
                changes.Count(change => change.Kind == ModUpdateChangeKind.Added),
                changes.Count(change => change.Kind == ModUpdateChangeKind.Replaced),
                changes.Count(change => change.Kind == ModUpdateChangeKind.Removed),
                unchanged);

            return new ModUpdatePlan(
                modFolder, config, contents.Mod, contents.File, contents.Record.Id, extracted, root, changes, unchanged);
        }
        catch
        {
            extracted.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<ModUpdateResult> ApplyAsync(
        ModUpdatePlan plan, string modsDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);
        ObjectDisposedException.ThrowIf(plan.IsDisposed, plan);

        var modFolder = plan.ModFolder;
        var parent = Path.GetDirectoryName(modFolder)
                     ?? throw new ModOperationException("A mod cannot be the root of the file system.", modFolder);
        var name = Path.GetFileName(modFolder);

        // Staged beside the extraction, never inside it, or a retry copies the copy into itself.
        var scratch = plan.Extracted.StagingDirectory + ".update";
        var staged = Path.Combine(scratch, name);

        TrashResult previous;
        ModOperationResult installed;
        IniCarryReport? carried;

        try
        {
            try
            {
                RemoveScratch(scratch);
                CopyTree(plan.Root.Path, staged, skipMetadata: true);

                if (Directory.Exists(Path.Combine(modFolder, MetadataFolder)))
                {
                    CopyTree(Path.Combine(modFolder, MetadataFolder), Path.Combine(staged, MetadataFolder), skipMetadata: false);

                    // The old version's originals stay with it in the trash: the new INIs are the author's own.
                    var staleOriginals = Path.Combine(staged, MetadataFolder, OriginalsFolder);

                    if (Directory.Exists(staleOriginals))
                    {
                        Directory.Delete(staleOriginals, recursive: true);
                    }
                }

                carried = plan.KeepIniChanges
                    ? await _iniOriginals.CarryAsync(modFolder, staged, cancellationToken).ConfigureAwait(false)
                    : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ModOperationException)
            {
                throw new ModOperationException(
                    $"The new version could not be prepared, so nothing was changed: {ex.Message}", staged, ex);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Staged on the Mods folder's disk under a DISABLED_ name, then one rename: never half a mod.
            ModOperationResult waiting;

            try
            {
                waiting = await _files
                    .InstallAsync(staged, parent, ModsFolderLeftovers.UpdateCopyName(name), keepExistingName: true, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ModOperationException ex)
            {
                throw new ModOperationException(
                    $"The new version could not be copied into your Mods folder, so nothing was changed: {ex.Message}",
                    modFolder,
                    ex);
            }

            try
            {
                var deleted = await _files.DeleteAsync(modFolder, modsDirectory, cancellationToken).ConfigureAwait(false);
                previous = deleted.Trash
                           ?? throw new ModOperationException("The previous version was not moved to the trash.", modFolder);
            }
            catch
            {
                OwnScratch.TryDeleteFolder(waiting.ToPath, _logger);
                throw;
            }

            try
            {
                // Not cancellable past here, so the mod is never left half gone.
                Directory.Move(waiting.ToPath, modFolder);
                _logger.Information("Put the new version of {Mod} in place: {From} -> {To}", name, waiting.ToPath, modFolder);
                installed = waiting with { ToPath = PathComparer.Normalize(modFolder) };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                OwnScratch.TryDeleteFolder(waiting.ToPath, _logger);
                await _files.RestoreAsync(previous, CancellationToken.None).ConfigureAwait(false);

                throw new ModOperationException(
                    $"The new version could not be put in place, so the previous one was put back: {ex.Message}",
                    modFolder,
                    ex);
            }
        }
        finally
        {
            try
            {
                RemoveScratch(scratch);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Warning(ex, "Could not remove the staged update {Path}", scratch);
            }
        }

        var provenance = _source.ProvenanceOf(plan.Page, plan.File);

        try
        {
            await _configs.UpdateAsync(
                installed.ToPath,
                config => config with
                {
                    Version = plan.NewVersion is { Length: > 0 } version ? version : config.Version,
                    GameBanana = provenance with { LastChecked = _time.GetUtcNow() },
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (ModOperationException ex)
        {
            _logger.Warning(ex, "Updated {Path} but could not record the new version", installed.ToPath);
        }

        await _downloads.MarkInstalledAsync(plan.DownloadId, installed.ToPath, CancellationToken.None).ConfigureAwait(false);

        var result = new ModUpdateResult(
            installed.ToPath,
            previous,
            plan.NewVersion,
            plan.Changes.Count(change => change.Kind == ModUpdateChangeKind.Added),
            plan.Changes.Count(change => change.Kind == ModUpdateChangeKind.Replaced),
            plan.Changes.Count(change => change.Kind == ModUpdateChangeKind.Removed),
            carried);

        _logger.Information(
            "Updated {Path} to {Version} from GameBanana mod {ModId}; the previous version is in the trash at {Trash}",
            installed.ToPath,
            plan.NewVersion,
            plan.Page.ModId,
            previous.TrashedPath);

        return result;
    }

    /// <inheritdoc />
    public async Task UndoAsync(ModUpdateResult result, string modsDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);

        // Checked before anything moves, or no copy of the mod would be left.
        if (!Path.Exists(result.Previous.TrashedPath))
        {
            throw new ModOperationException(
                $"The previous version of '{Path.GetFileName(result.ModFolder)}' is no longer in the trash " +
                $"(it was at '{PathDisplay.Show(result.Previous.TrashedPath)}'), so the update cannot be undone. The updated " +
                "version is still installed.",
                result.ModFolder);
        }

        var current = await _files.DeleteAsync(result.ModFolder, modsDirectory, cancellationToken).ConfigureAwait(false);

        try
        {
            await _files.RestoreAsync(result.Previous, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ModOperationException restoreFailed)
        {
            _logger.Warning(
                restoreFailed,
                "Could not put the previous version of {Path} back; restoring the updated version",
                result.ModFolder);

            try
            {
                await _files.RestoreAsync(current.Trash!, CancellationToken.None).ConfigureAwait(false);
            }
            catch (ModOperationException backFailed)
            {
                throw new ModOperationException(
                    $"The previous version could not be put back ({restoreFailed.Message}), and neither could " +
                    $"the updated one ({backFailed.Message}). Both are in the trash: the previous version at " +
                    $"'{PathDisplay.Show(result.Previous.TrashedPath)}', the updated one at '{PathDisplay.Show(current.Trash!.TrashedPath)}'.",
                    result.ModFolder,
                    backFailed);
            }

            throw;
        }

        _logger.Information("Undid the update of {Path}; the previous version is back", result.ModFolder);
    }

    /// <summary>The archive: one already on the download list for this file, or a fresh download onto it.</summary>
    private async Task<DownloadContents> DownloadAsync(
        GameBananaMod page,
        GameBananaFile file,
        string? gameId,
        Action<DownloadJob>? downloading,
        CancellationToken cancellationToken)
    {
        var jobs = await _downloads.LoadAsync(cancellationToken).ConfigureAwait(false);

        var waiting = jobs.FirstOrDefault(job =>
            !job.IsRunning && job.Record.HasArchive && job.Record.ModId == page.ModId && job.Record.FileId == file.IdRow);

        if (waiting is not null
            && await _downloads.OpenAsync(waiting.Id, cancellationToken).ConfigureAwait(false) is { } kept)
        {
            return kept with { Mod = page };
        }

        var started = _downloads.Start(page, file, new DownloadRequest(gameId));
        downloading?.Invoke(started);

        await using (cancellationToken.Register(() => _downloads.Cancel(started.Id)).ConfigureAwait(false))
        {
            await started.Completion.ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        switch (started.State)
        {
            case DownloadState.Ready:
                break;

            case DownloadState.Blocked:
                throw new GameBananaDownloadBlockedException(
                    started.Record.Error ?? "GameBanana wants a browser for this download.",
                    page.PageUrl,
                    page.ModId);

            case DownloadState.Cancelled:
                throw new OperationCanceledException(cancellationToken);

            default:
                throw new GameBananaException(
                    started.Record.Error ?? "The download did not finish.", page.ModId, page.PageUrl);
        }

        return await _downloads.OpenAsync(started.Id, cancellationToken).ConfigureAwait(false)
               ?? throw new GameBananaException(
                   "The download finished but its archive is no longer there.", page.ModId, page.PageUrl);
    }

    /// <summary>The archive's mod that replaces this one: the only one, most files shared, then same name.</summary>
    private static ArchiveModRoot ChooseRoot(
        ExtractedArchive extracted, Dictionary<string, string> installed, string modFolder)
    {
        if (extracted.ModRoots.Count == 1)
        {
            return extracted.ModRoots[0];
        }

        var bare = ModsFolderLayout.StripDisabledPrefix(Path.GetFileName(modFolder));

        return extracted.ModRoots
            .OrderByDescending(root => FilesIn(root.Path, skipMetadata: true).Keys.Count(installed.ContainsKey))
            .ThenByDescending(root => PathComparer.AreNamesEqual(root.Name, bare))
            .First();
    }

    private static List<ModUpdateFileChange> Compare(
        string modFolder,
        string rootFolder,
        Dictionary<string, string> installed,
        Dictionary<string, string> incoming,
        ModConfig config,
        out int unchanged)
    {
        var changes = new List<ModUpdateFileChange>();
        unchanged = 0;

        foreach (var (key, relative) in incoming)
        {
            if (!installed.TryGetValue(key, out var existing))
            {
                changes.Add(new ModUpdateFileChange(relative, ModUpdateChangeKind.Added));
                continue;
            }

            var oldPath = Path.Combine(modFolder, existing);

            if (SameContents(oldPath, Path.Combine(rootFolder, relative)))
            {
                unchanged++;
                continue;
            }

            changes.Add(new ModUpdateFileChange(relative, ModUpdateChangeKind.Replaced, MayBeEdited(oldPath, config)));
        }

        changes.AddRange(installed
            .Where(pair => !incoming.ContainsKey(pair.Key))
            .Select(pair => new ModUpdateFileChange(pair.Value, ModUpdateChangeKind.Removed)));

        changes.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));

        return changes;
    }

    /// <summary>Whether an <c>.ini</c> was written after XXSM added the mod, the only sign it was edited.</summary>
    private static bool MayBeEdited(string path, ModConfig config) =>
        path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)
        && config.DateAdded is { } added
        && File.GetLastWriteTimeUtc(path) > added.UtcDateTime + InstallSlack;

    /// <summary>Every file under a folder, keyed case-insensitively by its path with <c>/</c> separators.</summary>
    private static Dictionary<string, string> FilesIn(string folder, bool skipMetadata)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in FileTree.Files(folder))
        {
            var relative = Path.GetRelativePath(folder, path).Replace(Path.DirectorySeparatorChar, '/');

            if (skipMetadata && (relative.StartsWith(MetadataFolder + "/", StringComparison.Ordinal)))
            {
                continue;
            }

            files[relative] = relative;
        }

        return files;
    }

    private static bool SameContents(string left, string right)
    {
        var a = new FileInfo(left);
        var b = new FileInfo(right);

        if (a.Length != b.Length)
        {
            return false;
        }

        using var first = a.OpenRead();
        using var second = b.OpenRead();

        var bufferA = new byte[81920];
        var bufferB = new byte[81920];

        while (true)
        {
            var readA = first.ReadAtLeast(bufferA, bufferA.Length, throwOnEndOfStream: false);
            var readB = second.ReadAtLeast(bufferB, bufferB.Length, throwOnEndOfStream: false);

            if (readA != readB || !bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB)))
            {
                return false;
            }

            if (readA == 0)
            {
                return true;
            }
        }
    }

    /// <summary>Removes a staged update: XXSM's own scratch in its own cache, never the user's files.</summary>
    private static void RemoveScratch(string scratch)
    {
        if (Directory.Exists(scratch))
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static void CopyTree(string source, string destination, bool skipMetadata)
    {
        Directory.CreateDirectory(destination);

        // Every file exactly as named: FilesIn ignores case, and would drop one of two case twins.
        foreach (var path in FileTree.Files(source))
        {
            var relative = Path.GetRelativePath(source, path).Replace(Path.DirectorySeparatorChar, '/');

            if (skipMetadata && relative.StartsWith(MetadataFolder + "/", StringComparison.Ordinal))
            {
                continue;
            }

            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(source, relative), target, overwrite: true);
        }
    }
}
