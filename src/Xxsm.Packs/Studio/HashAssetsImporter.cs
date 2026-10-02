using System.Globalization;
using Serilog;
using SharpCompress.Archives;
using Xxsm.Core;
using Xxsm.Core.Diagnostics;
using Xxsm.Core.Io;
using Xxsm.Packs.Characters;
using Xxsm.Packs.Hashes;
using Xxsm.Packs.Model;

namespace Xxsm.Packs.Studio;

/// <summary>Imports a model-importer assets repository, the primary way a new game gets its characters.</summary>
public interface IHashAssetsImporter
{
    /// <summary>Reads every <c>hash.json</c> in a folder or an archive and works out what importing does.</summary>
    /// <param name="draft">The draft it would be imported into.</param>
    /// <param name="source">A folder at or above the repository's data folders, or an archive of one; nothing
    /// is unpacked to disk.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="ModOperationException">There is nothing at <paramref name="source"/>, or it cannot be
    /// opened as an archive.</exception>
    Task<CharacterImportPlan> PlanAsync(PackDraft draft, string source, CancellationToken cancellationToken = default);

    /// <summary>Applies the rows a person chose. Pure: returns a new draft and writes nothing.</summary>
    /// <param name="draft">The draft to apply it to — normally the one the plan was made against.</param>
    /// <param name="plan">The preview.</param>
    /// <param name="choices">What was decided per row; a row with no choice takes its defaults.</param>
    /// <param name="now">When, for the draft's import history.</param>
    StudioImportOutcome Apply(
        PackDraft draft,
        CharacterImportPlan plan,
        IReadOnlyList<CharacterImportChoice>? choices,
        DateTimeOffset now);
}

/// <summary>The default <see cref="IHashAssetsImporter"/>: a hash.json's folder names its character.</summary>
/// <remarks>Only <c>PlayerCharacterData</c> rows are ticked by default. An archive's other entries are never
/// read.</remarks>
public sealed class HashAssetsImporter(ILogger logger) : IHashAssetsImporter
{
    /// <summary>The file an assets repository keeps a character's hashes in.</summary>
    public const string HashFileName = "hash.json";

    /// <summary>The most <c>hash.json</c> files one import reads.</summary>
    public const int MaxFiles = 20_000;

    /// <summary>The largest single <c>hash.json</c> read. A real one is a few kilobytes.</summary>
    public const long MaxFileBytes = 16L * 1024 * 1024;

    private const string DataSuffix = "Data";
    private const string PlayableStem = "PlayerCharacter";

    private readonly ILogger _logger = logger.ForContext<HashAssetsImporter>();

    /// <inheritdoc />
    public async Task<CharacterImportPlan> PlanAsync(
        PackDraft draft,
        string source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var resolved = PathComparer.TryResolveExisting(source, out var found)
            ? found
            : PathComparer.Normalize(Path.GetFullPath(source));

        var diagnostics = new List<Diagnostic>();
        List<FoundFile> files;

        if (Directory.Exists(resolved))
        {
            files = await ReadFolderAsync(resolved, diagnostics, cancellationToken).ConfigureAwait(false);
        }
        else if (File.Exists(resolved))
        {
            files = await ReadArchiveAsync(resolved, diagnostics, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new ModOperationException($"There is no folder or archive at '{PathDisplay.Show(resolved)}'.", resolved);
        }

        if (files.Count == 0)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                StudioImportCodes.NothingFound,
                $"No {HashFileName} files were found in '{PathDisplay.Show(resolved)}'. An assets repository keeps one in each " +
                $"character's folder, such as {PlayableStem}{DataSuffix}/<name>/{HashFileName}."));
        }

        var plan = Build(draft, resolved, files, diagnostics);

        _logger.Information(
            "Planned an assets import from {Source}: {Files} hash files, {Create} to create, {Add} to update, " +
            "{Unchanged} unchanged",
            resolved,
            files.Count,
            plan.CreateCount,
            plan.AddHashesCount,
            plan.UnchangedCount);

        return plan;
    }

    /// <inheritdoc />
    public StudioImportOutcome Apply(
        PackDraft draft,
        CharacterImportPlan plan,
        IReadOnlyList<CharacterImportChoice>? choices,
        DateTimeOffset now)
    {
        var outcome = CharacterImports.Apply(draft, plan, choices, StudioImportKinds.HashAssets, now);

        _logger.Information(
            "Applied an assets import from {Source}: created {Created}, updated {Updated}, skipped {Skipped}",
            plan.Source,
            outcome.Created.Count,
            outcome.Updated.Count,
            outcome.Skipped.Count);

        return outcome;
    }

    private static CharacterImportPlan Build(
        PackDraft draft,
        string source,
        List<FoundFile> files,
        List<Diagnostic> diagnostics)
    {
        var existing = CharacterImports.VariantsById(draft);
        var existingHashes = CharacterImports.HashKeysById(draft);

        var shaders = new SortedSet<string>(StringComparer.Ordinal);

        var found = new List<Found>();
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            var segments = file.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var directories = segments[..^1];

            if (directories.Length == 0)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    StudioImportCodes.NoCharacterFolder,
                    $"'{PathDisplay.Show(file.RelativePath)}' is not inside a character's folder, so there is no telling who it " +
                    "belongs to. It was skipped."));
                continue;
            }

            var (folder, container, playable) = Locate(directories);

            var parsed = HashPaste.ReadHashJsonEntries(file.Bytes, file.RelativePath);
            var rowDiagnostics = parsed.Rejected
                .Select(r => new Diagnostic(DiagnosticSeverity.Warning, StudioImportCodes.UnreadableFile, r.Reason))
                .ToList();

            var hashes = new List<PackHashEntry>();

            foreach (var hash in parsed.Hashes)
            {
                hashes.Add(hash.Entry with { Variant = string.Empty });

                if (hash.Entry.Kind == HashKind.RootVs)
                {
                    shaders.Add(hash.Entry.Hash);
                }
            }

            var id = PackDrafts.IsValidId(folder) ? folder : CharacterNames.Slugify(folder);
            string? note = null;

            if (!string.Equals(id, folder, StringComparison.Ordinal))
            {
                note = $"'{PathDisplay.Show(folder)}' cannot be an internal name as it is, so it would be '{id}'.";
            }

            var conflict = claimed.TryGetValue(id, out var first);

            if (conflict)
            {
                note = $"'{PathDisplay.Show(folder)}' is the same name as '{first}' apart from capitals, and internal names ignore " +
                       "capitals. Give it a different internal name, or leave it out.";
            }
            else
            {
                claimed[id] = folder;
            }

            found.Add(new Found(file.RelativePath, folder, id, container, playable, hashes, conflict, note, rowDiagnostics));
        }

        var names = found.Where(f => !f.Conflict).Select(f => f.Id).Concat(existing.Keys).ToList();
        var containers = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var row in found.Where(f => !f.Conflict && f.Container is not null))
        {
            var containerId = PackDrafts.IsValidId(row.Container) ? row.Container! : CharacterNames.Slugify(row.Container);
            containers[row.Id] = names.Find(n => string.Equals(n, containerId, StringComparison.OrdinalIgnoreCase)) ?? containerId;
        }

        var proposals = SkinLinks.Propose(names, containers)
            .GroupBy(p => p.InternalName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var rows = new List<CharacterImportRow>(found.Count);

        foreach (var row in found)
        {
            var link = proposals.GetValueOrDefault(row.Id)
                       ?? new SkinLinkProposal(row.Id, null, LinkConfidence.High, "No other character's name begins its name.");

            var (action, newCount, missingCount, currentBase) = row.Conflict
                ? (HashImportAction.Conflict, 0, 0, null)
                : CharacterImports.Compare(row.Id, row.Hashes, existing, existingHashes);

            rows.Add(new CharacterImportRow
            {
                SourcePath = row.Path,
                FolderName = row.Folder,
                InternalName = row.Id,
                ContainerFolder = row.Container,
                Hashes = row.Hashes,
                Action = action,
                NewHashCount = newCount,
                MissingHashCount = missingCount,
                Link = link,
                CurrentBaseCharacterId = currentBase,
                SelectedByDefault = action switch
                {
                    HashImportAction.Create => row.Playable,
                    HashImportAction.AddHashes => true,
                    _ => false,
                },
                Note = row.Note,
                Diagnostics = row.Diagnostics,
            });
        }

        return new CharacterImportPlan
        {
            Source = source,
            Rows = rows,
            SharedShaderHashes = [.. shaders],
            Diagnostics = diagnostics,
        };
    }

    /// <summary>Works out which character a <c>hash.json</c> belongs to from the folders above it.</summary>
    private static (string Folder, string? Container, bool Playable) Locate(string[] directories)
    {
        var data = Array.FindIndex(
            directories,
            d => d.Length > DataSuffix.Length && d.EndsWith(DataSuffix, StringComparison.OrdinalIgnoreCase));

        if (data < 0 || data == directories.Length - 1)
        {
            // No data folder above it: a single character folder, or a folder of them. Playable.
            return (directories[^1], null, true);
        }

        var stem = directories[data][..^DataSuffix.Length];
        var playable = string.Equals(stem, PlayableStem, StringComparison.OrdinalIgnoreCase);

        var folder = directories[^1];
        var container = directories.Length - data > 2 ? directories[^2] : null;

        return (folder, container, playable);
    }

    private async Task<List<FoundFile>> ReadFolderAsync(
        string root,
        List<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var files = new List<FoundFile>();
        var top = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            IgnoreInaccessible = true,
        };

        IEnumerable<string> paths;

        try
        {
            paths = Directory.EnumerateFiles(root, HashFileName, options).Order(StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException($"Could not look inside '{PathDisplay.Show(root)}': {ex.Message}", root, ex);
        }

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (files.Count >= MaxFiles)
            {
                diagnostics.Add(LimitReached(root));
                break;
            }

            var relative = PathComparer.Normalize(Path.GetRelativePath(root, path));

            try
            {
                if (new FileInfo(path).Length > MaxFileBytes)
                {
                    diagnostics.Add(TooLarge(relative));
                    continue;
                }

                var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                files.Add(new FoundFile(top + "/" + relative, bytes));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Warning(ex, "Could not read {Path} during an assets import", path);
                diagnostics.Add(Unreadable(relative, ex.Message));
            }
        }

        return files;
    }

    private async Task<List<FoundFile>> ReadArchiveAsync(
        string archivePath,
        List<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        IArchive archive;

        try
        {
            archive = ArchiveFactory.Open(archivePath);
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                       or IOException
                                       or UnauthorizedAccessException
                                       or NotSupportedException)
        {
            throw new ModOperationException(
                $"'{PathDisplay.Show(archivePath)}' could not be opened as a folder or an archive: {ex.Message}. If it needs a " +
                "password or is a format XXSM does not read, unpack it yourself and choose the folder instead.",
                archivePath,
                ex);
        }

        var files = new List<FoundFile>();

        using (archive)
        {
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (entry.IsDirectory || string.IsNullOrWhiteSpace(entry.Key))
                {
                    continue;
                }

                var key = PathComparer.Normalize(entry.Key).TrimStart('/');

                if (!string.Equals(Path.GetFileName(key), HashFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (files.Count >= MaxFiles)
                {
                    diagnostics.Add(LimitReached(archivePath));
                    break;
                }

                if (entry.Size > MaxFileBytes)
                {
                    diagnostics.Add(TooLarge(key));
                    continue;
                }

                try
                {
                    await using var stream = entry.OpenEntryStream();
                    using var buffer = new MemoryStream();

                    // Counted as it is read: the size an archive declares is only its word.
                    var chunk = new byte[81920];
                    int read;

                    while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        if (buffer.Length + read > MaxFileBytes)
                        {
                            break;
                        }

                        buffer.Write(chunk, 0, read);
                    }

                    if (read > 0)
                    {
                        diagnostics.Add(TooLarge(key));
                        continue;
                    }

                    files.Add(new FoundFile(key, buffer.ToArray()));
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException)
                {
                    _logger.Warning(ex, "Could not read {Entry} from {Archive} during an assets import", key, archivePath);
                    diagnostics.Add(Unreadable(key, ex.Message));
                }
            }
        }

        return files;
    }

    private static Diagnostic LimitReached(string source) => new(
        DiagnosticSeverity.Warning,
        StudioImportCodes.LimitReached,
        $"'{PathDisplay.Show(source)}' holds more than {MaxFiles.ToString(CultureInfo.InvariantCulture)} {HashFileName} files. " +
        "Only the first ones were read.");

    private static Diagnostic TooLarge(string relative) => new(
        DiagnosticSeverity.Warning,
        StudioImportCodes.LimitReached,
        $"'{PathDisplay.Show(relative)}' is too large to be a real {HashFileName} and was skipped.");

    private static Diagnostic Unreadable(string relative, string message) => new(
        DiagnosticSeverity.Warning,
        StudioImportCodes.UnreadableFile,
        $"'{PathDisplay.Show(relative)}' could not be read and was skipped: {message}");

    private sealed record FoundFile(string RelativePath, byte[] Bytes);

    private sealed record Found(
        string Path,
        string Folder,
        string Id,
        string? Container,
        bool Playable,
        List<PackHashEntry> Hashes,
        bool Conflict,
        string? Note,
        List<Diagnostic> Diagnostics);
}
