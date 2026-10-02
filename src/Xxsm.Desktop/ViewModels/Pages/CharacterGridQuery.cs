using Xxsm.Core.Settings;

namespace Xxsm.Desktop.ViewModels.Pages;

/// <summary>What the grid should show: search, attribute chips, sort order and pins; kept pure for tests.</summary>
public sealed record CharacterGridQuery
{
    /// <summary>No search, no filters, name order, nothing pinned.</summary>
    public static CharacterGridQuery Default { get; } = new();

    /// <summary>Matched against a tile's display name, case- and culture-insensitively.</summary>
    public string SearchText { get; init; } = string.Empty;

    /// <summary>Selected values by attribute id; a variant matches when any of its values is selected.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> AttributeFilters { get; init; } =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>How the unpinned remainder is ordered.</summary>
    public CharacterSortMode SortMode { get; init; } = CharacterSortMode.Name;

    /// <summary>Whether the sort runs the other way; ties on mod count stay A to Z either way.</summary>
    public bool SortDescending { get; init; }

    /// <summary>Internal names floated to the top of the grid, in front of the sort order.</summary>
    public IReadOnlySet<string> PinnedInternalNames { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether hidden characters are shown too, each on its own tile, so they can be shown again.</summary>
    public bool IncludeHidden { get; init; }

    /// <summary>Whether tiles count mods with a newer version on GameBanana; off while GameBanana is.</summary>
    public bool CountUpdates { get; init; }

    /// <summary>Whether only characters with a mod that has an update are shown.</summary>
    public bool UpdatesOnly { get; init; }
}
