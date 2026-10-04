using System.Text.Json.Serialization;

namespace Xxsm.Core.GameBanana;

/// <summary>The <c>Mod/{id}/ProfilePage</c> response, as apiv11 returns it. Every field may be missing.</summary>
public sealed record GameBananaProfilePage
{
    /// <summary>The mod's own id.</summary>
    [JsonPropertyName("_idRow")]
    public long? IdRow { get; init; }

    /// <summary>The mod's title.</summary>
    [JsonPropertyName("_sName")]
    public string? Name { get; init; }

    /// <summary>The author's own version string. Free text; never parsed.</summary>
    [JsonPropertyName("_sVersion")]
    public string? Version { get; init; }

    /// <summary>The one-line blurb, when the mod has one. Plain text upstream.</summary>
    [JsonPropertyName("_sDescription")]
    public string? Description { get; init; }

    /// <summary>The description body. <strong>Attacker-controlled HTML</strong> — never shown raw.</summary>
    [JsonPropertyName("_sText")]
    public string? Text { get; init; }

    /// <summary>The mod's page address.</summary>
    [JsonPropertyName("_sProfileUrl")]
    public string? ProfileUrl { get; init; }

    /// <summary>When the mod was first submitted, in Unix seconds.</summary>
    [JsonPropertyName("_tsDateAdded")]
    public long? DateAddedTs { get; init; }

    /// <summary>When the mod was last changed, in Unix seconds. What an update check compares.</summary>
    [JsonPropertyName("_tsDateModified")]
    public long? DateModifiedTs { get; init; }

    /// <summary>Who submitted it.</summary>
    [JsonPropertyName("_aSubmitter")]
    public GameBananaSubmitter? Submitter { get; init; }

    /// <summary>The screenshots, the first of which is the page's own thumbnail.</summary>
    [JsonPropertyName("_aPreviewMedia")]
    public GameBananaPreviewMedia? PreviewMedia { get; init; }

    /// <summary>The game the mod is listed under.</summary>
    [JsonPropertyName("_aGame")]
    public GameBananaGame? Game { get; init; }

    /// <summary>The category, which for a character mod is usually the character.</summary>
    [JsonPropertyName("_aCategory")]
    public GameBananaCategory? Category { get; init; }

    /// <summary>The category's parent, for example <c>Characters</c>.</summary>
    [JsonPropertyName("_aSuperCategory")]
    public GameBananaCategory? SuperCategory { get; init; }

    /// <summary>The author's tags. Objects, not strings — see the fixture README.</summary>
    [JsonPropertyName("_aTags")]
    public IReadOnlyList<GameBananaTag>? Tags { get; init; }

    /// <summary>The downloadable files, as on the download page.</summary>
    [JsonPropertyName("_aFiles")]
    public IReadOnlyList<GameBananaFile>? Files { get; init; }

    /// <summary>Whether the mod has been trashed.</summary>
    [JsonPropertyName("_bIsTrashed")]
    public bool? IsTrashed { get; init; }

    /// <summary>Whether the mod has been withheld by a moderator.</summary>
    [JsonPropertyName("_bIsWithheld")]
    public bool? IsWithheld { get; init; }

    /// <summary>Whether the mod is private to its author.</summary>
    [JsonPropertyName("_bIsPrivate")]
    public bool? IsPrivate { get; init; }
}

/// <summary>The <c>Mod/{id}/DownloadPage</c> response, asked for when the profile page carried no files.</summary>
public sealed record GameBananaDownloadPage
{
    /// <summary>The downloadable files.</summary>
    [JsonPropertyName("_aFiles")]
    public IReadOnlyList<GameBananaFile>? Files { get; init; }

    /// <summary>Whether the mod has been trashed.</summary>
    [JsonPropertyName("_bIsTrashed")]
    public bool? IsTrashed { get; init; }

    /// <summary>Whether the mod has been withheld by a moderator.</summary>
    [JsonPropertyName("_bIsWithheld")]
    public bool? IsWithheld { get; init; }
}

/// <summary>Who submitted a mod.</summary>
public sealed record GameBananaSubmitter
{
    /// <summary>Their display name. What XXSM writes as the mod's author.</summary>
    [JsonPropertyName("_sName")]
    public string? Name { get; init; }

    /// <summary>Their member page.</summary>
    [JsonPropertyName("_sProfileUrl")]
    public string? ProfileUrl { get; init; }

    /// <summary>Their avatar's address.</summary>
    [JsonPropertyName("_sAvatarUrl")]
    public string? AvatarUrl { get; init; }
}

/// <summary>A mod's preview images.</summary>
public sealed record GameBananaPreviewMedia
{
    /// <summary>The screenshots, in the author's own order.</summary>
    [JsonPropertyName("_aImages")]
    public IReadOnlyList<GameBananaImage>? Images { get; init; }
}

/// <summary>One preview image, at <see cref="BaseUrl"/>/<see cref="File"/>. Each sized variant is optional.</summary>
public sealed record GameBananaImage
{
    /// <summary>What kind of image it is, for example <c>screenshot</c>.</summary>
    [JsonPropertyName("_sType")]
    public string? Type { get; init; }

    /// <summary>The host and folder the file sits in, without a trailing slash.</summary>
    [JsonPropertyName("_sBaseUrl")]
    public string? BaseUrl { get; init; }

    /// <summary>The full-size file name.</summary>
    [JsonPropertyName("_sFile")]
    public string? File { get; init; }

    /// <summary>The 530px-wide file name, when there is one.</summary>
    [JsonPropertyName("_sFile530")]
    public string? File530 { get; init; }

    /// <summary>The 100px-wide file name, when there is one.</summary>
    [JsonPropertyName("_sFile100")]
    public string? File100 { get; init; }
}

/// <summary>The game a mod is listed under on GameBanana.</summary>
public sealed record GameBananaGame
{
    /// <summary>The game's id on GameBanana.</summary>
    [JsonPropertyName("_idRow")]
    public long? IdRow { get; init; }

    /// <summary>The game's name, as GameBanana writes it.</summary>
    [JsonPropertyName("_sName")]
    public string? Name { get; init; }

    /// <summary>GameBanana's abbreviation for the game, for example <c>GI</c>.</summary>
    [JsonPropertyName("_sAbbreviation")]
    public string? Abbreviation { get; init; }
}

/// <summary>A mod's category, or its parent category.</summary>
public sealed record GameBananaCategory
{
    /// <summary>The category's id.</summary>
    [JsonPropertyName("_idRow")]
    public long? IdRow { get; init; }

    /// <summary>Its name.</summary>
    [JsonPropertyName("_sName")]
    public string? Name { get; init; }

    /// <summary>Its page.</summary>
    [JsonPropertyName("_sProfileUrl")]
    public string? ProfileUrl { get; init; }
}

/// <summary>One of an author's tags: a title and a value, not a bare word.</summary>
public sealed record GameBananaTag
{
    /// <summary>The kind of tag, for example <c>Software Used</c>.</summary>
    [JsonPropertyName("_sTitle")]
    public string? Title { get; init; }

    /// <summary>Its value, for example <c>Blender</c>.</summary>
    [JsonPropertyName("_sValue")]
    public string? Value { get; init; }
}

/// <summary>One downloadable file on a mod page.</summary>
public sealed record GameBananaFile
{
    /// <summary>The file's id. Also its download address: <c>gamebanana.com/dl/&lt;id&gt;</c>.</summary>
    [JsonPropertyName("_idRow")]
    public long? IdRow { get; init; }

    /// <summary>Its name, for example <c>somemod_74807.7z</c>.</summary>
    [JsonPropertyName("_sFile")]
    public string? File { get; init; }

    /// <summary>Its size in bytes. Worth showing before a download starts: these reach hundreds of MB.</summary>
    [JsonPropertyName("_nFilesize")]
    public long? Filesize { get; init; }

    /// <summary>Where to fetch it.</summary>
    [JsonPropertyName("_sDownloadUrl")]
    public string? DownloadUrl { get; init; }

    /// <summary>Its MD5, when upstream supplies one. Verified after download.</summary>
    [JsonPropertyName("_sMd5Checksum")]
    public string? Md5Checksum { get; init; }

    /// <summary>How many times it has been downloaded. Shown when choosing between a page's files.</summary>
    [JsonPropertyName("_nDownloadCount")]
    public long? DownloadCount { get; init; }

    /// <summary>When it was uploaded, in Unix seconds.</summary>
    [JsonPropertyName("_tsDateAdded")]
    public long? DateAddedTs { get; init; }

    /// <summary>The version this particular file is, when the author versions files separately.</summary>
    [JsonPropertyName("_sVersion")]
    public string? Version { get; init; }

    /// <summary>What the author says this file is, when they said anything.</summary>
    [JsonPropertyName("_sDescription")]
    public string? Description { get; init; }

    /// <summary>The site's virus scan verdict, for example <c>clean</c>.</summary>
    [JsonPropertyName("_sAvResult")]
    public string? AvResult { get; init; }
}
