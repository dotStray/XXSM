using Serilog;
using Xxsm.Core.Io;
using Xxsm.Core.Serialization;

namespace Xxsm.Core.Mods;

/// <summary>The default <see cref="IModSwitcher"/>: renames, and journals in the Mods folder.</summary>
public sealed class ModSwitcher(IModFileOperations files, TimeProvider time, ILogger logger) : IModSwitcher
{
    /// <summary>The journal's name inside the Mods folder's state directory.</summary>
    public const string FileName = "switch-log.jsonl";

    private readonly IModFileOperations _files = files;
    private readonly TimeProvider _time = time;
    private readonly ILogger _logger = logger.ForContext<ModSwitcher>();

    /// <inheritdoc />
    public string GetJournalPath(string modsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);

        return PathComparer.Normalize(
            Path.Combine(modsDirectory, ModsFolderLayout.StateDirectoryName, FileName));
    }

    /// <inheritdoc />
    public async Task<ModSwitchRunResult> ApplyAsync(
        string modsDirectory,
        IReadOnlyList<ModSwitch> switches,
        ModSwitchSource source,
        string? label,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);
        ArgumentNullException.ThrowIfNull(switches);

        var root = PathComparer.Normalize(modsDirectory);

        foreach (var request in switches)
        {
            if (!PathComparer.IsSameOrUnder(root, request.ModFolder) || PathComparer.AreEqual(root, request.ModFolder))
            {
                throw new ModOperationException(
                    $"'{PathDisplay.Show(request.ModFolder)}' is not a mod inside '{PathDisplay.Show(root)}'. Nothing has been switched.",
                    request.ModFolder);
            }
        }

        var runId = Guid.NewGuid().ToString("n")[..12];
        var startedAt = _time.GetUtcNow();
        var outcomes = new List<ModSwitchOutcome>(switches.Count);
        var journalled = new List<SwitchJournalEntry>();

        try
        {
            foreach (var request in switches)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var result = await _files
                        .SetEnabledAsync(request.ModFolder, request.Enable, cancellationToken)
                        .ConfigureAwait(false);

                    outcomes.Add(new ModSwitchOutcome(request, result, null));

                    if (result.Changed)
                    {
                        journalled.Add(new SwitchJournalEntry
                        {
                            RunId = runId,
                            At = _time.GetUtcNow(),
                            Kind = SwitchJournalEntryKind.Switch,
                            Source = source,
                            Label = label,
                            From = Relative(root, result.FromPath),
                            To = Relative(root, result.ToPath),
                        });
                    }
                }
                catch (ModOperationException ex)
                {
                    outcomes.Add(new ModSwitchOutcome(request, null, ex.Message));
                    _logger.Warning(ex, "Could not switch {Mod} {State}", request.ModFolder, request.Enable ? "on" : "off");
                }
            }
        }
        finally
        {
            // Written even when cancelled part-way: what was renamed has to stay undoable.
            await AppendAsync(root, journalled, CancellationToken.None).ConfigureAwait(false);
        }

        _logger.Information(
            "Switch run {RunId} ({Source} {Label}) renamed {Changed} of {Requested} mods in {ModsDirectory}",
            runId,
            source,
            label,
            journalled.Count,
            switches.Count,
            root);

        return new ModSwitchRunResult
        {
            RunId = runId,
            At = startedAt,
            Outcomes = outcomes,
        };
    }

    /// <inheritdoc />
    public async Task<ModSwitchUndoResult> UndoAsync(
        string modsDirectory,
        string? runId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);

        var root = PathComparer.Normalize(modsDirectory);
        var entries = await ReadAsync(root, cancellationToken).ConfigureAwait(false);
        var runs = Summarise(entries);

        var run = runId is { Length: > 0 }
            ? runs.FirstOrDefault(candidate => string.Equals(candidate.RunId, runId, StringComparison.Ordinal))
              ?? throw new ModOperationException(
                  $"There is no run '{runId}' in the switch journal at '{GetJournalPath(root)}'.", root)
            : runs.FirstOrDefault(candidate => !candidate.IsFullyUndone)
              ?? throw new ModOperationException(
                  "There is nothing left to undo: no profile or randomiser run in this Mods folder " +
                  "has switched a mod that is not already back.",
                  root);

        // Exact on purpose: Klee/Red and klee/Red are two mods on ext4.
        var undone = new HashSet<string>(
            entries
                .Where(entry => entry.Kind == SwitchJournalEntryKind.Undo &&
                                string.Equals(entry.Undoes, run.RunId, StringComparison.Ordinal))
                .Select(entry => entry.From),
            StringComparer.Ordinal);

        var switches = entries
            .Where(entry => entry.Kind == SwitchJournalEntryKind.Switch &&
                            string.Equals(entry.RunId, run.RunId, StringComparison.Ordinal))
            .Reverse()
            .ToList();

        var outcomes = new List<ModSwitchUndoOutcome>(switches.Count);
        var journalled = new List<SwitchJournalEntry>();
        var undoRunId = Guid.NewGuid().ToString("n")[..12];

        try
        {
            foreach (var entry in switches)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (undone.Contains(entry.To))
                {
                    outcomes.Add(new ModSwitchUndoOutcome(entry.To, entry.From, true, null));
                    continue;
                }

                outcomes.Add(await UndoOneAsync(root, entry, run.RunId, undoRunId, journalled, cancellationToken)
                    .ConfigureAwait(false));
            }
        }
        finally
        {
            await AppendAsync(root, journalled, CancellationToken.None).ConfigureAwait(false);
        }

        _logger.Information(
            "Undid switch run {RunId}: {Restored} of {Switches} mods put back",
            run.RunId,
            journalled.Count,
            switches.Count);

        return new ModSwitchUndoResult
        {
            RunId = run.RunId,
            Outcomes = outcomes,
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModSwitchRunSummary>> ListRunsAsync(
        string modsDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);

        var entries = await ReadAsync(PathComparer.Normalize(modsDirectory), cancellationToken).ConfigureAwait(false);

        return Summarise(entries);
    }

    private async Task<ModSwitchUndoOutcome> UndoOneAsync(
        string root,
        SwitchJournalEntry entry,
        string runId,
        string undoRunId,
        List<SwitchJournalEntry> journalled,
        CancellationToken cancellationToken)
    {
        // The journal can be edited: a place it names outside the Mods folder is left alone.
        if (!UntrustedLocation.TryResolveInside(root, entry.To, out var current)
            || !UntrustedLocation.TryResolveInside(root, entry.From, out var original))
        {
            return new ModSwitchUndoOutcome(
                entry.To,
                entry.From,
                false,
                "The record names a place outside your Mods folder, so it was left alone.");
        }

        if (!PathComparer.TryResolveExisting(current, out var resolved) || !Directory.Exists(resolved))
        {
            return new ModSwitchUndoOutcome(
                entry.To,
                entry.From,
                false,
                "It is no longer there — it has been moved, renamed or deleted since.");
        }

        if (PathComparer.TryResolveExisting(original, out var occupant) && Directory.Exists(occupant) &&
            !PathComparer.AreEqual(occupant, resolved))
        {
            return new ModSwitchUndoOutcome(
                entry.To,
                entry.From,
                false,
                $"Something else is at '{entry.From}' now. XXSM will not overwrite it.");
        }

        try
        {
            var result = await _files
                .SetEnabledAsync(resolved, !ModsFolderLayout.IsDisabled(Path.GetFileName(original)), cancellationToken)
                .ConfigureAwait(false);

            var restoredTo = Relative(root, result.ToPath);

            journalled.Add(new SwitchJournalEntry
            {
                RunId = undoRunId,
                At = _time.GetUtcNow(),
                Kind = SwitchJournalEntryKind.Undo,
                From = entry.To,
                To = restoredTo,
                Undoes = runId,
            });

            return new ModSwitchUndoOutcome(entry.To, restoredTo, true, null);
        }
        catch (ModOperationException ex)
        {
            _logger.Warning(ex, "Could not undo the switch of {Mod}", resolved);

            return new ModSwitchUndoOutcome(entry.To, entry.From, false, ex.Message);
        }
    }

    private static List<ModSwitchRunSummary> Summarise(IReadOnlyList<SwitchJournalEntry> entries) =>
    [
        .. JsonLinesFile
            .Runs(entries, entry => entry.RunId, entry => entry.Kind == SwitchJournalEntryKind.Switch, entry => entry.Undoes)
            .Select(run => new ModSwitchRunSummary(
                run.RunId,
                run.Entries[0].At,
                run.Entries[0].Source ?? ModSwitchSource.Profile,
                run.Entries[0].Label,
                run.Entries.Count,
                run.Undone)),
    ];

    private async Task AppendAsync(
        string root, List<SwitchJournalEntry> entries, CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var path = GetJournalPath(root);

        try
        {
            await JsonLinesFile.AppendAsync(path, entries, SwitchJournalJsonContext.Default.SwitchJournalEntry, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException(
                $"Could not write the switch journal at '{PathDisplay.Show(path)}': {ex.Message}. " +
                "The mods have been switched, but this run cannot be undone.",
                path,
                ex);
        }
    }

    private async Task<IReadOnlyList<SwitchJournalEntry>> ReadAsync(string root, CancellationToken cancellationToken)
    {
        var path = GetJournalPath(root);

        try
        {
            return await JsonLinesFile.ReadAsync(path, SwitchJournalJsonContext.Default.SwitchJournalEntry, _logger, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException($"Could not read the switch journal at '{PathDisplay.Show(path)}': {ex.Message}", path, ex);
        }
    }

    private static string Relative(string root, string path) =>
        PathComparer.TryGetRelativePath(root, path) ?? PathComparer.Normalize(path);
}
