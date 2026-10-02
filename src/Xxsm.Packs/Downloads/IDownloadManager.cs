using Xxsm.Core.GameBanana;

namespace Xxsm.Packs.Downloads;

/// <summary>Runs GameBanana downloads in the background and remembers what became of each one.</summary>
public interface IDownloadManager
{
    /// <summary>Raised when a download is added, changes state, or leaves the list, on the download's thread.</summary>
    event EventHandler<DownloadChange>? Changed;

    /// <summary>Raised as bytes arrive. Never touches the disk.</summary>
    event EventHandler<DownloadChange>? Progress;

    /// <summary>Every download this session knows about, newest first.</summary>
    IReadOnlyList<DownloadJob> Jobs { get; }

    /// <summary>Reads the list from disk, once, and prunes what has expired.</summary>
    Task<IReadOnlyList<DownloadJob>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts downloading a mod's file, and returns before it finishes.</summary>
    /// <param name="page">The mod, from <see cref="IGameBananaClient.GetModAsync"/>.</param>
    /// <param name="file">Which file to take, or null when the page has only one.</param>
    /// <param name="request">Where it should end up.</param>
    /// <returns>The job. Await <see cref="DownloadJob.Completion"/> to wait for it.</returns>
    /// <exception cref="GameBananaException">The mod has no file or is unavailable;
    /// <see cref="GameBananaFileChoiceException"/> when it has several and no file was given.</exception>
    DownloadJob Start(GameBananaMod page, GameBananaFile? file = null, DownloadRequest request = default);

    /// <summary>Completes when every download running at the call has stopped. Never faults.</summary>
    Task WhenIdleAsync();

    /// <summary>Stops a download and discards the part of the file that arrived.</summary>
    /// <returns>True when there was a running download to stop.</returns>
    bool Cancel(string id);

    /// <summary>Stops every running download, as <see cref="Cancel"/> does one. Entries and archives stay.</summary>
    /// <returns>How many downloads were stopped.</returns>
    int CancelAll();

    /// <summary>Starts a finished download's mod again, as a new entry, reading its page again.</summary>
    /// <returns>The new job, or null when there is no such entry.</returns>
    Task<DownloadJob?> RetryAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Everything needed to install one entry, without asking GameBanana again.</summary>
    /// <returns>The contents, or null when the entry is gone or its archive is.</returns>
    Task<DownloadContents?> OpenAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Records that an entry was installed and removes its archive. A write failure is logged.</summary>
    /// <param name="id">Which download.</param>
    /// <param name="installedPath">Where the mod ended up, when one mod came out of it.</param>
    /// <param name="cancellationToken">Cancels before anything is written.</param>
    /// <returns>The entry as it now stands, or null when there is no such entry.</returns>
    Task<DownloadJob?> MarkInstalledAsync(
        string id, string? installedPath = null, CancellationToken cancellationToken = default);

    /// <summary>Takes an entry off the list and removes its archive, stopping it first if it runs.</summary>
    /// <returns>True when there was an entry to remove.</returns>
    Task<bool> ForgetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Removes expired entries and their archives; a running download is never pruned.</summary>
    /// <returns>How many entries went.</returns>
    Task<int> PruneAsync(CancellationToken cancellationToken = default);
}
