using Serilog;
using Xxsm.Core;
using Xxsm.Core.Archives;
using Xxsm.Core.Diagnostics;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Packs.Characters;
using Xxsm.Packs.Merge;
using Xxsm.Packs.Sorting;

namespace Xxsm.Packs.Installation;

/// <summary>The default <see cref="IModInstaller"/>.</summary>
public sealed class ModInstaller(
    IModArchiveReader archives,
    IModHashLearner learner,
    IModFileOperations files,
    IModPreviewSource previews,
    IModConfigStore configs,
    IModPreviewEditor pictures,
    IModFiling filing,
    ILogger logger) : IModInstaller
{
    private readonly IModArchiveReader _archives = archives;
    private readonly IModHashLearner _learner = learner;
    private readonly IModFileOperations _files = files;
    private readonly IModPreviewSource _previews = previews;
    private readonly IModConfigStore _configs = configs;
    private readonly IModPreviewEditor _pictures = pictures;
    private readonly IModFiling _filing = filing;
    private readonly ILogger _logger = logger.ForContext<ModInstaller>();

    /// <inheritdoc />
    public async Task<InstallPlan> PlanAsync(
        string source,
        GameData data,
        string modsDirectory,
        string? targetVariantId = null,
        SortSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);
        ArgumentNullException.ThrowIfNull(data);

        var resolved = PathComparer.TryResolveExisting(source, out var found)
            ? found
            : PathComparer.Normalize(Path.GetFullPath(source));

        var target = targetVariantId is { Length: > 0 }
            ? data.Find(targetVariantId)
              ?? throw new ModOperationException(
                  $"There is no character called '{targetVariantId}' to install into.", targetVariantId)
            : null;

        return Directory.Exists(resolved)
            ? await PlanFolderAsync(resolved, data, modsDirectory, target, settings, cancellationToken)
                .ConfigureAwait(false)
            : await PlanArchiveAsync(resolved, data, modsDirectory, target, settings, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<InstallResult> ApplyAsync(
        InstallPlan plan,
        IReadOnlyList<InstallChoice> choices,
        GameData data,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(data);

        var outcomes = new List<InstallOutcome>(choices.Count);

        foreach (var choice in choices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var folder = FolderFor(choice, data);
            var destination = PathComparer.Join(plan.ModsDirectory, folder);
            var name = choice.Name is { Length: > 0 } chosen ? chosen : choice.Candidate.Name;

            try
            {
                var result = await _files
                    .InstallAsync(choice.Candidate.SourcePath, destination, name, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                var metadataError = await RecordAsync(choice, result, cancellationToken)
                    .ConfigureAwait(false);

                var filingError = await RememberChoiceAsync(plan, choice, result, data, cancellationToken)
                    .ConfigureAwait(false);

                outcomes.Add(new InstallOutcome(
                    choice, folder, result, Error: null, Join(metadataError, filingError)));

                _logger.Information(
                    "Installed {Name} from {Source} to {Destination}",
                    result.ToName,
                    choice.Candidate.SourcePath,
                    result.ToPath);
            }
            catch (XxsmException exception)
            {
                // One failure must not lose the rest; the OS's own words go to the user.
                outcomes.Add(new InstallOutcome(choice, folder, Result: null, exception.Message));

                _logger.Warning(
                    exception,
                    "Could not install {Name} from {Source}",
                    name,
                    choice.Candidate.SourcePath);
            }
        }

        return new InstallResult { Outcomes = outcomes };
    }

    /// <summary>The character folder a choice lands in, by <c>modFilesName</c> as auto-sort files.</summary>
    private static string FolderFor(InstallChoice choice, GameData data)
    {
        var id = choice.VariantId is { Length: > 0 } chosen ? chosen : choice.Candidate.SuggestedVariantId;

        return id is { Length: > 0 } && data.Find(id) is { } variant
            ? variant.ModFilesName
            : ModsFolderLayout.UnsortedFolderName;
    }

    /// <summary>Remembers the character a person chose for a mod, when they chose one; not for <c>Others</c>.</summary>
    /// <returns>Why it could not be remembered, or null when there was nothing to remember or it was.</returns>
    private async Task<string?> RememberChoiceAsync(
        InstallPlan plan,
        InstallChoice choice,
        ModOperationResult result,
        GameData data,
        CancellationToken cancellationToken)
    {
        var changedFromProposal = choice.VariantId is { Length: > 0 } picked
                                  && !string.Equals(
                                      picked, choice.Candidate.SuggestedVariantId, StringComparison.OrdinalIgnoreCase);

        if ((plan.TargetVariantId is null && !changedFromProposal)
            || result.ToPath is not { Length: > 0 } path)
        {
            return null;
        }

        var id = choice.VariantId is { Length: > 0 } chosen ? chosen : choice.Candidate.SuggestedVariantId;

        if (id is not { Length: > 0 } || data.Find(id) is not { } variant)
        {
            return null;
        }

        try
        {
            await _filing.RememberAsync(path, variant.InternalName, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (XxsmException exception)
        {
            _logger.Warning(exception, "Installed {Path} but could not remember it was filed by hand", path);

            return exception.Message;
        }
    }

    private static string? Join(string? first, string? second) =>
        first is null ? second : second is null ? first : first + " " + second;

    /// <summary>Writes what the user typed about a mod into its <c>.xxsm/mod.json</c>, when there is any.</summary>
    /// <returns>Why it could not be written, or null when there was nothing to write or it was.</returns>
    private async Task<string?> RecordAsync(
        InstallChoice choice, ModOperationResult result, CancellationToken cancellationToken)
    {
        var metadata = await RecordMetadataAsync(choice, result, cancellationToken).ConfigureAwait(false);

        if (choice.PreviewImage is not { } picture || result.ToPath is not { Length: > 0 } path)
        {
            return metadata;
        }

        try
        {
            await _pictures.SetAsync(path, picture, cancellationToken).ConfigureAwait(false);
            return metadata;
        }
        catch (XxsmException exception)
        {
            _logger.Warning(exception, "Installed {Path} but could not set the picture given for it", path);

            return metadata is null ? exception.Message : metadata + " " + exception.Message;
        }
    }

    private async Task<string?> RecordMetadataAsync(
        InstallChoice choice, ModOperationResult result, CancellationToken cancellationToken)
    {
        if (result.ToPath is not { Length: > 0 } path)
        {
            return null;
        }

        var label = choice.DisplayName is { Length: > 0 } wanted
                    && !PathComparer.AreNamesEqual(wanted, result.ToName)
            ? wanted
            : null;

        if (label is null
            && choice.Author is not { Length: > 0 }
            && choice.ModUrl is not { Length: > 0 }
            && choice.Notes is not { Length: > 0 }
            && choice.Version is not { Length: > 0 }
            && choice.Description is not { Length: > 0 }
            && choice.GameBanana is null)
        {
            return null;
        }

        try
        {
            await _configs
                .UpdateAsync(
                    path,
                    config => Linked(config, choice.ModUrl) with
                    {
                        CustomName = label ?? config.CustomName,
                        Author = choice.Author is { Length: > 0 } author ? author : config.Author,
                        Notes = choice.Notes is { Length: > 0 } notes ? notes : config.Notes,
                        Version = choice.Version is { Length: > 0 } version ? version : config.Version,
                        Description = choice.Description is { Length: > 0 } text ? text : config.Description,
                        GameBanana = choice.GameBanana ?? Linked(config, choice.ModUrl).GameBanana,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            return null;
        }
        catch (XxsmException exception)
        {
            _logger.Warning(exception, "Installed {Path} but could not write its metadata", path);

            return exception.Message;
        }
    }

    /// <summary>The configuration with the typed address, linked to its GameBanana page when it is one.</summary>
    private static ModConfig Linked(ModConfig config, string? modUrl) =>
        modUrl is { Length: > 0 } url ? config.WithModUrl(url) : config;

    private async Task<InstallPlan> PlanFolderAsync(
        string folder,
        GameData data,
        string modsDirectory,
        Merge.MergedVariant? target,
        SortSettings? settings,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<Diagnostic>();
        var candidates = new List<InstallCandidate>();

        if (ModsFolderLayout.LooksLikeModFolder(folder))
        {
            candidates.Add(await CandidateAsync(
                    folder,
                    relativePath: string.Empty,
                    Path.GetFileName(folder),
                    archiveName: null,
                    data,
                    target,
                    settings,
                    cancellationToken)
                .ConfigureAwait(false));
        }
        else
        {
            foreach (var child in Directory.EnumerateDirectories(folder).Order(PathComparer.Instance))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var name = Path.GetFileName(child);

                // Both tests for a child, only the structural one for the folder the user picked.
                if (ModsFolderLayout.IsReservedEntry(name)
                    || !ModsFolderLayout.LooksLikeModFolder(child)
                    || !ModsFolderLayout.ContainsModContent(child))
                {
                    continue;
                }

                candidates.Add(await CandidateAsync(
                        child, name, name, archiveName: null, data, target, settings, cancellationToken)
                    .ConfigureAwait(false));
            }

            if (candidates.Count == 0)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    ModDiagnosticCodes.UnfiledMod,
                    $"Nothing in '{PathDisplay.Show(folder)}' looks like a 3DMigoto mod — no INI, no buffers, no " +
                    "textures. Check you pointed at the mod itself rather than the folder above it."));
            }
        }

        return new InstallPlan(
            folder, modsDirectory, archive: null, candidates, strandedFiles: [], diagnostics, target?.InternalName);
    }

    private async Task<InstallPlan> PlanArchiveAsync(
        string archivePath,
        GameData data,
        string modsDirectory,
        Merge.MergedVariant? target,
        SortSettings? settings,
        CancellationToken cancellationToken)
    {
        var extracted = await _archives.ExtractAsync(archivePath, bounds: null, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var candidates = new List<InstallCandidate>();

            foreach (var root in extracted.ModRoots)
            {
                cancellationToken.ThrowIfCancellationRequested();

                candidates.Add(await CandidateAsync(
                        root.Path,
                        root.RelativePath,
                        root.Name is { Length: > 0 } named ? named : extracted.ArchiveName,
                        extracted.ArchiveName,
                        data,
                        target,
                        settings,
                        cancellationToken)
                    .ConfigureAwait(false));
            }

            var diagnostics = new List<Diagnostic>(extracted.Diagnostics);

            if (candidates.Count == 0)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    ModDiagnosticCodes.UnfiledMod,
                    $"Nothing in '{Path.GetFileName(archivePath)}' looks like a 3DMigoto mod. " +
                    "It may be a collection of loose textures, or it may need unpacking a level first."));
            }

            return new InstallPlan(
                archivePath,
                modsDirectory,
                extracted,
                candidates,
                extracted.StrandedFiles,
                diagnostics,
                target?.InternalName);
        }
        catch
        {
            extracted.Dispose();
            throw;
        }
    }

    private async Task<InstallCandidate> CandidateAsync(
        string path,
        string relativePath,
        string name,
        string? archiveName,
        GameData data,
        Merge.MergedVariant? target,
        SortSettings? settings,
        CancellationToken cancellationToken)
    {
        var learned = await _learner
            .LearnAsync(
                path,
                data,
                new SortRequest(FolderName: name, ArchiveName: archiveName),
                settings,
                cancellationToken)
            .ConfigureAwait(false);

        var preview = await _previews.FindAsync(path, config: null, bounds: null, cancellationToken)
            .ConfigureAwait(false);

        var (variantId, folderName, reason) = Propose(learned, target, data);
        var (fileCount, bytes, files) = Inspect(path);

        return new InstallCandidate
        {
            SourcePath = PathComparer.Normalize(path),
            RelativePath = relativePath,
            Name = name,
            FileCount = fileCount,
            Files = files,
            Bytes = bytes,
            Learned = learned,
            SuggestedVariantId = variantId,
            SuggestedFolderName = folderName,
            Reason = reason,
            PreviewPath = preview?.Path,
        };
    }

    /// <summary>What a candidate holds: file count, bytes, and the first names up to the listing limit.</summary>
    private static (int FileCount, long Bytes, IReadOnlyList<string> Files) Inspect(string directory)
    {
        var count = 0;
        var bytes = 0L;
        var names = new List<string>();

        try
        {
            foreach (var file in FileTree.Files(directory))
            {
                count++;

                if (names.Count < InstallCandidate.FileListLimit)
                {
                    names.Add(PathComparer.TryGetRelativePath(directory, file) ?? Path.GetFileName(file));
                }

                try
                {
                    bytes += new FileInfo(file).Length;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    bytes += 0;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return (count, bytes, names);
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return (count, bytes, names);
    }

    /// <summary>Where a candidate should go and why; a character the user chose outranks the sorter.</summary>
    private static (string? VariantId, string FolderName, string Reason) Propose(
        LearnedFromMod learned, Merge.MergedVariant? target, GameData data)
    {
        if (target is not null)
        {
            var agrees = learned.MatchedVariantId is { Length: > 0 } matched
                         && PathComparer.AreNamesEqual(matched, target.InternalName);

            var reason = agrees
                ? $"You chose {target.DisplayName}, and its hashes agree."
                : learned.MatchedVariantId is { Length: > 0 } other
                    ? $"You chose {target.DisplayName}. Its hashes look like " +
                      $"{data.Find(other)?.DisplayName ?? other}, so check this is what you meant."
                    : $"You chose {target.DisplayName}.";

            return (target.InternalName, target.ModFilesName, reason);
        }

        if (learned.MatchedVariantId is { Length: > 0 } variantId && data.Find(variantId) is { } variant)
        {
            return (variant.InternalName, variant.ModFilesName, Explain(learned));
        }

        return (
            null,
            ModsFolderLayout.UnsortedFolderName,
            $"Nothing identified this mod, so it goes to {ModsFolderLayout.UnsortedFolderName}. " +
            "Pick a character, or install it and teach that character its hashes afterwards.");
    }

    /// <summary>The sentence the confirm step shows for an identified mod.</summary>
    private static string Explain(LearnedFromMod learned) => learned.Decision.Explanation;
}
