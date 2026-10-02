using System.Text.Json.Serialization;

namespace Xxsm.Packs.Registry;

/// <summary>The user's choices about where packs come from and which versions they want.</summary>
public sealed record PackPreferences
{
    private readonly IReadOnlyList<string> _registries = [];
    private readonly IReadOnlyDictionary<string, PackGamePreference> _games =
        new Dictionary<string, PackGamePreference>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The file format version.</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    /// <summary>The registries to consult, in order; later ones win ties. Empty means the default.</summary>
    [JsonPropertyName("registries")]
    public IReadOnlyList<string> Registries
    {
        get => _registries;
        init => _registries = value ?? [];
    }

    /// <summary>Days an installed version stays after a newer one before it goes to the trash; 30 by default.</summary>
    /// <remarks>Zero or less keeps every version. The version in use, the newest and kept ones never go.</remarks>
    [JsonIgnore]
    public int RemoveOldVersionsAfterDays => StoredRemoveOldVersionsAfterDays ?? DefaultRemoveOldVersionsAfterDays;

    /// <summary><see cref="RemoveOldVersionsAfterDays"/> as the file holds it; null when unsaid.</summary>
    [JsonPropertyName("removeOldVersionsAfterDays")]
    public int? StoredRemoveOldVersionsAfterDays { get; init; }

    /// <summary>What <see cref="RemoveOldVersionsAfterDays"/> is when the file does not say.</summary>
    public const int DefaultRemoveOldVersionsAfterDays = 30;

    /// <summary>Whether the app installs a newer pack by itself, for every game; off by default.</summary>
    [JsonIgnore]
    public bool AutoUpdate => StoredAutoUpdate ?? false;

    /// <summary><see cref="AutoUpdate"/> as the file holds it; null, which means off, when unsaid.</summary>
    [JsonPropertyName("autoUpdate")]
    public bool? StoredAutoUpdate { get; init; }

    /// <summary>Per-game choices, keyed by game id.</summary>
    [JsonPropertyName("games")]
    public IReadOnlyDictionary<string, PackGamePreference> Games
    {
        get => _games;
        init => _games = value ?? new Dictionary<string, PackGamePreference>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The preference for one game, or an empty default when it has none.</summary>
    /// <returns>Never null.</returns>
    public PackGamePreference ForGame(string gameId) =>
        FindKey(gameId) is { } key ? Games[key] : new PackGamePreference();

    /// <summary>The key in <see cref="Games"/> for this game, whatever its casing, or null.</summary>
    /// <remarks>A key read from the file is not case-insensitive, so look it up through this.</remarks>
    public string? FindKey(string gameId) =>
        Games.Keys.FirstOrDefault(key =>
            string.Equals(key, gameId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>What the user has chosen for one game.</summary>
public sealed record PackGamePreference
{
    /// <summary>The version the user has pinned, or null when they take the newest. Holds this game alone.</summary>
    [JsonPropertyName("pinnedVersion")]
    public string? PinnedVersion { get; init; }

    private readonly IReadOnlyList<string> _keptVersions = [];

    /// <summary>Installed versions marked to keep, which are never removed for being old.</summary>
    [JsonPropertyName("keptVersions")]
    public IReadOnlyList<string> KeptVersions
    {
        get => _keptVersions;
        init => _keptVersions = value ?? [];
    }

    /// <summary>Whether one version is marked to keep.</summary>
    /// <returns>True when it is.</returns>
    public bool Keeps(string packVersion) =>
        KeptVersions.Contains(packVersion, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether this game may be updated at all: it is not pinned.</summary>
    [JsonIgnore]
    public bool UpdatesAllowed => PinnedVersion is not { Length: > 0 };
}
