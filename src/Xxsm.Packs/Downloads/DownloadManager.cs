using System.Globalization;
using Serilog;
using Xxsm.Core;
using Xxsm.Core.GameBanana;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Core.Settings;
using Xxsm.Packs.GameBanana;

namespace Xxsm.Packs.Downloads;

/// <summary>The default <see cref="IDownloadManager"/>.</summary>
public sealed class DownloadManager(
    IGameBananaClient client,
    IGameBananaInstallSource source,
    IDownloadStore store,
    IAppSettingsStore settings,
    IAppPaths paths,
    ILogger logger,
    TimeProvider? time = null) : IDownloadManager, IDisposable
{
    private readonly IGameBananaClient _client = client;
    private readonly IGameBananaInstallSource _source = source;
    private readonly IDownloadStore _store = store;
    private readonly IAppSettingsStore _settings = settings;
    private readonly ILogger _logger = logger.ForContext<DownloadManager>();
    private readonly string _downloads = paths.DownloadsDirectory;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private readonly Lock _gate = new();
    private readonly List<DownloadJob> _jobs = [];

    private bool _loaded;
    private bool _disposed;

    /// <inheritdoc />
    public event EventHandler<DownloadChange>? Changed;

    /// <inheritdoc />
    public event EventHandler<DownloadChange>? Progress;

    /// <inheritdoc />
    public IReadOnlyList<DownloadJob> Jobs
    {
        get
        {
            lock (_gate)
            {
                return [.. _jobs.OrderByDescending(job => job.Record.StartedAt)];
            }
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DownloadJob>> LoadAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_loaded)
            {
                return [.. _jobs.OrderByDescending(job => job.Record.StartedAt)];
            }
        }

        var records = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        var recovered = new List<DownloadRecord>();

        foreach (var record in records)
        {
            // A Running entry on disk means the application stopped mid-download.
            var restored = record.IsRunning
                ? record with
                {
                    State = DownloadState.Cancelled,
                    Error = "XXSM closed before this finished.",
                    FinishedAt = record.FinishedAt ?? _time.GetUtcNow(),
                }
                : record;

            if (!ReferenceEquals(restored, record))
            {
                recovered.Add(restored);
            }

            lock (_gate)
            {
                if (!_jobs.Any(job => job.Id == restored.Id))
                {
                    _jobs.Add(new DownloadJob(restored, _time));
                }
            }
        }

        lock (_gate)
        {
            _loaded = true;
        }

        if (recovered.Count > 0)
        {
            await _store.SaveAsync(recovered, await KeepCountAsync(cancellationToken).ConfigureAwait(false), cancellationToken)
                .ConfigureAwait(false);
        }

        await PruneAsync(cancellationToken).ConfigureAwait(false);

        return Jobs;
    }

    /// <inheritdoc />
    public DownloadJob Start(
        GameBananaMod page, GameBananaFile? file = null, DownloadRequest request = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var chosen = page.FileToTake(file);

        var record = new DownloadRecord
        {
            Id = Guid.NewGuid().ToString("n"),
            ModId = page.ModId,
            Name = page.Name ?? chosen.File,
            Author = page.Author,
            Version = page.Version,
            Description = page.Description,
            PageUrl = page.PageUrl.AbsoluteUri,
            PictureUrl = page.PreviewImageUrl?.AbsoluteUri,
            DateModifiedTs = page.DateModifiedTs,
            FileId = chosen.IdRow,
            FileName = chosen.File,
            TotalBytes = chosen.Filesize is > 0 ? chosen.Filesize : null,
            Bytes = 0,
            State = DownloadState.Running,
            StartedAt = _time.GetUtcNow(),
            GameId = request.GameId,
            TargetVariantId = request.TargetVariantId,
            TargetDisplayName = request.TargetDisplayName,
        };

        var job = new DownloadJob(record, _time)
        {
            Cancellation = new CancellationTokenSource(),
            Page = page,
            File = chosen,
        };

        lock (_gate)
        {
            _jobs.Add(job);
        }

        Changed?.Invoke(this, new DownloadChange(job));

        _ = RunAsync(job);

        return job;
    }

    /// <inheritdoc />
    public Task WhenIdleAsync()
    {
        List<Task> running;

        lock (_gate)
        {
            running = [.. _jobs.Where(job => !job.HasCompleted).Select(job => job.Completion)];
        }

        return Task.WhenAll(running);
    }

    /// <inheritdoc />
    public bool Cancel(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        DownloadJob? job;

        lock (_gate)
        {
            job = _jobs.FirstOrDefault(entry => entry.Id == id && entry.IsRunning);
        }

        if (job?.Cancellation is not { } cancellation)
        {
            return false;
        }

        _logger.Information("Cancelling the download of GameBanana mod {ModId}", job.Record.ModId);

        cancellation.Cancel();

        return true;
    }

    /// <inheritdoc />
    public int CancelAll()
    {
        List<string> running;

        lock (_gate)
        {
            running = [.. _jobs.Where(entry => entry.IsRunning).Select(entry => entry.Id)];
        }

        return running.Count(Cancel);
    }

    /// <inheritdoc />
    public async Task<DownloadJob?> RetryAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        DownloadJob? existing;

        lock (_gate)
        {
            existing = _jobs.FirstOrDefault(entry => entry.Id == id);
        }

        if (existing is null)
        {
            return null;
        }

        var page = await _client
            .GetModAsync(existing.Record.ModId, refresh: true, cancellationToken)
            .ConfigureAwait(false);

        var file = existing.Record.FileId is { } wanted
            ? page.Files.FirstOrDefault(entry => entry.IdRow == wanted)
            : null;

        // The file has gone and the page offers several: the user chooses, not a retry.
        if (file is null && existing.Record.FileId is not null && page.HasFileChoice)
        {
            throw new GameBananaException(
                $"{existing.Record.FileName ?? "The file this download was for"} is no longer on the "
                + $"GameBanana page of '{page.Name ?? GameBananaUrl.ForMod(page.ModId)}', which now has "
                + $"{page.Files.Count.ToString(CultureInfo.InvariantCulture)} files. Add the mod again "
                + "from its address to choose one.",
                page.ModId,
                page.PageUrl);
        }

        return Start(
            page,
            file,
            new DownloadRequest(
                existing.Record.GameId,
                existing.Record.TargetVariantId,
                existing.Record.TargetDisplayName));
    }

    /// <inheritdoc />
    public Task<DownloadContents?> OpenAsync(
        string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        DownloadJob? job;

        lock (_gate)
        {
            job = _jobs.FirstOrDefault(entry => entry.Id == id);
        }

        if (job is null || job.Record.ArchivePath is not { Length: > 0 } archive || !File.Exists(archive))
        {
            return Task.FromResult<DownloadContents?>(null);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var record = job.Record;
        var file = job.File ?? FileOf(record);
        var page = job.Page ?? PageOf(record, file);

        var picture = job.Picture
            ?? (record.PicturePath is { Length: > 0 } path && File.Exists(path)
                ? PreviewImageSource.FromFile(path)
                : null);

        return Task.FromResult<DownloadContents?>(
            new DownloadContents(record, page, file, picture, archive));
    }

    /// <inheritdoc />
    public async Task<DownloadJob?> MarkInstalledAsync(
        string id, string? installedPath = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        DownloadJob? job;

        lock (_gate)
        {
            job = _jobs.FirstOrDefault(entry => entry.Id == id);
        }

        if (job is null)
        {
            return null;
        }

        Discard(job.Record.ArchivePath);
        Discard(job.Record.PicturePath);

        job.Picture = null;
        job.Record = job.Record with
        {
            State = DownloadState.Installed,
            ArchivePath = null,
            PicturePath = null,
            InstalledPath = installedPath,
            FinishedAt = job.Record.FinishedAt ?? _time.GetUtcNow(),
        };

        // Quietly: the mod is installed by now, and must not be reported failed nor lose its Undo.
        await SaveQuietlyAsync(job.Record, cancellationToken).ConfigureAwait(false);

        Changed?.Invoke(this, new DownloadChange(job));

        return job;
    }

    /// <inheritdoc />
    public async Task<bool> ForgetAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        DownloadJob? job;

        lock (_gate)
        {
            job = _jobs.FirstOrDefault(entry => entry.Id == id);

            if (job is not null)
            {
                _jobs.Remove(job);
            }
        }

        if (job is null)
        {
            return false;
        }

        // Marked first so its last write is skipped, then waited for so a write under way lands first.
        job.Forget();
        job.Cancellation?.Cancel();
        await job.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);

        Discard(job.Record.ArchivePath);
        Discard(job.Record.PicturePath);

        await _store.RemoveAsync([id], cancellationToken).ConfigureAwait(false);

        Changed?.Invoke(this, new DownloadChange(job));

        return true;
    }

    /// <inheritdoc />
    public async Task<int> PruneAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settings.ReadAsync(cancellationToken).ConfigureAwait(false);
        var keepFor = settings.GameBanana.KeepDownloads;
        var keepCount = settings.GameBanana.DownloadHistoryCount;
        var now = _time.GetUtcNow();

        var expired = new List<DownloadJob>();

        lock (_gate)
        {
            var ordered = _jobs
                .OrderByDescending(job => job.Record.StartedAt)
                .ToList();

            for (var index = 0; index < ordered.Count; index++)
            {
                var job = ordered[index];

                // Never prune a download in flight; asked of the completion, not the flag, on purpose.
                if (!job.HasCompleted)
                {
                    continue;
                }

                var age = now - (job.Record.FinishedAt ?? job.Record.StartedAt);

                if (age > keepFor || index >= keepCount)
                {
                    expired.Add(job);
                }
            }

            foreach (var job in expired)
            {
                _jobs.Remove(job);
            }
        }

        if (expired.Count == 0)
        {
            return 0;
        }

        foreach (var job in expired)
        {
            Discard(job.Record.ArchivePath);
            Discard(job.Record.PicturePath);
        }

        await _store.RemoveAsync([.. expired.Select(job => job.Id)], cancellationToken).ConfigureAwait(false);

        _logger.Information(
            "Removed {Count} download(s) from the list: older than {Days} day(s), or past the newest {Keep}",
            expired.Count,
            keepFor.TotalDays,
            keepCount);

        foreach (var job in expired)
        {
            Changed?.Invoke(this, new DownloadChange(job));
        }

        return expired.Count;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        List<DownloadJob> running;

        lock (_gate)
        {
            running = [.. _jobs.Where(job => !job.HasCompleted)];
        }

        foreach (var job in running)
        {
            job.Cancellation?.Cancel();
        }
    }

    private async Task RunAsync(DownloadJob job)
    {
        var token = job.Cancellation?.Token ?? CancellationToken.None;

        await SaveQuietlyAsync(job.Record, CancellationToken.None).ConfigureAwait(false);

        var progress = new Relay(reported =>
        {
            job.Observe(reported.Bytes, reported.TotalBytes);
            Progress?.Invoke(this, new DownloadChange(job));
        });

        try
        {
            // Never disposed on purpose: disposing deletes the archive, which the list now owns.
            var fetched = await _source
                .FetchAsync(job.Page!, job.File, progress, token)
                .ConfigureAwait(false);

            var picture = await StorePictureAsync(fetched, token).ConfigureAwait(false);

            job.Picture = fetched.Picture;
            job.File = fetched.File;
            job.Record = job.Record with
            {
                State = DownloadState.Ready,
                ArchivePath = fetched.ArchivePath,
                PicturePath = picture,
                Bytes = fetched.Download.Bytes,
                TotalBytes = fetched.Download.Bytes,
                Md5 = fetched.Download.Md5,
                Md5Verified = fetched.Download.Md5Verified,
                FileName = fetched.File.File ?? job.Record.FileName,
                FileId = fetched.File.IdRow ?? job.Record.FileId,
                Error = fetched.PictureError,
                FinishedAt = _time.GetUtcNow(),
            };

            _logger.Information(
                "Downloaded GameBanana mod {ModId} ({Name}) to {Path}",
                job.Record.ModId,
                job.Record.Name ?? "(unnamed)",
                fetched.ArchivePath);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            job.Record = job.Record with
            {
                State = DownloadState.Cancelled,
                Bytes = job.Bytes,
                FinishedAt = _time.GetUtcNow(),
            };
        }
        catch (OperationCanceledException ex)
        {
            _logger.Warning(ex, "The download of GameBanana mod {ModId} stopped without being cancelled", job.Record.ModId);

            job.Record = job.Record with
            {
                State = DownloadState.Failed,
                Error = $"The download stopped before it finished, and not because it was cancelled: {ex.Message} Try again.",
                Bytes = job.Bytes,
                FinishedAt = _time.GetUtcNow(),
            };
        }
        catch (GameBananaDownloadBlockedException blocked)
        {
            job.Picture = await TryPictureAsync(job, token).ConfigureAwait(false);

            job.Record = job.Record with
            {
                State = DownloadState.Blocked,
                Error = blocked.Message,
                PageUrl = blocked.PageUrl.AbsoluteUri,
                FinishedAt = _time.GetUtcNow(),
            };
        }
        catch (Exception ex) when (ex is XxsmException or IOException or UnauthorizedAccessException
                                      or HttpRequestException)
        {
            _logger.Warning(ex, "The download of GameBanana mod {ModId} failed", job.Record.ModId);

            job.Record = job.Record with
            {
                State = DownloadState.Failed,
                Error = ex.Message,
                Bytes = job.Bytes,
                FinishedAt = _time.GetUtcNow(),
            };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "The download of GameBanana mod {ModId} failed on an error nothing expected", job.Record.ModId);

            job.Record = job.Record with
            {
                State = DownloadState.Failed,
                Error = ex.Message,
                Bytes = job.Bytes,
                FinishedAt = _time.GetUtcNow(),
            };
        }
        finally
        {
            job.Cancellation?.Dispose();
            job.Cancellation = null;

            if (!job.IsForgotten)
            {
                await SaveQuietlyAsync(job.Record, CancellationToken.None).ConfigureAwait(false);
            }

            // In this order: marked stopped, announced, then completed.
            job.MarkFinished();
            Changed?.Invoke(this, new DownloadChange(job));
            job.Finish();
        }
    }

    private async Task<PreviewImageSource?> TryPictureAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        try
        {
            return job.Page is { } page
                ? await _source.PictureAsync(page, cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (Exception ex) when (ex is XxsmException or IOException or UnauthorizedAccessException
                                      or HttpRequestException)
        {
            _logger.Debug(ex, "Could not fetch the preview picture for GameBanana mod {ModId}", job.Record.ModId);

            return null;
        }
    }

    private async Task<string?> StorePictureAsync(GameBananaFetch fetched, CancellationToken cancellationToken)
    {
        if (fetched.Picture is not { } picture)
        {
            return null;
        }

        var directory = Path.GetDirectoryName(fetched.ArchivePath);

        if (directory is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            var bytes = await picture.ReadAsync(cancellationToken).ConfigureAwait(false);
            var path = Path.Combine(directory, "preview" + picture.Extension);

            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);

            return path;
        }
        catch (Exception ex) when (ex is XxsmException or IOException or UnauthorizedAccessException)
        {
            _logger.Debug(ex, "Could not keep the preview picture for GameBanana mod {ModId}", fetched.Mod.ModId);

            return null;
        }
    }

    private async Task SaveAsync(DownloadRecord record, CancellationToken cancellationToken) =>
        await _store
            .SaveAsync([record], await KeepCountAsync(cancellationToken).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);

    private async Task SaveQuietlyAsync(DownloadRecord record, CancellationToken cancellationToken)
    {
        try
        {
            await SaveAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is XxsmException or IOException or UnauthorizedAccessException)
        {
            _logger.Warning(ex, "Could not record the download of GameBanana mod {ModId}", record.ModId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error(ex, "Could not record the download of GameBanana mod {ModId}, on an error nothing expected", record.ModId);
        }
    }

    private async Task<int> KeepCountAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.ReadAsync(cancellationToken).ConfigureAwait(false);

        return settings.GameBanana.DownloadHistoryCount;
    }

    private void Discard(string? path)
    {
        if (path is not { Length: > 0 })
        {
            return;
        }

        // Only ever a file inside XXSM's own downloads folder: the list can be edited by hand.
        if (!UntrustedLocation.IsSameOrUnderExactly(_downloads, path))
        {
            _logger.Warning("Not removing {Path}: it is not in XXSM's downloads folder {Downloads}", path, _downloads);
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            GameBananaFetch.RemoveEmptyFolder(Path.GetDirectoryName(path), _logger);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // File.Delete on purpose: XXSM's own cache, not user content.
            _logger.Debug(ex, "Could not remove the downloaded file {Path}", path);
        }
    }

    /// <summary>Reports progress on the thread that read the bytes, rather than posting it elsewhere.</summary>
    /// <remarks><see cref="Progress{T}"/> would queue reports to arrive after the download finished.</remarks>
    private sealed class Relay(Action<GameBananaDownloadProgress> report)
        : IProgress<GameBananaDownloadProgress>
    {
        public void Report(GameBananaDownloadProgress value) => report(value);
    }

    private static GameBananaFile FileOf(DownloadRecord record) => new()
    {
        IdRow = record.FileId,
        File = record.FileName,
        Filesize = record.TotalBytes,
    };

    private static GameBananaMod PageOf(DownloadRecord record, GameBananaFile file) => new()
    {
        ModId = record.ModId,
        Name = record.Name,
        Author = record.Author,
        Version = record.Version,
        Description = record.Description,
        Summary = GameBananaText.Summarise(record.Description),
        PageUrl = Uri.TryCreate(record.PageUrl, UriKind.Absolute, out var page)
            ? page
            : new Uri(GameBananaUrl.ForMod(record.ModId)),
        PreviewImageUrl = Uri.TryCreate(record.PictureUrl, UriKind.Absolute, out var picture) ? picture : null,
        DateModifiedTs = record.DateModifiedTs,
        DateModified = record.DateModifiedTs is > 0 and < 253_402_300_800
            ? DateTimeOffset.FromUnixTimeSeconds(record.DateModifiedTs.Value)
            : null,
        Files = [file],
    };
}
