using System.Text.Json.Serialization;

namespace Xxsm.Packs.Downloads;

/// <summary>What became of one download.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DownloadState>))]
public enum DownloadState
{
    /// <summary>It is being fetched now.</summary>
    Running,

    /// <summary>The archive is on disk and nothing has been installed from it yet.</summary>
    Ready,

    /// <summary>It was installed. The archive has been removed; the entry says where it went.</summary>
    Installed,

    /// <summary>The user stopped it, or closed XXSM while it was running.</summary>
    Cancelled,

    /// <summary>It could not be fetched. <see cref="DownloadRecord.Error"/> says why.</summary>
    Failed,

    /// <summary>GameBanana wanted a browser for it.</summary>
    Blocked,
}

/// <summary>One line of the download list: what was fetched, where it went, and what the page said.</summary>
public sealed record DownloadRecord
{
    /// <summary>This download's own id, unique within the list.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>The GameBanana mod id it came from.</summary>
    [JsonPropertyName("modId")]
    public required long ModId { get; init; }

    /// <summary>What to call it. The page's title, or the file name when the page had none.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Who submitted it.</summary>
    [JsonPropertyName("author")]
    public string? Author { get; init; }

    /// <summary>The author's own version string, when the page carries one.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>The page's description, as plain text.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>The mod's page.</summary>
    [JsonPropertyName("pageUrl")]
    public string? PageUrl { get; init; }

    /// <summary>Where the preview picture came from.</summary>
    [JsonPropertyName("pictureUrl")]
    public string? PictureUrl { get; init; }

    /// <summary>The preview picture, saved beside the archive.</summary>
    [JsonPropertyName("picturePath")]
    public string? PicturePath { get; init; }

    /// <summary>Upstream's last-changed value, for the update checker's baseline.</summary>
    [JsonPropertyName("dateModifiedTs")]
    public long? DateModifiedTs { get; init; }

    /// <summary>Which of the mod's files was taken.</summary>
    [JsonPropertyName("fileId")]
    public long? FileId { get; init; }

    /// <summary>That file's name.</summary>
    [JsonPropertyName("fileName")]
    public string? FileName { get; init; }

    /// <summary>How big the file is altogether, when the server said.</summary>
    [JsonPropertyName("totalBytes")]
    public long? TotalBytes { get; init; }

    /// <summary>How many bytes arrived.</summary>
    [JsonPropertyName("bytes")]
    public long Bytes { get; init; }

    /// <summary>The checksum upstream published for the file, when it published one.</summary>
    [JsonPropertyName("md5")]
    public string? Md5 { get; init; }

    /// <summary>Whether the bytes matched <see cref="Md5"/>; false when upstream published none to check.</summary>
    [JsonPropertyName("md5Verified")]
    public bool Md5Verified { get; init; }

    /// <summary>What became of it.</summary>
    [JsonPropertyName("state")]
    public required DownloadState State { get; init; }

    /// <summary>The archive on disk, while there is one.</summary>
    [JsonPropertyName("archivePath")]
    public string? ArchivePath { get; init; }

    /// <summary>Why it failed, in the site's or the system's own words.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>When it started.</summary>
    [JsonPropertyName("startedAt")]
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>When it stopped, however it stopped.</summary>
    [JsonPropertyName("finishedAt")]
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>The game whose Mods folder it was started from.</summary>
    [JsonPropertyName("gameId")]
    public string? GameId { get; init; }

    /// <summary>The character it was dropped on, when the user named one.</summary>
    [JsonPropertyName("targetVariantId")]
    public string? TargetVariantId { get; init; }

    /// <summary>That character's name, for the install card's title.</summary>
    [JsonPropertyName("targetDisplayName")]
    public string? TargetDisplayName { get; init; }

    /// <summary>Where it was installed, once it was.</summary>
    [JsonPropertyName("installedPath")]
    public string? InstalledPath { get; init; }

    /// <summary>Whether the archive is still there to install from.</summary>
    [JsonIgnore]
    public bool HasArchive =>
        State == DownloadState.Ready && ArchivePath is { Length: > 0 } path && File.Exists(path);

    /// <summary>Whether this download is still going.</summary>
    [JsonIgnore]
    public bool IsRunning => State == DownloadState.Running;
}
