namespace Xxsm.Core.GameBanana;

/// <summary>One mod as XXSM understands it, projected from a <see cref="GameBananaProfilePage"/>.</summary>
public sealed record GameBananaMod
{
    /// <summary>The mod page's id.</summary>
    public required long ModId { get; init; }

    /// <summary>The mod's title, or null when the page has none.</summary>
    public string? Name { get; init; }

    /// <summary>Who submitted it.</summary>
    public string? Author { get; init; }

    /// <summary>The submitter's member page.</summary>
    public Uri? AuthorPageUrl { get; init; }

    /// <summary>The author's own version string. Free text; never parsed or compared as a number.</summary>
    public string? Version { get; init; }

    /// <summary>The whole description, as plain text.</summary>
    public string? Description { get; init; }

    /// <summary>The opening line or two of <see cref="Description"/>, for somewhere with no room.</summary>
    public string? Summary { get; init; }

    /// <summary>The mod's page.</summary>
    public required Uri PageUrl { get; init; }

    /// <summary>The thumbnail: the site's 530-pixel rendering, else the original; null with no screenshots.</summary>
    public Uri? PreviewImageUrl { get; init; }

    /// <summary>When the mod was last changed. What an update check compares.</summary>
    public DateTimeOffset? DateModified { get; init; }

    /// <summary>Upstream's own last-changed value, in Unix seconds, as stored in <c>mod.json</c>.</summary>
    public long? DateModifiedTs { get; init; }

    /// <summary>When the mod was first submitted.</summary>
    public DateTimeOffset? DateAdded { get; init; }

    /// <summary>The game GameBanana lists the mod under, or null when the page did not say.</summary>
    public string? GameName { get; init; }

    /// <summary>GameBanana's abbreviation for that game, or null when the page did not say.</summary>
    public string? GameShortName { get; init; }

    /// <summary>The mod's category, for example a character's name.</summary>
    public string? Category { get; init; }

    /// <summary>The category's parent, for example <c>Characters</c>.</summary>
    public string? SuperCategory { get; init; }

    /// <summary>The author's tags, written out as <c>Title: Value</c>.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>The downloadable files, largest id first is <em>not</em> assumed — upstream's order.</summary>
    public IReadOnlyList<GameBananaFile> Files { get; init; } = [];

    /// <summary>Why the mod cannot be downloaded, or null when it can: trashed, withheld or private.</summary>
    public string? UnavailableReason { get; init; }

    /// <summary>Whether the mod page is no longer available.</summary>
    public bool IsUnavailable => UnavailableReason is { Length: > 0 };

    /// <summary>Whether there is a file to download.</summary>
    public bool HasFiles => Files.Count > 0;

    /// <summary>Whether the page offers several files, so the user has to choose one.</summary>
    public bool HasFileChoice => Files.Count > 1;

    /// <summary>The file to download: the one chosen, or the page's only file. Never a guess.</summary>
    /// <param name="chosen">The file the user chose, or null when they were not asked.</param>
    /// <returns>The file.</returns>
    /// <exception cref="GameBananaException">The mod is unavailable or has no files; <see
    /// cref="GameBananaFileChoiceException"/> when it has several and none was chosen.</exception>
    public GameBananaFile FileToTake(GameBananaFile? chosen)
    {
        if (UnavailableReason is { } reason)
        {
            throw new GameBananaException(reason, ModId, PageUrl);
        }

        if (chosen is not null)
        {
            return chosen;
        }

        return Files.Count switch
        {
            0 => throw new GameBananaException(
                $"'{Name ?? GameBananaUrl.ForMod(ModId)}' has no files to download on its " +
                "GameBanana page. Its author may have removed them.",
                ModId,
                PageUrl),
            1 => Files[0],
            _ => throw new GameBananaFileChoiceException(this),
        };
    }

    /// <summary>A file in one line: what the author calls it, its name, size and upload date.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> is null.</exception>
    public static string Describe(GameBananaFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var parts = new List<string>();

        if (file.IdRow is { } id)
        {
            parts.Add(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (Trimmed(file.Description) is { } description)
        {
            parts.Add($"\"{description}\"");
        }

        var details = new List<string>();

        if (Trimmed(file.File) is { } name)
        {
            details.Add(name);
        }

        if (file.Filesize is > 0 and var size)
        {
            details.Add((size / 1_048_576d).ToString("0.0 'MB'", System.Globalization.CultureInfo.InvariantCulture));
        }

        if (FromUnixSeconds(file.DateAddedTs) is { } added)
        {
            details.Add("uploaded " + added.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        }

        return string.Join(' ', parts) + (details.Count > 0 ? " " + string.Join(", ", details) : string.Empty);
    }

    /// <summary>When a file was uploaded, or null when upstream did not say.</summary>
    /// <returns>The date, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> is null.</exception>
    public static DateTimeOffset? UploadedOf(GameBananaFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return FromUnixSeconds(file.DateAddedTs);
    }

    /// <summary>Projects an apiv11 profile page into the application's own shape.</summary>
    /// <param name="page">The response, as deserialised.</param>
    /// <param name="modId">The mod id asked for, used when the payload has no <c>_idRow</c>.</param>
    /// <param name="files">The file list to use instead of the page's; null keeps the page's own.</param>
    /// <returns>The projection. Never null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="page"/> is null.</exception>
    public static GameBananaMod From(
        GameBananaProfilePage page, long modId, IReadOnlyList<GameBananaFile>? files = null)
    {
        ArgumentNullException.ThrowIfNull(page);

        var description = GameBananaText.ToPlainText(page.Text)
            ?? GameBananaText.ToPlainText(page.Description);

        return new GameBananaMod
        {
            ModId = page.IdRow is > 0 ? page.IdRow.Value : modId,
            Name = Trimmed(page.Name),
            Author = Trimmed(page.Submitter?.Name),
            AuthorPageUrl = ToUri(page.Submitter?.ProfileUrl),
            Version = Trimmed(page.Version),
            Description = description,
            Summary = GameBananaText.Summarise(description),
            PageUrl = ToUri(page.ProfileUrl) ?? new Uri(GameBananaUrl.ForMod(modId)),
            PreviewImageUrl = ThumbnailOf(page.PreviewMedia),
            DateModified = FromUnixSeconds(page.DateModifiedTs),
            DateModifiedTs = page.DateModifiedTs,
            DateAdded = FromUnixSeconds(page.DateAddedTs),
            GameName = Trimmed(page.Game?.Name),
            GameShortName = Trimmed(page.Game?.Abbreviation),
            Category = Trimmed(page.Category?.Name),
            SuperCategory = Trimmed(page.SuperCategory?.Name),
            // A null in any of these lists is skipped rather than failing the whole answer.
            Tags = [.. (page.Tags ?? []).OfType<GameBananaTag>().Select(NameOf).OfType<string>()],
            Files = [.. (files ?? page.Files ?? []).Where(file => file is not null && (file.DownloadUrl is { Length: > 0 } || file.IdRow is > 0))],
            UnavailableReason = ReasonOf(page),
        };
    }

    /// <summary>The full address of a file, from its own or built from its id.</summary>
    /// <returns>Where to fetch it, or null when the entry names neither.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> is null.</exception>
    public static Uri? AddressOf(GameBananaFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return ToUri(file.DownloadUrl)
            ?? (file.IdRow is > 0
                ? new Uri($"https://{GameBananaUrl.Host}/dl/{file.IdRow.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}")
                : null);
    }

    private static string? ReasonOf(GameBananaProfilePage page) => page switch
    {
        { IsTrashed: true } => "That mod has been trashed on GameBanana, so there is nothing left to download.",
        { IsWithheld: true } => "That mod has been withheld by a GameBanana moderator, so it cannot be downloaded.",
        { IsPrivate: true } => "That mod is private to its author, so it cannot be downloaded.",
        _ => null,
    };

    private static string? NameOf(GameBananaTag tag) => (Trimmed(tag.Title), Trimmed(tag.Value)) switch
    {
        (null, null) => null,
        ({ } title, null) => title,
        (null, { } value) => value,
        ({ } title, { } value) => $"{title}: {value}",
    };

    private static Uri? ThumbnailOf(GameBananaPreviewMedia? media)
    {
        foreach (var image in media?.Images ?? [])
        {
            if (image is null || Trimmed(image.BaseUrl) is not { } baseUrl)
            {
                continue;
            }

            var name = Trimmed(image.File530) ?? Trimmed(image.File);

            if (name is not null && ToUri($"{baseUrl.TrimEnd('/')}/{name}") is { } address)
            {
                return address;
            }
        }

        return null;
    }

    private static DateTimeOffset? FromUnixSeconds(long? seconds) =>
        seconds is > 0 and < 253_402_300_800 ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : null;

    private static Uri? ToUri(string? text) =>
        Trimmed(text) is { } value
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
            ? uri
            : null;

    private static string? Trimmed(string? text) => text?.Trim() is { Length: > 0 } value ? value : null;
}
