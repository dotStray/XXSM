using Serilog;
using Xxsm.Core;
using Xxsm.Core.Diagnostics;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Packs.Merge;

namespace Xxsm.Packs.Sorting;

/// <summary>The default <see cref="ISortRunner"/>.</summary>
public sealed class SortRunner(
    IModRepository repository,
    IModSignalExtractor signals,
    IModSorter sorter,
    IModFileOperations files,
    IModConfigStore configs,
    IModFiling filing,
    ISortJournal journal,
    TimeProvider time,
    ILogger logger) : ISortRunner
{
    private readonly IModRepository _repository = repository;
    private readonly IModSignalExtractor _signals = signals;
    private readonly IModSorter _sorter = sorter;
    private readonly IModFileOperations _files = files;
    private readonly IModConfigStore _configs = configs;
    private readonly IModFiling _filing = filing;
    private readonly ISortJournal _journal = journal;
    private readonly TimeProvider _time = time;
    private readonly ILogger _logger = logger.ForContext<SortRunner>();

    /// <inheritdoc />
    public async Task<SortKeepResult> KeepAsync(
        IReadOnlyList<SortRunRow> rows,
        GameData gameData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(gameData);

        var remembered = new List<SortRunRow>();
        var notRemembered = new List<SortRunRow>();
        var failures = new List<SortKeepFailure>();

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!row.WillMove)
            {
                continue;
            }

            if (UnsortedMods.CharacterFor(row.Mod.VariantFolderName, gameData) is not { } character)
            {
                notRemembered.Add(row);
                continue;
            }

            try
            {
                await _filing.RememberAsync(row.Mod.Path, character.InternalName, cancellationToken)
                    .ConfigureAwait(false);

                remembered.Add(row);
            }
            catch (ModOperationException ex)
            {
                // One mod whose metadata cannot be written must not cost the others.
                failures.Add(new SortKeepFailure(row, ex.Message));
                _logger.Warning(ex, "Could not remember {Mod} as kept where it is", row.Mod.Path);
            }
        }

        return new SortKeepResult
        {
            Remembered = remembered,
            NotRemembered = notRemembered,
            Failures = failures,
        };
    }

    /// <inheritdoc />
    public async Task<SortRunPlan> PlanAsync(
        string modsDirectory,
        GameData gameData,
        SortSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gameData);

        var inventory = await _repository.ScanAsync(modsDirectory, cancellationToken).ConfigureAwait(false);

        // Built once for the whole run: it is the expensive part.
        var index = SortIndex.Build(gameData, settings);
        var diagnostics = new List<Diagnostic>(inventory.Diagnostics);
        var rows = new List<SortRunRow>(inventory.ModCount);

        foreach (var folder in inventory.VariantFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var decided = new List<DecidedMod>(folder.Mods.Count);

            foreach (var mod in folder.Mods)
            {
                decided.Add(await DecideAsync(mod, index, diagnostics, cancellationToken).ConfigureAwait(false));
            }

            // A folder of one character's mods that is not a character folder is an archive: it moves whole.
            if (WrapperDestination(folder, decided, gameData) is { Length: > 0 } destination)
            {
                var wrapper = await _repository.ReadModAsync(folder.Path, cancellationToken).ConfigureAwait(false);

                rows.Add(BuildWrapperRow(wrapper, decided, destination));
                continue;
            }

            foreach (var (mod, decision) in decided)
            {
                rows.Add(BuildRow(mod, decision, gameData));
            }
        }

        foreach (var mod in inventory.UnfiledMods)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (_, decision) = await DecideAsync(mod, index, diagnostics, cancellationToken).ConfigureAwait(false);

            rows.Add(BuildRow(mod, decision, gameData));
        }

        var plan = new SortRunPlan
        {
            ModsDirectory = inventory.ModsDirectory,
            GameId = gameData.GameId,
            Rows = rows,
            Diagnostics = diagnostics,
        };

        _logger.Information(
            "Planned a sort of {ModsDirectory}: {Rows} mods, {Moves} would move",
            plan.ModsDirectory,
            rows.Count,
            plan.Moves.Count);

        return plan;
    }

    /// <inheritdoc />
    public async Task<SortRunResult> ApplyAsync(
        string modsDirectory,
        IReadOnlyList<SortRunRow> rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);
        ArgumentNullException.ThrowIfNull(rows);

        var root = PathComparer.Normalize(modsDirectory);
        var runId = Guid.NewGuid().ToString("n")[..12];
        var startedAt = _time.GetUtcNow();
        var outcomes = new List<SortRunOutcome>(rows.Count);
        var journalled = new List<SortJournalEntry>();

        // A mod named like its destination folder moves first, before others are filed into it.
        var ordered = rows
            .OrderByDescending(row => row.WillMove && PathComparer.AreEqual(
                row.Mod.Path,
                Path.Combine(root, row.DestinationFolderName)))
            .ToList();

        try
        {
            foreach (var row in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!row.WillMove)
                {
                    continue;
                }

                var destination = Path.Combine(root, row.DestinationFolderName);

                // Asked before the move: afterwards the folder always exists.
                var created = !PathComparer.TryResolveExisting(destination, out var existing) ||
                              !Directory.Exists(existing);

                try
                {
                    var result = await _files
                        .MoveAsync(row.Mod.Path, destination, name: null, cancellationToken)
                        .ConfigureAwait(false);

                    outcomes.Add(new SortRunOutcome(row, result, null));

                    if (!result.Changed)
                    {
                        continue;
                    }

                    journalled.Add(new SortJournalEntry
                    {
                        RunId = runId,
                        At = _time.GetUtcNow(),
                        Kind = SortJournalEntryKind.Move,
                        From = Relative(root, result.FromPath),
                        To = Relative(root, result.ToPath),
                        Decision = row.Decision with { Root = Relative(root, result.ToPath) },
                        CreatedFolder = created ? row.DestinationFolderName : null,
                    });

                    await RecordSortResultAsync(result.ToPath, row, startedAt, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (ModOperationException ex)
                {
                    // One mod that cannot move must not cost the others.
                    outcomes.Add(new SortRunOutcome(row, null, ex.Message));
                    _logger.Warning(ex, "Could not move {Mod} to {Destination}", row.Mod.Path, destination);
                }
            }
        }
        finally
        {
            // Written even when stopped part-way: it is what makes the run undoable.
            await _journal.AppendAsync(root, journalled, CancellationToken.None).ConfigureAwait(false);
        }

        _logger.Information(
            "Sort run {RunId} moved {Moved} of {Rows} mods in {ModsDirectory}",
            runId,
            journalled.Count,
            rows.Count,
            root);

        return new SortRunResult
        {
            RunId = runId,
            At = startedAt,
            Outcomes = outcomes,
        };
    }

    /// <inheritdoc />
    public async Task<SortUndoResult> UndoAsync(
        string modsDirectory,
        string? runId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);

        var root = PathComparer.Normalize(modsDirectory);
        var entries = await _journal.ReadAsync(root, cancellationToken).ConfigureAwait(false);
        var runs = await _journal.ListRunsAsync(root, cancellationToken).ConfigureAwait(false);

        var run = runId is { Length: > 0 }
            ? runs.FirstOrDefault(candidate => string.Equals(candidate.RunId, runId, StringComparison.Ordinal))
              ?? throw new ModOperationException(
                  $"There is no sort run '{runId}' in the journal at " +
                  $"'{_journal.GetJournalPath(root)}'.",
                  root)
            : runs.FirstOrDefault(candidate => !candidate.IsFullyUndone)
              ?? throw new ModOperationException(
                  "There is no sort run left to undo in this Mods folder.", root);

        var moves = entries
            .Where(entry => entry.Kind == SortJournalEntryKind.Move &&
                            string.Equals(entry.RunId, run.RunId, StringComparison.Ordinal))
            .Reverse()
            .ToList();

        var outcomes = new List<SortUndoOutcome>(moves.Count);
        var journalled = new List<SortJournalEntry>();
        var undoRunId = Guid.NewGuid().ToString("n")[..12];

        try
        {
            foreach (var move in moves)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // A place outside the Mods folder, from an edited journal, is left alone.
                if (!UntrustedLocation.TryResolveInside(root, move.To, out var current)
                    || !UntrustedLocation.TryResolveInside(root, move.From, out var original))
                {
                    outcomes.Add(new SortUndoOutcome(
                        move.To,
                        move.From,
                        false,
                        "The sort's record names a place outside your Mods folder, so it was left alone."));
                    continue;
                }

                var originalParent = Path.GetDirectoryName(original)!;
                var originalName = Path.GetFileName(original);

                if (!Directory.Exists(current))
                {
                    outcomes.Add(new SortUndoOutcome(
                        move.To,
                        move.From,
                        false,
                        "It is no longer there — it has been moved, renamed or deleted since the sort."));
                    continue;
                }

                // A mod sorted into a folder of its own name: undo may remove that folder, if it holds only this mod.
                var isOwnCharacterFolder =
                    PathComparer.AreEqual(Path.GetDirectoryName(current), original) &&
                    Directory.Exists(original) &&
                    Directory.EnumerateFileSystemEntries(original).Count() == 1;

                if (Directory.Exists(original) && !isOwnCharacterFolder)
                {
                    outcomes.Add(new SortUndoOutcome(
                        move.To,
                        move.From,
                        false,
                        $"Something else is at '{PathDisplay.Show(move.From)}' now. XXSM will not overwrite it."));
                    continue;
                }

                try
                {
                    var result = await _files
                        .MoveAsync(current, originalParent, originalName, cancellationToken)
                        .ConfigureAwait(false);

                    outcomes.Add(new SortUndoOutcome(move.To, Relative(root, result.ToPath), true, null));

                    journalled.Add(new SortJournalEntry
                    {
                        RunId = undoRunId,
                        At = _time.GetUtcNow(),
                        Kind = SortJournalEntryKind.Undo,
                        From = move.To,
                        To = Relative(root, result.ToPath),
                        Undoes = run.RunId,
                    });
                }
                catch (ModOperationException ex)
                {
                    outcomes.Add(new SortUndoOutcome(move.To, move.From, false, ex.Message));
                    _logger.Warning(ex, "Could not undo the move of {Mod}", current);
                }
            }
        }
        finally
        {
            // Written even when stopped part-way, so nothing is offered for undo twice.
            await _journal.AppendAsync(root, journalled, CancellationToken.None).ConfigureAwait(false);
        }

        RemoveFoldersTheRunCreated(root, moves);

        _logger.Information(
            "Undid sort run {RunId}: {Restored} of {Moves} mods put back",
            run.RunId,
            journalled.Count,
            moves.Count);

        return new SortUndoResult
        {
            RunId = run.RunId,
            Outcomes = outcomes,
        };
    }

    /// <summary>Removes the character folders the undone run created, non-recursively, once empty.</summary>
    private void RemoveFoldersTheRunCreated(string root, IReadOnlyList<SortJournalEntry> moves)
    {
        foreach (var name in moves
                     .Select(move => move.CreatedFolder)
                     .Where(folder => folder is { Length: > 0 })
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!UntrustedLocation.TryResolveInside(root, name, out var folder))
            {
                _logger.Warning("Not removing {Folder}: the sort's record names it outside the Mods folder {Root}", name, root);
                continue;
            }

            try
            {
                if (!PathComparer.TryResolveExisting(folder, out var existing) ||
                    !Directory.Exists(existing) ||
                    Directory.EnumerateFileSystemEntries(existing).Any())
                {
                    continue;
                }

                Directory.Delete(existing, recursive: false);

                _logger.Information("Removed {Folder}, which this run had created", existing);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Never worth failing an undo over: every mod is already back.
                _logger.Warning(ex, "Could not remove the empty character folder {Folder}", folder);
            }
        }
    }

    /// <summary>One mod and where the sorter thinks it belongs.</summary>
    private readonly record struct DecidedMod(InstalledMod Mod, SortDecision Decision);

    private async Task<DecidedMod> DecideAsync(
        InstalledMod mod,
        SortIndex index,
        List<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var signals = await _signals.ExtractAsync(mod.Path, null, cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(signals.Diagnostics);

        return new DecidedMod(
            mod,
            _sorter.Sort(index, signals, new SortRequest(mod.VariantOverride, mod.Name)));
    }

    /// <summary>The character folder a top-level folder should move into whole, or null if it should not.</summary>
    /// <remarks>A folder that is not a character and whose mods agree moves whole; one nobody identifies goes to
    /// <c>Others</c>.</remarks>
    private static string? WrapperDestination(
        VariantFolder folder,
        IReadOnlyList<DecidedMod> decided,
        GameData gameData)
    {
        if (gameData.Find(folder.Name) is not null ||
            PathComparer.AreNamesEqual(folder.Name, ModsFolderLayout.UnsortedFolderName))
        {
            return null;
        }

        if (decided.Count == 0)
        {
            return null;
        }

        // A person's own filing is never overridden, nor dragged along by a folder move.
        if (decided.Any(entry => entry.Decision.DecidedBy == SortDecidedBy.Manual))
        {
            return null;
        }

        string? destination = null;

        foreach (var (_, decision) in decided)
        {
            if (!decision.IsResolved || decision.VariantId is not { Length: > 0 } variantId)
            {
                continue;
            }

            var candidate = ResolveFolderName(variantId, gameData);

            if (destination is null)
            {
                destination = candidate;
            }
            else if (!PathComparer.AreNamesEqual(destination, candidate))
            {
                // Two characters in one folder: splitting it is the lesser evil.
                return null;
            }
        }

        if (destination is not null && IsTheSameCharacterUnderAnotherName(folder.Name, destination, gameData))
        {
            return null;
        }

        return destination ?? ModsFolderLayout.UnsortedFolderName;
    }

    /// <summary>Whether a folder is the destination character's folder under another spelling.</summary>
    private static bool IsTheSameCharacterUnderAnotherName(
        string folderName, string destination, GameData gameData)
    {
        if (SortName.Normalize(folderName) is not { Length: > 0 } normalised)
        {
            return false;
        }

        var variant = gameData.Variants.FirstOrDefault(
            candidate => PathComparer.AreNamesEqual(
                ResolveFolderName(candidate.InternalName, gameData), destination));

        if (variant is null)
        {
            return false;
        }

        foreach (var name in Names(variant))
        {
            if (string.Equals(SortName.Normalize(name), normalised, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Every name a variant answers to.</summary>
    private static IEnumerable<string> Names(MergedVariant variant)
    {
        yield return variant.InternalName;
        yield return variant.DisplayName;
        yield return variant.ModFilesName;

        foreach (var alias in variant.Aliases)
        {
            yield return alias;
        }
    }

    private static SortRunRow BuildWrapperRow(
        InstalledMod wrapper,
        IReadOnlyList<DecidedMod> decided,
        string destination)
    {
        var identified = decided.Count(entry => entry.Decision.IsResolved);

        var best = decided
            .OrderByDescending(entry => entry.Decision.IsResolved)
            .ThenByDescending(entry => entry.Decision.Confidence)
            .First()
            .Decision;

        var reason = identified switch
        {
            0 => $"Nothing in this folder could be identified, and it is not a character. " +
                 $"The whole folder goes to {destination}, contents intact.",

            _ when identified == decided.Count =>
                $"Everything in this folder is {destination}. The folder moves whole, so its " +
                "readme and preview images stay with the mods they describe.",

            _ => $"{identified} of the {decided.Count} mods in this folder are {destination}, and " +
                 "nothing in it points anywhere else. The folder moves whole, so its readme and " +
                 "preview images stay with the mods they describe.",
        };

        return new SortRunRow
        {
            Mod = wrapper with { VariantFolderName = null },
            Decision = best,
            DestinationFolderName = destination,
            Action = SortRowAction.Move,
            Reason = reason,
        };
    }

    private static SortRunRow BuildRow(InstalledMod mod, SortDecision decision, GameData gameData)
    {
        var destination = decision.VariantId is { Length: > 0 } variantId
            ? ResolveFolderName(variantId, gameData)
            : ModsFolderLayout.UnsortedFolderName;

        var alreadyThere = mod.VariantFolderName is { Length: > 0 } current &&
                           PathComparer.AreNamesEqual(current, destination);

        var (action, reason) = decision switch
        {
            { DecidedBy: SortDecidedBy.Manual } when alreadyThere =>
                (SortRowAction.ManuallyFiled, "You filed this mod here."),

            { DecidedBy: SortDecidedBy.Manual } =>
                (SortRowAction.Move, $"You filed this mod under {destination}."),

            _ when alreadyThere =>
                (SortRowAction.AlreadyFiled, "Already filed correctly."),

            { IsResolved: false } when mod.VariantFolderName is { Length: > 0 } =>
                (SortRowAction.UnidentifiedButFiled,
                    "Could not be identified, and is already filed under " +
                    $"'{mod.VariantFolderName}'. Left where it is."),

            _ => (SortRowAction.Move, decision.Explanation),
        };

        return new SortRunRow
        {
            Mod = mod,
            Decision = decision,
            DestinationFolderName = action == SortRowAction.UnidentifiedButFiled
                ? mod.VariantFolderName!
                : destination,
            Action = action,
            Reason = reason,
        };
    }

    /// <summary>Maps a variant to its folder: its <see cref="MergedVariant.ModFilesName"/>.</summary>
    private static string ResolveFolderName(string variantId, GameData gameData) =>
        gameData.Find(variantId)?.ModFilesName ?? variantId;

    private static string Relative(string root, string path) =>
        PathComparer.TryGetRelativePath(root, path) ?? PathComparer.Normalize(path);

    /// <summary>Writes the audit trail into the mod's own <c>.xxsm/mod.json</c>; a failure is not fatal.</summary>
    private async Task RecordSortResultAsync(
        string modPath, SortRunRow row, DateTimeOffset at, CancellationToken cancellationToken)
    {
        try
        {
            await _configs.UpdateAsync(
                    modPath,
                    config => config with
                    {
                        SortResult = new ModSortResult
                        {
                            DecidedBy = row.Decision.DecidedBy.ToString().ToLowerInvariant(),
                            Confidence = Math.Round(row.Decision.Confidence, 4, MidpointRounding.AwayFromZero),
                            MatchedVariant = row.Decision.VariantId,
                            RunnerUp = row.Decision.RunnerUpVariantId,
                            ExtractedHashes = row.Decision.ExtractedHashes,
                            SortedAt = at,
                            VariantIsUncertain = row.VariantIsUncertain,
                        },
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ModOperationException ex)
        {
            _logger.Warning(
                ex,
                "Moved {Mod} but could not record why in its {File}",
                modPath,
                Path.Combine(ModConfigSchema.DirectoryName, ModConfigSchema.FileName));
        }
    }
}
