using Xxsm.Packs.Model;

namespace Xxsm.Packs.Overlays;

/// <summary>Reads and writes the user's overlay for a game. Every write is atomic, after a backup.</summary>
public interface IOverlayStore
{
    /// <summary>The path the overlay for a game is stored at.</summary>
    string GetOverlayPath(string gameId);

    /// <summary>The path the previous version is backed up to.</summary>
    string GetBackupPath(string gameId);

    /// <summary>Reads a game's overlay, or an empty one when the user has made no edits.</summary>
    /// <returns>The overlay. Never null: no file means no edits yet.</returns>
    /// <exception cref="Xxsm.Core.PackLoadException">The overlay exists but cannot be read.</exception>
    Task<PackOverlay> ReadAsync(string gameId, CancellationToken cancellationToken = default);

    /// <summary>Replaces a game's overlay whole: backup, temp file, flush, then rename over the original.</summary>
    /// <remarks>For an edit that depends on what the overlay holds, use <see cref="UpdateAsync{T}"/>.</remarks>
    /// <param name="overlay">The overlay to persist. Its game id names the file.</param>
    /// <param name="cancellationToken">Cancels before anything is written.</param>
    /// <exception cref="Xxsm.Core.ModOperationException">The write failed. The previous overlay is left
    /// intact.</exception>
    Task WriteAsync(PackOverlay overlay, CancellationToken cancellationToken = default);

    /// <summary>Reads, changes and saves a game's overlay as one turn no other change can interleave with.</summary>
    /// <typeparam name="T">What the change reports back.</typeparam>
    /// <param name="gameId">The game.</param>
    /// <param name="change">Given the overlay as it is, the new overlay (or none, to save nothing) and a result.
    /// It may run more than once, so it must only compute.</param>
    /// <param name="cancellationToken">Cancels while waiting or before the save.</param>
    /// <exception cref="Xxsm.Core.PackLoadException">The overlay exists but cannot be read.</exception>
    /// <exception cref="Xxsm.Core.ModOperationException">The save failed, or another program kept saving the file.
    /// The previous overlay is left intact.</exception>
    Task<T> UpdateAsync<T>(
        string gameId, Func<PackOverlay, OverlayUpdate<T>> change, CancellationToken cancellationToken = default);
}

/// <summary>What a change given to <see cref="IOverlayStore.UpdateAsync{T}"/> decided.</summary>
/// <typeparam name="T">What it reports back.</typeparam>
/// <param name="Overlay">The overlay to save, or <see langword="null"/> to save nothing.</param>
/// <param name="Result">What to report back.</param>
public readonly record struct OverlayUpdate<T>(PackOverlay? Overlay, T Result);

/// <summary>Builds an <see cref="OverlayUpdate{T}"/>.</summary>
public static class OverlayUpdate
{
    /// <summary>Save <paramref name="overlay"/> and report <paramref name="result"/>.</summary>
    public static OverlayUpdate<T> Write<T>(PackOverlay overlay, T result)
    {
        ArgumentNullException.ThrowIfNull(overlay);

        return new(overlay, result);
    }

    /// <summary>Save nothing, and report <paramref name="result"/>.</summary>
    public static OverlayUpdate<T> Keep<T>(T result) => new(null, result);
}
