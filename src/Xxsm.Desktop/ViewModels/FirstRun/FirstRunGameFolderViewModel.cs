using CommunityToolkit.Mvvm.ComponentModel;

namespace Xxsm.Desktop.ViewModels.FirstRun;

/// <summary>One installed game on setup's Mods folder step: its name, icon and chosen folder.</summary>
public sealed partial class FirstRunGameFolderViewModel : ObservableObject
{
    /// <summary>Creates the row.</summary>
    /// <param name="gameId">The game.</param>
    /// <param name="displayName">Its name, as the pack gives it.</param>
    /// <param name="icon">Its icon.</param>
    /// <param name="modsDirectory">The folder already saved for it, if any.</param>
    public FirstRunGameFolderViewModel(string gameId, string displayName, GameIconViewModel? icon, string? modsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        GameId = gameId;
        DisplayName = displayName;
        Icon = icon;
        _modsDirectory = modsDirectory;
    }

    /// <summary>The game.</summary>
    public string GameId { get; }

    /// <summary>Its name.</summary>
    public string DisplayName { get; }

    /// <summary>Its icon.</summary>
    public GameIconViewModel? Icon { get; }

    /// <summary>The folder saved for this game, or null while there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModsDirectory))]
    private string? _modsDirectory;

    /// <summary>What XXSM found in the folder last chosen, or why it will not do.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    private string? _note;

    /// <summary>Whether a folder is saved for this game.</summary>
    public bool HasModsDirectory => ModsDirectory is { Length: > 0 };

    /// <summary>Whether there is something to say about the folder chosen.</summary>
    public bool HasNote => Note is { Length: > 0 };
}
