using Xxsm.Core;
using Xxsm.Core.Mods;

namespace Xxsm.Packs.Portraits;

/// <summary>Keeps XXSM's own copy of a picture given to a character, so it survives the original being moved.</summary>
/// <remarks>Each copy gets a new name, so a cache keyed by the <c>image</c> value never shows the old one.</remarks>
public interface ICharacterPortraitStore
{
    /// <summary>Copies a picture into XXSM's data folder for a character of one game.</summary>
    /// <returns>The copy's <c>file://</c> URL, which is what a character's <c>image</c> holds.</returns>
    /// <exception cref="ModOperationException">The game id could name a folder outside the portraits folder, the
    /// picture is not usable, or the copy failed (with the operating system's text).</exception>
    Task<string> StoreAsync(string gameId, PreviewImageSource image, CancellationToken cancellationToken = default);
}
