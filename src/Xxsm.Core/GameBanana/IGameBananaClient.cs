namespace Xxsm.Core.GameBanana;

/// <summary>How far a download has got.</summary>
/// <param name="Bytes">How many bytes have arrived.</param>
/// <param name="TotalBytes">How many there are altogether, or null when the server did not say.</param>
public readonly record struct GameBananaDownloadProgress(long Bytes, long? TotalBytes)
{
    /// <summary>How far along, 0 to 1, or null when the total is unknown.</summary>
    public double? Fraction => TotalBytes is > 0 ? Math.Clamp((double)Bytes / TotalBytes.Value, 0, 1) : null;
}

/// <summary>A file that was downloaded.</summary>
/// <param name="Path">Where it was written.</param>
/// <param name="Bytes">How big it turned out to be.</param>
/// <param name="Md5">The checksum upstream published, or null when it published none.</param>
/// <param name="Md5Verified">Whether the bytes matched <paramref name="Md5"/>; false with nothing to check. A mismatch
/// throws.</param>
public sealed record GameBananaDownloadedFile(string Path, long Bytes, string? Md5, bool Md5Verified);

/// <summary>Reads GameBanana's apiv11 and downloads from it: paced, cached, and only while switched on.</summary>
/// <remarks>Every method throws <see cref="GameBananaDisabledException"/> while GameBanana is switched off.</remarks>
public interface IGameBananaClient
{
    /// <summary>Reads a mod's page.</summary>
    /// <param name="modId">The mod id, as <see cref="GameBananaUrl.TryParseModId"/> found it.</param>
    /// <param name="refresh">True to ignore a cached copy and ask the site.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The mod.</returns>
    /// <exception cref="GameBananaDisabledException">GameBanana support is switched off.</exception>
    /// <exception cref="GameBananaException">No such mod, or the site refused, could not be reached or did not answer
    /// as the API; with its own status text.</exception>
    Task<GameBananaMod> GetModAsync(
        long modId, bool refresh = false, CancellationToken cancellationToken = default);

    /// <summary>Downloads one of a mod's files.</summary>
    /// <param name="file">The file, from <see cref="GameBananaMod.Files"/>.</param>
    /// <param name="destinationDirectory">The folder to write it into. Created if missing.</param>
    /// <param name="modId">The mod it belongs to, for the log and any error message.</param>
    /// <param name="progress">Told how far along the download is; may be null.</param>
    /// <param name="cancellationToken">Cancels the download and removes the part file.</param>
    /// <returns>Where the archive landed, and whether its checksum matched.</returns>
    /// <exception cref="GameBananaDisabledException">GameBanana support is switched off.</exception>
    /// <exception cref="GameBananaDownloadBlockedException">The site answered with a browser challenge.</exception>
    /// <exception cref="GameBananaException">The download failed, or the bytes did not match the published
    /// checksum.</exception>
    Task<GameBananaDownloadedFile> DownloadAsync(
        GameBananaFile file,
        string destinationDirectory,
        long? modId = null,
        IProgress<GameBananaDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Forgets cached responses, so the next look-up asks the site. Works while switched off.</summary>
    /// <param name="modId">The mod to forget, or null for every mod.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>How many cached responses were removed.</returns>
    Task<int> ForgetAsync(long? modId = null, CancellationToken cancellationToken = default);

    /// <summary>Waits for this client's next turn, for a request made elsewhere, such as a mod's picture.</summary>
    Task WaitForTurnAsync(CancellationToken cancellationToken = default);
}
