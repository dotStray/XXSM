using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using Xxsm.Core;
using Xxsm.Core.Io;
using Xxsm.Packs.Serialization;

namespace Xxsm.Packs.Downloads;

/// <summary>The whole file: a version and the entries.</summary>
public sealed record DownloadFile
{
    /// <summary>The format's version, so a later one can be recognised rather than guessed at.</summary>
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    /// <summary>The downloads, newest first.</summary>
    [JsonPropertyName("downloads")]
    public IReadOnlyList<DownloadRecord> Downloads { get; init; } = [];
}

/// <summary>Reads and writes the download list; every write reads the file again and merges by id.</summary>
public interface IDownloadStore
{
    /// <summary>Where the list lives.</summary>
    string Path { get; }

    /// <summary>Reads the list.</summary>
    /// <returns>Every entry, newest first. Empty when there is no file yet.</returns>
    /// <exception cref="ModOperationException">The file exists and could not be read.</exception>
    Task<IReadOnlyList<DownloadRecord>> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds or replaces entries, keeping everything else the file already holds.</summary>
    /// <param name="records">The entries to write.</param>
    /// <param name="keep">How many entries to keep altogether, newest first.</param>
    /// <param name="cancellationToken">Cancels before anything is written.</param>
    /// <exception cref="ModOperationException">The list could not be written.</exception>
    Task<IReadOnlyList<DownloadRecord>> SaveAsync(
        IReadOnlyList<DownloadRecord> records, int keep, CancellationToken cancellationToken = default);

    /// <summary>Removes entries by id.</summary>
    /// <returns>The list as it now stands, newest first.</returns>
    /// <exception cref="ModOperationException">The list could not be written.</exception>
    Task<IReadOnlyList<DownloadRecord>> RemoveAsync(
        IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default);
}

/// <summary>The default <see cref="IDownloadStore"/>.</summary>
public sealed class DownloadStore(IAppPaths paths, ILogger logger) : IDownloadStore, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _logger = logger.ForContext<DownloadStore>();

    /// <inheritdoc />
    public string Path { get; } = paths.DownloadHistoryFile;

    /// <inheritdoc />
    public async Task<IReadOnlyList<DownloadRecord>> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DownloadRecord>> SaveAsync(
        IReadOnlyList<DownloadRecord> records, int keep, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);

        return await MutateAsync(
            existing =>
            {
                var merged = new List<DownloadRecord>(records);

                merged.AddRange(existing.Where(entry => !records.Any(record => record.Id == entry.Id)));

                return [.. merged
                    .OrderByDescending(entry => entry.StartedAt)
                    .Take(Math.Max(keep, records.Count))];
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DownloadRecord>> RemoveAsync(
        IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        return await MutateAsync(
            existing => [.. existing.Where(entry => !ids.Contains(entry.Id, StringComparer.Ordinal))],
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Releases the gate that serialises writes.</summary>
    public void Dispose() => _gate.Dispose();

    private async Task<IReadOnlyList<DownloadRecord>> MutateAsync(
        Func<IReadOnlyList<DownloadRecord>, IReadOnlyList<DownloadRecord>> change,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Read inside the lock just before writing: the command line writes this file too.
            var existing = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var updated = change(existing);

            await WriteAsync(updated, cancellationToken).ConfigureAwait(false);

            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<DownloadRecord>> ReadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!PathComparer.TryResolveExisting(Path, out var resolved) || !File.Exists(resolved))
        {
            return [];
        }

        string text;

        try
        {
            text = await File.ReadAllTextAsync(resolved, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException(
                $"Could not read the download list at '{PathDisplay.Show(resolved)}': {ex.Message}", resolved, ex);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        try
        {
            var file = JsonSerializer.Deserialize(text, DownloadsJsonContext.Default.DownloadFile);

            return [.. (file?.Downloads ?? []).OfType<DownloadRecord>().OrderByDescending(entry => entry.StartedAt)];
        }
        catch (JsonException ex)
        {
            // Kept aside before the next save writes over it.
            var aside = $"{resolved}.corrupt-{DateTime.UtcNow:yyyyMMdd'T'HHmmss}";

            try
            {
                File.Copy(resolved, aside, overwrite: false);
            }
            catch (Exception copy) when (copy is IOException or UnauthorizedAccessException)
            {
                _logger.Warning(copy, "Could not keep the unreadable download list aside at {Aside}", aside);
            }

            _logger.Warning(ex, "The download list at {Path} could not be read; kept aside at {Aside}, starting a new one", resolved, aside);

            return [];
        }
    }

    private async Task WriteAsync(IReadOnlyList<DownloadRecord> records, CancellationToken cancellationToken)
    {
        var text = JsonSerializer.Serialize(
            new DownloadFile { Downloads = records }, DownloadsJsonContext.Default.DownloadFile);

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

            await AtomicFile.WriteAllTextAsync(Path, text, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException(
                $"Could not write the download list at '{PathDisplay.Show(Path)}': {ex.Message}", Path, ex);
        }
    }
}
