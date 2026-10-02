using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Core.Mods;
using Xxsm.Packs.Merge;

namespace Xxsm.Desktop.ViewModels;

/// <summary>Asks where a custom character's mods go before it is deleted, even when it has none.</summary>
public sealed partial class CharacterDeleteViewModel : ObservableObject
{
    private readonly ITextCatalogue _text;
    private readonly IReadOnlyList<CharacterChoiceViewModel> _allTargets;

    /// <summary>Creates the panel.</summary>
    /// <param name="text">The interface's wording.</param>
    /// <param name="data">The merged game data, for the characters mods could move to.</param>
    /// <param name="variant">The character being deleted.</param>
    /// <param name="modCount">How many mods are filed under it.</param>
    public CharacterDeleteViewModel(
        ITextCatalogue text, GameData data, MergedVariant variant, int modCount)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(variant);

        _text = text;
        Variant = variant;
        ModCount = modCount;

        _allTargets =
        [
            .. data.VisibleVariants
                .Where(candidate => !string.Equals(
                    candidate.InternalName, variant.InternalName, StringComparison.OrdinalIgnoreCase))
                .Select(candidate => new CharacterChoiceViewModel(candidate.InternalName, candidate.DisplayName))
                .OrderBy(candidate => candidate.DisplayName, StringComparer.CurrentCultureIgnoreCase),
        ];

        RefreshTargets();
    }

    /// <summary>The character being deleted.</summary>
    public MergedVariant Variant { get; }

    /// <summary>How many mods are filed under it.</summary>
    public int ModCount { get; }

    /// <summary>Whether anything would have to move.</summary>
    public bool HasMods => ModCount > 0;

    /// <summary>The panel's title.</summary>
    public string Heading => _text.Format(nameof(Strings.CharacterDelete_Heading), Variant.DisplayName);

    /// <summary>What will happen to the mods, in plain language.</summary>
    public string Body => ModCount == 0
        ? _text[nameof(Strings.CharacterDelete_NoMods)]
        : _text.ForCount(ModCount, nameof(Strings.CharacterDelete_Mods_One), nameof(Strings.CharacterDelete_Mods),
            _text.Mods(ModCount));

    /// <summary>Whether the mods should go to a character rather than to <c>Others/</c>.</summary>
    [ObservableProperty]
    private bool _toCharacter;

    /// <summary>The character chosen to take the mods, when one has been.</summary>
    [ObservableProperty]
    private CharacterChoiceViewModel? _target;

    /// <summary>What the user has typed to narrow the character list.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>The characters the mods could move to, after the search box narrows them.</summary>
    public ObservableCollection<CharacterChoiceViewModel> Targets { get; } = [];

    /// <summary>The internal name to re-home to, or null for <c>Others/</c>.</summary>
    public string? RehomeTo => ToCharacter ? Target?.InternalName : null;

    /// <summary>Where the mods will actually land, for the button's own label.</summary>
    public string DestinationText =>
        ToCharacter ? Target?.DisplayName ?? string.Empty : ModsFolderLayout.UnsortedFolderName;

    /// <summary>Whether the panel has enough to act on.</summary>
    public bool CanConfirm => !HasMods || !ToCharacter || Target is not null;

    /// <summary>Chooses a character for the mods to move to.</summary>
    [RelayCommand]
    private void Choose(CharacterChoiceViewModel? choice)
    {
        if (choice is not null)
        {
            Target = choice;
            ToCharacter = true;
        }
    }

    partial void OnSearchTextChanged(string value) => RefreshTargets();

    partial void OnToCharacterChanged(bool value)
    {
        OnPropertyChanged(nameof(RehomeTo));
        OnPropertyChanged(nameof(DestinationText));
        OnPropertyChanged(nameof(CanConfirm));
    }

    partial void OnTargetChanged(CharacterChoiceViewModel? value)
    {
        OnPropertyChanged(nameof(RehomeTo));
        OnPropertyChanged(nameof(DestinationText));
        OnPropertyChanged(nameof(CanConfirm));
    }

    private void RefreshTargets()
    {
        Targets.Clear();

        foreach (var candidate in _allTargets)
        {
            if (SearchText.Length == 0
                || candidate.DisplayName.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase))
            {
                Targets.Add(candidate);
            }
        }
    }
}
