using CommunityToolkit.Mvvm.Input;

namespace Xxsm.Desktop.ViewModels.Pages;

/// <summary>One entry on a character view's right-click menu: edit the character, or one of its skins.</summary>
public sealed class CharacterEditTargetViewModel
{
    /// <summary>Creates the entry.</summary>
    /// <param name="text">What the menu says, naming the character or skin.</param>
    /// <param name="command">Opens the Character Manager for it.</param>
    public CharacterEditTargetViewModel(string text, IRelayCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentNullException.ThrowIfNull(command);

        Text = text;
        Command = command;
    }

    /// <summary>What the menu says.</summary>
    public string Text { get; }

    /// <summary>Opens the Character Manager for the character or skin.</summary>
    public IRelayCommand Command { get; }
}
