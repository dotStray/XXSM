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
    IModRepository repository,
    IModSwitcher switcher,
    ISortRunner sorting,
    ILogger logger) : IModInstaller
{
    private readonly IModArchiveReader _archives = archives;
    private readonly IModHashLearner _learner = learner;
    private readonly IModFileOperations _files = files;
    private readonly IModPreviewSource _previews = previews;
    private readonly IModConfigStore _configs = configs;
    private readonly IModPreviewEditor _pictures = pictures;
    private readonly IModFiling _filing = filing;
    private readonly IModRepository _repository = repository;
    private readonly IModSwitcher _switcher = switcher;
    private readonly ISortRunner _sorting = sorting;
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
        InstallSwitching switching = InstallSwitching.AsItIs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(data);

        if (switching == InstallSwitching.OnlyThis && !CanSwitchOnlyThis(choices, data))
        {
            throw new ModOperationException(
                "Switching off a character's other mods needs exactly one mod going to a character, not to Others. " +
                "Nothing was installed.");
        }

        var outcomes = new List<InstallOutcome>(choices.Count);

        foreach (var choice in choices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var folder = FolderFor(choice, data);
            var destination = PathComparer.Join(plan.ModsDirectory, folder);
            var given = choice.Name is { Length: > 0 } chosen ? chosen : choice.Candidate.Name;
            var name = switching switch
            {
                InstallSwitching.Off => ModsFolderLayout.AddDisabledPrefix(given),
                InstallSwitching.OnlyThis => ModsFolderLayout.StripDisabledPrefix(given),
                _ => given,
            };

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

        var switchedOff = switching == InstallSwitching.OnlyThis && outcomes is [{ Succeeded: true } only]
            ? await SwitchOffOthersAsync(plan.ModsDirectory, only.InstalledPath!, cancellationToken).ConfigureAwait(false)
            : null;

        return new InstallResult { Outcomes = outcomes, SwitchedOff = switchedOff };
    }

    /// <inheritdoc />
    public bool CanSwitchOnlyThis(IReadOnlyList<InstallChoice> choices, GameData data)
    {
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(data);

        return choices is [var one] &&
               !string.Equals(FolderFor(one, data), ModsFolderLayout.UnsortedFolderName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Switches off every other switched-on mod in the new mod's character folder, as one journalled run.</summary>
    private async Task<ModSwitchRunResult?> SwitchOffOthersAsync(string modsDirectory, string installed, CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(installed)!;
        var inventory = await _repository.ScanAsync(modsDirectory, cancellationToken).ConfigureAwait(false);
        var others = inventory.AllMods
            .Where(mod => mod.IsEnabled &&
                          PathComparer.AreEqual(Path.GetDirectoryName(mod.Path) ?? string.Empty, folder) &&
                          !PathComparer.AreEqual(mod.Path, installed))
            .Select(mod => new ModSwitch(mod.Path, Enable: false))
            .ToList();

        if (others.Count == 0)
        {
            return null;
        }

        var run = await _switcher.ApplyAsync(modsDirectory, others, ModSwitchSource.Install, label: null, cancellationToken)
            .ConfigureAwait(false);

        _logger.Information("Switched off {Count} other mods in {Folder} after installing {Mod}", run.ChangedCount, folder, installed);

        return run;
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
                    && !PathComparer.AreNamesEqual(wanted, ModsFolderLayout.StripDisabledPrefix(result.ToName))
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
        var read = await ReadSourceAsync(folder, archiveName: null, isArchive: false, data, target, settings, cancellationToken)
            .ConfigureAwait(false);

        var diagnostics = new List<Diagnostic>();

        if (read.Suggested.Candidates.Count == 0)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                ModDiagnosticCodes.UnfiledMod,
                $"Nothing in '{PathDisplay.Show(folder)}' looks like a 3DMigoto mod — no INI, no buffers, no " +
                "textures. Check you pointed at the mod itself rather than the folder above it."));
        }

        return new InstallPlan(
            folder, modsDirectory, archive: null, read.Suggested, read.Alternative, read.Reason, diagnostics, target?.InternalName);
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
            var read = await ReadSourceAsync(
                    extracted.StagingDirectory, extracted.ArchiveName, isArchive: true, data, target, settings, cancellationToken)
                .ConfigureAwait(false);

            var diagnostics = new List<Diagnostic>(extracted.Diagnostics);

            if (read.Suggested.Candidates.Count == 0)
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
                read.Suggested,
                read.Alternative,
                read.Reason,
                diagnostics,
                target?.InternalName);
        }
        catch
        {
            extracted.Dispose();
            throw;
        }
    }

    /// <summary>How a source was read: the grouping proposed, the other one, and why.</summary>
    private sealed record SourceReading(InstallGrouping Suggested, InstallGrouping? Alternative, string? Reason)
    {
        public static SourceReading Nothing { get; } = new(new InstallGrouping(IsOneMod: false, [], []), null, null);
    }

    /// <summary>
    /// Reads a folder or an unpacked archive. A source that is a mod is one mod. Otherwise each folder in it is placed
    /// as the sort places a folder in the Mods folder, and the folders are one mod made of parts unless they point at
    /// different characters or two of them change the same things.
    /// </summary>
    private async Task<SourceReading> ReadSourceAsync(
        string root,
        string? archiveName,
        bool isArchive,
        GameData data,
        Merge.MergedVariant? target,
        SortSettings? settings,
        CancellationToken cancellationToken)
    {
        var top = ModsFolderLayout.UnwrapLoneFolder(root);

        if (ModsFolderLayout.LooksLikeModFolder(top))
        {
            // A folder the user picked may be a mod of pictures alone; one found inside a source must hold mod content.
            if ((isArchive || !PathComparer.AreEqual(root, top)) && !ModsFolderLayout.ContainsModContent(top))
            {
                return SourceReading.Nothing;
            }

            var only = await WholeAsync(root, top, archiveName, data, target, settings, decided: null, cancellationToken)
                .ConfigureAwait(false);

            return new SourceReading(new InstallGrouping(IsOneMod: true, [only], []), null, null);
        }

        var sorted = await _sorting.PlanAsync(top, data, settings, cancellationToken).ConfigureAwait(false);
        var rows = sorted.Rows.Where(row => ModsFolderLayout.ContainsModContent(row.Mod.Path)).ToList();

        if (rows.Count == 0)
        {
            return SourceReading.Nothing;
        }

        var separate = new List<InstallCandidate>(rows.Count);

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            separate.Add(await CandidateAsync(
                    row.Mod.Path,
                    RelativeTo(root, row.Mod.Path),
                    Path.GetFileName(row.Mod.Path),
                    archiveName: null,
                    data,
                    target,
                    settings,
                    row,
                    cancellationToken)
                .ConfigureAwait(false));
        }

        separate.Sort(static (left, right) => PathComparer.Instance.Compare(left.RelativePath, right.RelativePath));

        var pointing = new List<SortRunRow>();

        foreach (var row in rows.Where(PointsAtACharacter))
        {
            if (!pointing.Exists(other => PathComparer.AreNamesEqual(other.DestinationFolderName, row.DestinationFolderName)))
            {
                pointing.Add(row);
            }
        }

        var whole = await WholeAsync(
                root, top, archiveName, data, target, settings, pointing.Count == 1 ? pointing[0] : null, cancellationToken)
            .ConfigureAwait(false);

        whole = whole with { IncludedParts = [.. separate.Select(part => RelativeTo(top, part.SourcePath))] };

        var joined = new InstallGrouping(IsOneMod: true, [whole], []);
        var apart = new InstallGrouping(IsOneMod: false, separate, Stranded(root, top, separate));

        if (separate.Count == 1)
        {
            return new SourceReading(joined, null, null);
        }

        var clash = FirstClash(separate);
        var count = separate.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

        string reason;
        bool isOneMod;

        if (pointing.Count > 1)
        {
            isOneMod = false;
            reason = $"Suggested: {count} separate mods, as the folders are for {Characters(pointing, data)}.";
        }
        else if (clash is { } pair)
        {
            isOneMod = false;
            reason = $"Suggested: {count} separate mods, as '{pair.First}' and '{pair.Second}' change the same things, " +
                     "so they cannot be on together.";
        }
        else
        {
            isOneMod = true;
            var recognised = pointing.Count == 1
                ? $"only {Characters(pointing, data)} is recognised in them"
                : "none is recognised in them";

            reason = $"Suggested: one mod, as its {count} folders change different things, and {recognised}.";
        }

        _logger.Information(
            "Read {Source} as {Grouping}: {Folders} folders, {Characters} characters recognised, clash {Clash}",
            root,
            isOneMod ? "one mod" : "separate mods",
            separate.Count,
            pointing.Count,
            clash is { } found ? $"{found.First} / {found.Second}" : "none");

        return isOneMod
            ? new SourceReading(joined, apart, reason)
            : new SourceReading(apart, joined, reason);
    }

    /// <summary>The source's top folder as one candidate, named after the archive when it is the archive's root.</summary>
    private Task<InstallCandidate> WholeAsync(
        string root,
        string top,
        string? archiveName,
        GameData data,
        Merge.MergedVariant? target,
        SortSettings? settings,
        SortRunRow? decided,
        CancellationToken cancellationToken)
    {
        var name = PathComparer.AreEqual(root, top) && archiveName is { Length: > 0 } archive
            ? archive
            : Path.GetFileName(PathComparer.Normalize(top).TrimEnd('/'));

        return CandidateAsync(top, RelativeTo(root, top), name, archiveName, data, target, settings, decided, cancellationToken);
    }

    /// <summary>Whether the sort placed a folder under a character on evidence of its own: a known hash, a character
    /// waiting for hashes, or the user's filing. A folder only named like someone points nowhere.</summary>
    private static bool PointsAtACharacter(SortRunRow row) =>
        !PathComparer.AreNamesEqual(row.DestinationFolderName, ModsFolderLayout.UnsortedFolderName)
        && (row.Decision.Candidates.Count > 0
            || row.Decision.MatchedVariantHasNoHashes
            || row.Decision.DecidedBy == SortDecidedBy.Manual);

    /// <summary>The first two folders that change the same things — at least two hashes, and at least half of the
    /// smaller one's — so cannot be on together; null when no two do.</summary>
    private static (string First, string Second)? FirstClash(List<InstallCandidate> candidates)
    {
        var hashes = candidates
            .Select(candidate => candidate.Learned.Hashes
                .Select(hash => hash.Entry.Hash.ToLowerInvariant())
                .ToHashSet(StringComparer.Ordinal))
            .ToList();

        for (var first = 0; first < candidates.Count; first++)
        {
            for (var second = first + 1; second < candidates.Count; second++)
            {
                var shared = hashes[first].Count(hashes[second].Contains);
                var smaller = Math.Min(hashes[first].Count, hashes[second].Count);

                if (shared >= 2 && shared * 2 >= smaller)
                {
                    return (candidates[first].Name, candidates[second].Name);
                }
            }
        }

        return null;
    }

    /// <summary>The characters some folders were placed under, by display name: three, then how many more.</summary>
    private static string Characters(IReadOnlyList<SortRunRow> rows, GameData data)
    {
        var names = rows
            .Select(row => VariantFor(row, data)?.DisplayName ?? row.DestinationFolderName)
            .ToList();

        if (names.Count == 1)
        {
            return names[0];
        }

        if (names.Count <= 3)
        {
            return $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
        }

        var more = (names.Count - 3).ToString(System.Globalization.CultureInfo.InvariantCulture);

        return $"{string.Join(", ", names.Take(3))} and {more} more";
    }

    /// <summary>The variant whose folder the sort chose: the one it decided on when that is the folder, else the first
    /// filed under it.</summary>
    private static Merge.MergedVariant? VariantFor(SortRunRow row, GameData data)
    {
        if (row.Decision.VariantId is { Length: > 0 } decided
            && data.Find(decided) is { } variant
            && PathComparer.AreNamesEqual(variant.ModFilesName, row.DestinationFolderName))
        {
            return variant;
        }

        return data.Variants.FirstOrDefault(
            candidate => PathComparer.AreNamesEqual(candidate.ModFilesName, row.DestinationFolderName));
    }

    /// <summary>A path inside a source, relative to it with <c>/</c>; empty for the source itself.</summary>
    private static string RelativeTo(string root, string path) =>
        PathComparer.AreEqual(root, path)
            ? string.Empty
            : PathComparer.Normalize(Path.GetRelativePath(root, path));

    /// <summary>The files of a source that none of its separate mods holds, relative to the source; clutter left out.</summary>
    private static List<string> Stranded(string root, string top, IReadOnlyList<InstallCandidate> candidates)
    {
        var stranded = new List<string>();

        foreach (var file in FileTree.Files(top))
        {
            var relative = RelativeTo(root, file);

            if (relative.Split('/').Any(ModsFolderLayout.IsSourceClutter)
                || candidates.Any(candidate => PathComparer.IsSameOrUnder(candidate.SourcePath, file)))
            {
                continue;
            }

            stranded.Add(relative);
        }

        stranded.Sort(PathComparer.Instance);
        return stranded;
    }

    private async Task<InstallCandidate> CandidateAsync(
        string path,
        string relativePath,
        string name,
        string? archiveName,
        GameData data,
        Merge.MergedVariant? target,
        SortSettings? settings,
        SortRunRow? decided,
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

        var (variantId, folderName, reason) = Propose(learned, target, decided, data);
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
        LearnedFromMod learned, Merge.MergedVariant? target, SortRunRow? decided, GameData data)
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

        if (decided is not null)
        {
            if (VariantFor(decided, data) is { } placed)
            {
                return (placed.InternalName, placed.ModFilesName, decided.Reason);
            }
        }
        else if (learned.MatchedVariantId is { Length: > 0 } variantId && data.Find(variantId) is { } variant)
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
