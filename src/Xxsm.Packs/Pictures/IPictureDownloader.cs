using Xxsm.Core;
using Xxsm.Core.Mods;

namespace Xxsm.Packs.Pictures;

/// <summary>Fetches a picture dragged in from a web page, which arrives as an address rather than a file.</summary>
/// <remarks>One request per drop: nothing here crawls, retries or follows a page to find an image.</remarks>
public interface IPictureDownloader
{
    /// <summary>Fetches the picture at an address.</summary>
    /// <param name="address">An <c>http</c>, <c>https</c>, <c>data:image/…;base64</c> or <c>file</c> address.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The picture, as bytes with the format the server declared, or the file.</returns>
    /// <exception cref="ModOperationException">The address cannot be fetched, the server failed, or the result is not
    /// a png, jpg, webp, bmp or gif under the size limit.</exception>
    Task<PreviewImageSource> DownloadAsync(Uri address, CancellationToken cancellationToken = default);
}
