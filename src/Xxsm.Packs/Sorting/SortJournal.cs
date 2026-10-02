using Serilog;
using Xxsm.Core;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Packs.Serialization;

namespace Xxsm.Packs.Sorting;

/// <summary>The default <see cref="ISortJournal"/>: a JSON-lines file inside the Mods folder.</summary>
public sealed class SortJournal(ILogger logger) : ISortJournal
{
    /// <summary>The journal's name inside the Mods folder's state directory.</summary>
    public const string FileName = "sort-log.jsonl";

    private readonly ILogger _logger = logger.ForContext<SortJournal>();

    /// <inheritdoc />
    public string GetJournalPath(string modsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);

        return PathComparer.Normalize(
            Path.Combine(modsDirectory, ModsFolderLayout.StateDirectoryName, FileName));
    }

    /// <inheritdoc />
    public async Task AppendAsync(
        string modsDirectory,
        IReadOnlyList<SortJournalEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var path = GetJournalPath(modsDirectory);

        if (entries.Count == 0)
        {
            return;
        }

        try
        {
            await JsonLinesFile.AppendAsync(path, entries, SortJournalJsonContext.Default.SortJournalEntry, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException(
                $"Could not write the sort journal at '{PathDisplay.Show(path)}': {ex.Message}. " +
                "Without it this run cannot be undone.",
                path,
                ex);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SortJournalEntry>> ReadAsync(
        string modsDirectory, CancellationToken cancellationToken = default)
    {
        var path = GetJournalPath(modsDirectory);

        try
        {
            return await JsonLinesFile.ReadAsync(path, SortJournalJsonContext.Default.SortJournalEntry, _logger, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException($"Could not read the sort journal at '{PathDisplay.Show(path)}': {ex.Message}", path, ex);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SortRunSummary>> ListRunsAsync(
        string modsDirectory, CancellationToken cancellationToken = default)
    {
        var entries = await ReadAsync(modsDirectory, cancellationToken).ConfigureAwait(false);

        return
        [
            .. JsonLinesFile
                .Runs(entries, entry => entry.RunId, entry => entry.Kind == SortJournalEntryKind.Move, entry => entry.Undoes)
                .Select(run => new SortRunSummary(run.RunId, run.Entries[0].At, run.Entries.Count, run.Undone)),
        ];
    }
}
