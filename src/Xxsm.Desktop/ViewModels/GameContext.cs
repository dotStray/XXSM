using CommunityToolkit.Mvvm.ComponentModel;
using Xxsm.Core.Mods;
using Xxsm.Core.Settings;
using Xxsm.Packs.Merge;

namespace Xxsm.Desktop.ViewModels;

/// <summary>Which game the shell is showing, and what the pages derive from it; filled in by the shell.</summary>
public sealed partial class GameContext : ObservableObject
{
    /// <summary>The selected game, or null when no pack is installed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGame))]
    private string? _gameId;

    /// <summary>Its name, as the pack gives it.</summary>
    [ObservableProperty]
    private string? _displayName;

    /// <summary>The installed pack version.</summary>
    [ObservableProperty]
    private string? _packVersion;

    /// <summary>The merged pack and overlay, or null while it is loading or when nothing is installed.</summary>
    [ObservableProperty]
    private GameData? _data;

    /// <summary>The game's Mods folder, or null when the user has not chosen one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModsDirectory))]
    private string? _modsDirectory;

    /// <summary>What is in that folder, or null when it has not been scanned.</summary>
    [ObservableProperty]
    private ModsInventory? _inventory;

    /// <summary>How this game's families are shown in the grid.</summary>
    [ObservableProperty]
    private SkinDisplayMode _skinDisplayMode = SkinDisplayMode.Grouped;

    /// <summary>Internal names of the characters pinned to the top of the grid.</summary>
    [ObservableProperty]
    private IReadOnlySet<string> _pinnedCharacters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>How the user last left this game's grid: its order and two chips, saved as it changes.</summary>
    [ObservableProperty]
    private CharacterGridSettings _grid = CharacterGridSettings.Default;

    /// <summary>Whether a game is selected at all.</summary>
    public bool HasGame => GameId is { Length: > 0 };

    /// <summary>Whether the user has pointed this game at a Mods folder.</summary>
    public bool HasModsDirectory => ModsDirectory is { Length: > 0 };
}
