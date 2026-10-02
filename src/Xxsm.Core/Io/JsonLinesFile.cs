using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Serilog;

namespace Xxsm.Core.Io;

/// <summary>Appends to a JSON-lines file, one object per line, such as the sort and switch journals.</summary>
public static class JsonLinesFile
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Appends <paramref name="lines"/>, creating the file, and first ends a torn last line.</summary>
    /// <param name="path">The file. Its directory must exist.</param>
    /// <param name="lines">Whole lines, each ending in <c>\n</c>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="IOException">The file could not be opened or written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be written.</exception>
    public static async Task AppendAsync(string path, string lines, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(lines);

        // One append at a time across programs, on a lock file beside the journal, which readers must still open.
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } folder && !Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"Could not find a part of the path '{PathDisplay.Show(path)}'.");
        }

        using var turn = await FileLock.AcquireAsync(path + ".lock", cancellationToken).ConfigureAwait(false);

        await using var stream = new FileStream(
            path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, bufferSize: 4096, useAsync: true);

        var text = lines;

        if (stream.Length > 0)
        {
            var last = new byte[1];
            stream.Seek(-1, SeekOrigin.End);
            await stream.ReadExactlyAsync(last, cancellationToken).ConfigureAwait(false);

            if (last[0] != (byte)'\n')
            {
                text = "\n" + lines;
            }
        }

        stream.Seek(0, SeekOrigin.End);
        await stream.WriteAsync(Utf8.GetBytes(text), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Appends records, one compact JSON object per line, creating the file and its folder.</summary>
    /// <exception cref="IOException">The file could not be opened or written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be written.</exception>
    public static async Task AppendAsync<T>(
        string path, IEnumerable<T> records, JsonTypeInfo<T> type, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(type);

        var text = new StringBuilder();

        foreach (var record in records)
        {
            text.Append(JsonSerializer.Serialize(record, type)).Append('\n');
        }

        if (text.Length == 0)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        await AppendAsync(path, text.ToString(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads every record; a missing file has none, and a blank or torn line is skipped.</summary>
    /// <param name="path">The file.</param>
    /// <param name="type">How a line is read.</param>
    /// <param name="logger">Where a skipped torn line is logged; null says nothing.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be read.</exception>
    public static async Task<IReadOnlyList<T>> ReadAsync<T>(
        string path, JsonTypeInfo<T> type, ILogger? logger, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(type);

        if (!PathComparer.TryResolveExisting(path, out var resolved) || !File.Exists(resolved))
        {
            return [];
        }

        var lines = await File.ReadAllLinesAsync(resolved, cancellationToken).ConfigureAwait(false);
        var records = new List<T>(lines.Length);

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize(line, type) is { } record)
                {
                    records.Add(record);
                }
            }
            catch (JsonException ex)
            {
                // One torn line must not hide the records around it.
                logger?.Warning(ex, "Skipping an unreadable line in {Path}", resolved);
            }
        }

        return records;
    }

    /// <summary>Groups a journal's entries into runs, newest first by where each run first appears.</summary>
    /// <param name="entries">The journal, oldest first.</param>
    /// <param name="runId">The run an entry belongs to.</param>
    /// <param name="isAction">Whether an entry is one of its run's actions rather than an undo.</param>
    /// <param name="undoes">The run an undo entry undid one action of, or null.</param>
    /// <returns>Each run that has actions, with them and how many of them were undone.</returns>
    public static IReadOnlyList<JournalRun<T>> Runs<T>(
        IEnumerable<T> entries, Func<T, string> runId, Func<T, bool> isAction, Func<T, string?> undoes)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(isAction);
        ArgumentNullException.ThrowIfNull(undoes);

        var actions = new Dictionary<string, List<T>>(StringComparer.Ordinal);
        var undone = new Dictionary<string, int>(StringComparer.Ordinal);

        // Position in the file decides "newest": two runs in one second share a time.
        var firstSeen = new Dictionary<string, int>(StringComparer.Ordinal);
        var position = 0;

        foreach (var entry in entries)
        {
            var id = runId(entry);
            firstSeen.TryAdd(id, ++position);

            if (isAction(entry))
            {
                if (!actions.TryGetValue(id, out var list))
                {
                    actions[id] = list = [];
                }

                list.Add(entry);
            }
            else if (undoes(entry) is { Length: > 0 } target)
            {
                undone[target] = undone.GetValueOrDefault(target) + 1;
            }
        }

        return
        [
            .. actions
                .Select(pair => new JournalRun<T>(pair.Key, pair.Value, undone.GetValueOrDefault(pair.Key)))
                .OrderByDescending(run => firstSeen[run.RunId]),
        ];
    }
}

/// <summary>One run in a journal: its actions, oldest first, and how many of them were undone since.</summary>
public sealed record JournalRun<T>(string RunId, IReadOnlyList<T> Entries, int Undone);
