using CommunityToolkit.Mvvm.ComponentModel;
using Xxsm.Packs.Registry;

namespace Xxsm.Desktop.ViewModels;

/// <summary>One installed version of a game's pack, as a row under its card on the Game Packs page.</summary>
public sealed partial class PackVersionViewModel : ObservableObject
{
    private readonly Action<PackVersionViewModel, bool>? _keepChanged;

    /// <summary>Creates the row.</summary>
    /// <param name="game">The card it belongs to.</param>
    /// <param name="packVersion">The installed version.</param>
    /// <param name="isInUse">Whether it is the version the game uses.</param>
    /// <param name="isNewest">Whether it is the newest installed.</param>
    /// <param name="isKept">Whether the user marked it to keep.</param>
    /// <param name="keepChanged">Saves a change to <see cref="IsKept"/>; null where nothing is saved.</param>
    public PackVersionViewModel(
        PackChoiceViewModel game,
        string packVersion,
        bool isInUse,
        bool isNewest,
        bool isKept = false,
        Action<PackVersionViewModel, bool>? keepChanged = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentException.ThrowIfNullOrWhiteSpace(packVersion);

        Game = game;
        PackVersion = packVersion;
        IsInUse = isInUse;
        IsNewest = isNewest;
        _isKept = isKept;
        _keepChanged = keepChanged;
    }

    /// <summary>The card it belongs to: the game, and its name for a notice.</summary>
    public PackChoiceViewModel Game { get; }

    /// <summary>The version, as stored: what <em>Use</em>, <em>Keep</em> and <em>Delete</em> act on.</summary>
    public string PackVersion { get; }

    /// <summary>The version as a person reads it, <c>26.9.25.1</c>.</summary>
    public string VersionText => PackVersionText.Display(PackVersion);

    /// <summary>Whether the game uses this version now.</summary>
    public bool IsInUse { get; }

    /// <summary>Whether it is the newest installed, which is used unless another is chosen.</summary>
    public bool IsNewest { get; }

    /// <summary>Whether <em>Use</em> would change anything.</summary>
    public bool CanUse => !IsInUse;

    /// <summary>Whether the user marked this version to keep, so it is never pruned; ticking saves it.</summary>
    [ObservableProperty]
    private bool _isKept;

    partial void OnIsKeptChanged(bool value) => _keepChanged?.Invoke(this, value);
}
