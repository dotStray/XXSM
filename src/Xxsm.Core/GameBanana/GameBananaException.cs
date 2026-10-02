namespace Xxsm.Core.GameBanana;

/// <summary>Thrown when something XXSM asked GameBanana for did not happen. Callers degrade.</summary>
public class GameBananaException : XxsmException
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="message">The message, written to be shown to a user unedited.</param>
    /// <param name="modId">The mod the request was about, when there is one.</param>
    /// <param name="address">The address that was asked for, when there is one.</param>
    /// <param name="innerException">The underlying failure.</param>
    public GameBananaException(
        string message, long? modId = null, Uri? address = null, Exception? innerException = null)
        : base(message, innerException)
    {
        ModId = modId;
        Address = address;
    }

    /// <summary>The mod the request was about, when known.</summary>
    public long? ModId { get; }

    /// <summary>The address that was asked for, when known.</summary>
    public Uri? Address { get; }
}

/// <summary>Thrown when GameBanana support is switched off and something asked for it anyway.</summary>
public sealed class GameBananaDisabledException : GameBananaException
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="modId">The mod the request would have been about, when there is one.</param>
    public GameBananaDisabledException(long? modId = null)
        : base(
            "Looking mods up on GameBanana is switched off. Settings › GameBanana turns it on.",
            modId)
    {
    }
}

/// <summary>Thrown when a download was answered with a browser challenge instead of the file.</summary>
public sealed class GameBananaDownloadBlockedException : GameBananaException
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="message">The message, written to be shown to a user unedited.</param>
    /// <param name="pageUrl">The mod's page, to open in the user's browser.</param>
    /// <param name="modId">The mod the download was for.</param>
    public GameBananaDownloadBlockedException(string message, Uri pageUrl, long? modId = null)
        : base(message, modId, pageUrl)
    {
        PageUrl = pageUrl;
    }

    /// <summary>The mod's page, to open so the user can fetch the archive themselves.</summary>
    public Uri PageUrl { get; }
}

/// <summary>Thrown when a mod's page offers several files and nobody said which one to take.</summary>
public sealed class GameBananaFileChoiceException : GameBananaException
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="page">The mod, with its files.</param>
    public GameBananaFileChoiceException(GameBananaMod page)
        : base(
            $"'{page.Name ?? GameBananaUrl.ForMod(page.ModId)}' has "
            + $"{page.Files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} files on its "
            + "GameBanana page, and XXSM will not guess which one you want. They are: "
            + string.Join("; ", page.Files.Select(GameBananaMod.Describe))
            + ".",
            page.ModId,
            page.PageUrl)
    {
        Files = page.Files;
    }

    /// <summary>The files to choose between.</summary>
    public IReadOnlyList<GameBananaFile> Files { get; }
}

/// <summary>Thrown when a GameBanana developer tool was asked for while they are switched off.</summary>
public sealed class GameBananaDeveloperToolsOffException : GameBananaException
{
    /// <summary>Initialises a new instance.</summary>
    public GameBananaDeveloperToolsOffException()
        : base(
            "The GameBanana developer tools are switched off. Add \"developerTools\": true to the " +
            "\"gameBanana\" block of settings.json to turn them on.")
    {
    }
}
