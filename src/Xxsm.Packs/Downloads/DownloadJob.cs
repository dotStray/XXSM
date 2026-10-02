using Xxsm.Core.GameBanana;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;

namespace Xxsm.Packs.Downloads;

/// <summary>Where a download should end up, when it ends up anywhere.</summary>
/// <param name="GameId">The game whose Mods folder it was started from.</param>
/// <param name="TargetVariantId">A character to file it under, overriding the sorter, or null.</param>
/// <param name="TargetDisplayName">That character's name, for the install card's title.</param>
public readonly record struct DownloadRequest(
    string? GameId = null, string? TargetVariantId = null, string? TargetDisplayName = null);

/// <summary>One download, live: what the list knows about it plus how it is getting on.</summary>
public sealed class DownloadJob
{
    private readonly TransferRate _rate;
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private volatile bool _finished;
    private volatile bool _forgotten;

    internal DownloadJob(DownloadRecord record, TimeProvider time)
    {
        Record = record;
        _rate = new TransferRate(time);

        if (!record.IsRunning)
        {
            _finished = true;
            _completion.TrySetResult();
        }
    }

    /// <summary>This download's id.</summary>
    public string Id => Record.Id;

    /// <summary>What the list holds about it.</summary>
    public DownloadRecord Record { get; internal set; }

    /// <summary>What became of it.</summary>
    public DownloadState State => Record.State;

    /// <summary>Whether it is still going; false before the change is announced and completion is set.</summary>
    public bool IsRunning => !_finished;

    /// <summary>Whether everything watching has been told it stopped; later than <see cref="IsRunning"/>.</summary>
    public bool HasCompleted => Completion.IsCompleted;

    /// <summary>How many bytes have arrived.</summary>
    public long Bytes => IsRunning ? _rate.Bytes : Record.Bytes;

    /// <summary>How many there are altogether, when the server said.</summary>
    public long? TotalBytes => Record.TotalBytes;

    /// <summary>How far along, 0 to 1, or null when the total is unknown.</summary>
    public double? Fraction =>
        TotalBytes is > 0 ? Math.Clamp((double)Bytes / TotalBytes.Value, 0, 1) : null;

    /// <summary>How fast it is going, in bytes a second, or null when too early to say.</summary>
    public double? BytesPerSecond => IsRunning ? _rate.BytesPerSecond : null;

    /// <summary>How much longer it should take, or null when that cannot be estimated.</summary>
    public TimeSpan? Remaining => IsRunning ? _rate.Remaining(TotalBytes) : null;

    /// <summary>Completes when the download stops, however it stops. Never faults.</summary>
    public Task Completion => _completion.Task;

    /// <summary>The mod's page, while this session has read it; null for an entry restored at start-up.</summary>
    public GameBananaMod? Page { get; internal set; }

    /// <summary>Which of the mod's files this download is of.</summary>
    public GameBananaFile? File { get; internal set; }

    /// <summary>The preview picture, while this session holds it.</summary>
    public PreviewImageSource? Picture { get; internal set; }

    internal CancellationTokenSource? Cancellation { get; set; }

    /// <summary>Whether it has been taken off the list, so nothing may write it back.</summary>
    internal bool IsForgotten => _forgotten;

    internal void Observe(long bytes, long? total)
    {
        if (total is > 0 && Record.TotalBytes != total)
        {
            Record = Record with { TotalBytes = total };
        }

        _rate.Observe(bytes);
    }

    /// <summary>Says it has been taken off the list, before it is stopped.</summary>
    internal void Forget() => _forgotten = true;

    /// <summary>Says it has stopped, before the change is announced.</summary>
    internal void MarkFinished() => _finished = true;

    /// <summary>Completes it, after the change has been announced.</summary>
    internal void Finish()
    {
        _finished = true;
        _completion.TrySetResult();
    }
}

/// <summary>A download that changed.</summary>
public readonly record struct DownloadChange(DownloadJob Job);

/// <summary>Everything needed to install one entry from the list.</summary>
/// <param name="Record">The entry.</param>
/// <param name="Mod">The mod page, as it was when it was downloaded.</param>
/// <param name="File">The file that was taken.</param>
/// <param name="Picture">The preview picture, when one was kept.</param>
/// <param name="ArchivePath">The archive on disk.</param>
public sealed record DownloadContents(
    DownloadRecord Record,
    GameBananaMod Mod,
    GameBananaFile File,
    PreviewImageSource? Picture,
    string ArchivePath);
