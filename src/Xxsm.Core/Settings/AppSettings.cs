using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xxsm.Core.Settings;

/// <summary>Which colour scheme the application uses. Written as a name, not a number.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
public enum AppTheme
{
    /// <summary>Follow the desktop's own light/dark preference. The default.</summary>
    [JsonStringEnumMemberName("system")]
    System = 0,

    /// <summary>Always light, whatever the desktop says.</summary>
    [JsonStringEnumMemberName("light")]
    Light,

    /// <summary>Always dark, whatever the desktop says.</summary>
    [JsonStringEnumMemberName("dark")]
    Dark,
}

/// <summary>How a family of characters is presented in the grid. Moves no files.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SkinDisplayMode>))]
public enum SkinDisplayMode
{
    /// <summary>One tile per base character, counting the family's mods, with a skin selector. The default.</summary>
    [JsonStringEnumMemberName("grouped")]
    Grouped = 0,

    /// <summary>Every variant, base and skin alike, gets its own tile.</summary>
    [JsonStringEnumMemberName("separate")]
    Separate,
}

/// <summary>How the character grid is ordered; pinned characters always come first.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CharacterSortMode>))]
public enum CharacterSortMode
{
    /// <summary>Alphabetical by display name. The default.</summary>
    [JsonStringEnumMemberName("name")]
    Name = 0,

    /// <summary>By how many mods are filed under the character.</summary>
    [JsonStringEnumMemberName("modCount")]
    ModCount,
}

/// <summary>The four user-settable auto-sort constants; null means the application's default.</summary>
public sealed record SortThresholdSettings
{
    /// <summary>Nothing overridden.</summary>
    public static SortThresholdSettings Default { get; } = new();

    /// <summary>How many variants a hash may span before it stops identifying anything.</summary>
    [JsonPropertyName("ambiguityThreshold")]
    public int? AmbiguityThreshold { get; init; }

    /// <summary>The score a family must reach to be accepted at all.</summary>
    [JsonPropertyName("minScore")]
    public int? MinScore { get; init; }

    /// <summary>How far the winner must beat the best candidate from another family.</summary>
    [JsonPropertyName("marginRatio")]
    public double? MarginRatio { get; init; }

    /// <summary>The confidence ceiling applied when the member within a family is a fallback.</summary>
    [JsonPropertyName("defaultVariantConfidenceCeiling")]
    public double? DefaultVariantConfidenceCeiling { get; init; }

    /// <summary>Whether the user has overridden anything at all.</summary>
    [JsonIgnore]
    public bool IsDefault =>
        AmbiguityThreshold is null &&
        MinScore is null &&
        MarginRatio is null &&
        DefaultVariantConfidenceCeiling is null;
}

/// <summary>How the user last left a game's character grid: its order, and the two chips beside it.</summary>
/// <remarks>Every default is <c>default(T)</c>: a key missing from the file reads as that, whatever an initializer
/// says.</remarks>
public sealed record CharacterGridSettings
{
    /// <summary>The grid as it looks before the user has changed anything.</summary>
    public static CharacterGridSettings Default { get; } = new();

    /// <summary>How the unpinned characters are ordered.</summary>
    [JsonPropertyName("sortBy")]
    public CharacterSortMode SortBy { get; init; }

    /// <summary>Whether that order runs the other way round: Z to A, or fewest mods first.</summary>
    [JsonPropertyName("sortDescending")]
    public bool SortDescending { get; init; }

    /// <summary>Whether the labels over the portraits are off; negative so that missing means on.</summary>
    [JsonPropertyName("hideLabels")]
    public bool HideLabels { get; init; }

    /// <summary>Whether characters the user hid are in the grid too, faded.</summary>
    [JsonPropertyName("showHidden")]
    public bool ShowHidden { get; init; }

    /// <summary>Keys written by a different version of XXSM, preserved verbatim.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

/// <summary>What the user has chosen for one game.</summary>
public sealed record GameSettings
{
    private readonly CharacterGridSettings _grid = CharacterGridSettings.Default;

    /// <summary>The game's Mods folder, or null. Not checked on read: its drive may be unmounted.</summary>
    [JsonPropertyName("modsDirectory")]
    public string? ModsDirectory { get; init; }

    /// <summary>How this game's families are shown in the grid.</summary>
    [JsonPropertyName("skinDisplayMode")]
    public SkinDisplayMode SkinDisplayMode { get; init; } = SkinDisplayMode.Grouped;

    /// <summary>How the user last left this game's character grid.</summary>
    /// <remarks>Written as an accessor: a file without the key would otherwise read back null.</remarks>
    [JsonPropertyName("grid")]
    public CharacterGridSettings Grid
    {
        get => _grid;
        init => _grid = value ?? CharacterGridSettings.Default;
    }

    /// <summary>Internal names of the characters pinned to the top of this game's grid.</summary>
    [JsonPropertyName("pinnedCharacters")]
    public IReadOnlyList<string> PinnedCharacters { get; init; } = [];

    /// <summary>Keys written by a different version of XXSM, preserved verbatim.</summary>
    /// <remarks>Settable, not init-only: extension data cannot bind to a generated constructor parameter.</remarks>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

/// <summary>What the application remembers besides pack choices: Mods folders, views and sort tuning.</summary>
public sealed record AppSettings
{
    private readonly SortThresholdSettings _sort = SortThresholdSettings.Default;

    private readonly GameBananaSettings _gameBanana = GameBananaSettings.Default;

    private readonly IReadOnlyDictionary<string, GameSettings> _games =
        new Dictionary<string, GameSettings>(StringComparer.OrdinalIgnoreCase);

    /// <summary>An empty settings file — every default, nothing chosen.</summary>
    public static AppSettings Empty { get; } = new();

    /// <summary>The file format version.</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Which colour scheme to use.</summary>
    [JsonPropertyName("theme")]
    public AppTheme Theme { get; init; } = AppTheme.System;

    /// <summary>Whether the first-run flow has been completed; false puts the user in the wizard.</summary>
    [JsonPropertyName("firstRunCompleted")]
    public bool FirstRunCompleted { get; init; }

    /// <summary>The game selected when the app was last closed, so it reopens where it was.</summary>
    [JsonPropertyName("lastGameId")]
    public string? LastGameId { get; init; }

    /// <summary>Whether Pack Studio's Problems panel is folded to a strip beside the table.</summary>
    [JsonPropertyName("studioProblemsCollapsed")]
    public bool StudioProblemsCollapsed { get; init; }

    /// <summary>Whether Studio's <em>Add character</em> opens the Character Manager: <em>Fast add</em> off.</summary>
    [JsonPropertyName("studioAddOpensEditor")]
    public bool StudioAddOpensEditor { get; init; }

    /// <summary>Whether the menu is hidden behind ☰ in a wide window; a narrow window always starts hidden.</summary>
    [JsonPropertyName("menuHidden")]
    public bool MenuHidden { get; init; }

    /// <summary>Whether Pack Studio is in the rail. Hidden by default; <c>xxsm studio</c> works either way.</summary>
    [JsonPropertyName("showPackStudio")]
    public bool ShowPackStudio { get; init; }

    /// <summary>Whether XXSM keeps from asking if a newer version is out. False, so it asks, by default.</summary>
    [JsonPropertyName("updateCheckOff")]
    public bool UpdateCheckOff { get; init; }

    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> _columnWidths =
        new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal);

    /// <summary>Table column widths the user set, in pixels, by table then column; others keep their own.</summary>
    [JsonPropertyName("columnWidths")]
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> ColumnWidths
    {
        get => _columnWidths;
        init => _columnWidths = value ?? new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal);
    }

    /// <summary>These settings with one table's column widths forgotten, or every table's.</summary>
    /// <param name="table">The table, e.g. <c>studio</c>; null for every table.</param>
    /// <returns>The new settings.</returns>
    public AppSettings WithoutColumnWidths(string? table = null)
    {
        if (table is null)
        {
            return this with { ColumnWidths = new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal) };
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        var tables = new Dictionary<string, IReadOnlyDictionary<string, double>>(ColumnWidths, StringComparer.Ordinal);
        tables.Remove(table);

        return this with { ColumnWidths = tables };
    }

    /// <summary>These settings with some of one table's column widths replaced.</summary>
    /// <param name="table">The table, e.g. <c>studio</c>.</param>
    /// <param name="widths">The columns to set, by column id, in pixels. Others keep what they had.</param>
    /// <returns>The new settings.</returns>
    public AppSettings WithColumnWidths(string table, IReadOnlyDictionary<string, double> widths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentNullException.ThrowIfNull(widths);

        var merged = ColumnWidths.TryGetValue(table, out var existing)
            ? new Dictionary<string, double>(existing, StringComparer.Ordinal)
            : new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var (column, width) in widths)
        {
            if (double.IsFinite(width) && width > 0)
            {
                merged[column] = Math.Round(width, 1);
            }
        }

        var tables = new Dictionary<string, IReadOnlyDictionary<string, double>>(ColumnWidths, StringComparer.Ordinal)
        {
            [table] = merged,
        };

        return this with { ColumnWidths = tables };
    }

    /// <summary>The auto-sort tuning, shared by every game; never null.</summary>
    [JsonPropertyName("sort")]
    public SortThresholdSettings Sort
    {
        get => _sort;
        init => _sort = value ?? SortThresholdSettings.Default;
    }

    /// <summary>Whether XXSM may talk to GameBanana, and how. Off until switched on; never null.</summary>
    [JsonPropertyName("gameBanana")]
    public GameBananaSettings GameBanana
    {
        get => _gameBanana;
        init => _gameBanana = value ?? GameBananaSettings.Default;
    }

    /// <summary>Per-game choices, keyed by game id; never null.</summary>
    [JsonPropertyName("games")]
    public IReadOnlyDictionary<string, GameSettings> Games
    {
        get => _games;
        init => _games = value ?? new Dictionary<string, GameSettings>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Keys written by a different version of XXSM, preserved verbatim.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }

    /// <summary>The settings for one game, or an empty default when it has none.</summary>
    /// <returns>Never null.</returns>
    public GameSettings ForGame(string gameId) =>
        FindKey(gameId) is { } key ? Games[key] : new GameSettings();

    /// <summary>The key in <see cref="Games"/> for this game, ignoring case, or null when there is none.</summary>
    /// <param name="gameId">The game.</param>
    /// <returns>The key exactly as it is stored.</returns>
    /// <remarks>The dictionary read from the file compares exactly, so look keys up through this.</remarks>
    public string? FindKey(string gameId) =>
        Games.Keys.FirstOrDefault(key =>
            string.Equals(key, gameId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>What pasting a GameBanana address onto a mod that is already installed does by default.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GameBananaPasteAction>))]
public enum GameBananaPasteAction
{
    /// <summary>Show what GameBanana says beside what the mod has, and take only what is ticked. The default.</summary>
    [JsonStringEnumMemberName("ask")]
    Ask = 0,

    /// <summary>Fill in the fields that are empty and leave everything the user typed alone.</summary>
    [JsonStringEnumMemberName("fillBlanks")]
    FillBlanks,

    /// <summary>Keep the address and fetch nothing.</summary>
    [JsonStringEnumMemberName("saveOnly")]
    SaveOnly,
}

/// <summary>Everything about talking to GameBanana, behind one switch that starts off.</summary>
/// <remarks>A <c>bool?</c> here means "not written, use the default": read it through the resolved property.</remarks>
public sealed record GameBananaSettings
{
    /// <summary>Every default: the master switch off, and nothing else chosen.</summary>
    public static GameBananaSettings Default { get; } = new();

    /// <summary>The application's own API address, used when the file names none.</summary>
    public const string DefaultApiBaseUrl = "https://gamebanana.com/apiv11/";

    /// <summary>Whether XXSM may contact GameBanana at all. Off until the user says otherwise.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    /// <summary>Whether a mod installed from an address is filled in from its page. Default on.</summary>
    [JsonPropertyName("fetchOnInstall")]
    public bool? FetchOnInstall { get; init; }

    /// <summary>Whether <em>Add mod</em> offers <em>From GameBanana…</em>. Default on.</summary>
    [JsonPropertyName("addFromUrl")]
    public bool? AddFromUrl { get; init; }

    /// <summary>Whether pasting on a character page with nothing focused starts a look-up. Default on.</summary>
    [JsonPropertyName("pasteStartsLookup")]
    public bool? PasteStartsLookup { get; init; }

    /// <summary>What a look-up on an installed mod does. Default <see cref="GameBananaPasteAction.Ask"/>.</summary>
    [JsonPropertyName("pasteIntoExisting")]
    public GameBananaPasteAction PasteIntoExisting { get; init; }

    /// <summary>Whether a mod's preview picture is downloaded along with its words. Default on.</summary>
    [JsonPropertyName("downloadPictures")]
    public bool? DownloadPictures { get; init; }

    /// <summary>Whether the background update checker runs. Off by default.</summary>
    [JsonPropertyName("checkForUpdates")]
    public bool CheckForUpdates { get; init; }

    /// <summary>How often the checker runs, in hours. Default 24, as the specification says.</summary>
    [JsonPropertyName("checkIntervalHours")]
    public double? CheckIntervalHours { get; init; }

    /// <summary>How long a fetched page stays usable from disk, in hours. Default 6, at least 1.</summary>
    [JsonPropertyName("cacheHours")]
    public double? CacheHours { get; init; }

    /// <summary>Seconds between two requests to GameBanana. Default 1, and never less.</summary>
    [JsonPropertyName("secondsBetweenRequests")]
    public double? SecondsBetweenRequests { get; init; }

    /// <summary>How many days a finished download, and its archive, stays on the list. Default 7.</summary>
    [JsonPropertyName("keepDownloadsDays")]
    public double? KeepDownloadsDays { get; init; }

    /// <summary>How many downloads the list keeps at most, newest first. Default 50.</summary>
    [JsonPropertyName("downloadHistory")]
    public int? DownloadHistory { get; init; }

    /// <summary>Whether GameBanana developer tools are offered; only set by hand in this file.</summary>
    [JsonPropertyName("developerTools")]
    public bool DeveloperTools { get; init; }

    /// <summary>The API address, so a future API version is a settings change.</summary>
    [JsonPropertyName("apiBaseUrl")]
    public string? ApiBaseUrl { get; init; }

    /// <summary>Keys written by a different version of XXSM, preserved verbatim.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }

    /// <summary>Whether a mod installed from an address is filled in from its page.</summary>
    [JsonIgnore]
    public bool FetchOnInstallOrDefault => FetchOnInstall ?? true;

    /// <summary>Whether <em>Add mod</em> offers <em>From GameBanana…</em>.</summary>
    [JsonIgnore]
    public bool AddFromUrlOrDefault => AddFromUrl ?? true;

    /// <summary>Whether a paste with nothing focused starts a look-up.</summary>
    [JsonIgnore]
    public bool PasteStartsLookupOrDefault => PasteStartsLookup ?? true;

    /// <summary>Whether a preview picture is downloaded with a look-up.</summary>
    [JsonIgnore]
    public bool DownloadPicturesOrDefault => DownloadPictures ?? true;

    /// <summary>How often the checker runs, clamped to something a site can live with.</summary>
    [JsonIgnore]
    public TimeSpan CheckInterval =>
        TimeSpan.FromHours(Clamp(CheckIntervalHours, 24, 1, 24 * 30));

    /// <summary>How long a fetched page stays usable from disk. Zero means never cache.</summary>
    [JsonIgnore]
    public TimeSpan CacheLifetime => TimeSpan.FromHours(Clamp(CacheHours, 6, MinimumCacheHours, 24 * 7));

    /// <summary>The shortest gap between two requests.</summary>
    [JsonIgnore]
    public TimeSpan RequestGap => TimeSpan.FromSeconds(
        Clamp(SecondsBetweenRequests, MinimumSecondsBetweenRequests, MinimumSecondsBetweenRequests, MaximumSecondsBetweenRequests));

    /// <summary>The shortest wait between two requests anyone can set: one second.</summary>
    public const double MinimumSecondsBetweenRequests = 1;

    /// <summary>The longest wait between two requests that can be set.</summary>
    public const double MaximumSecondsBetweenRequests = 60;

    /// <summary>The least time a fetched page is kept, so a look-up is never asked twice in a row.</summary>
    public const double MinimumCacheHours = 1;

    /// <summary>How long a finished download stays on the list, at least a day.</summary>
    [JsonIgnore]
    public TimeSpan KeepDownloads => TimeSpan.FromDays(Clamp(KeepDownloadsDays, 7, 1, 365));

    /// <summary>How many downloads the list keeps at most.</summary>
    [JsonIgnore]
    public int DownloadHistoryCount => (int)Clamp(DownloadHistory, 50, 5, 500);

    /// <summary>The API address ending in a slash; an address that is not absolute http(s) is ignored.</summary>
    [JsonIgnore]
    public Uri ApiBase =>
        Uri.TryCreate(ApiBaseUrl?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? (uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/"))
            : new Uri(DefaultApiBaseUrl);

    private static double Clamp(double? value, double fallback, double min, double max) =>
        value is { } chosen && double.IsFinite(chosen) ? Math.Clamp(chosen, min, max) : fallback;
}
