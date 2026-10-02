using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Core.Mods;
using Xxsm.Packs.Merge;

namespace Xxsm.Desktop.ViewModels;

/// <summary>One candidate character in a character picker.</summary>
public sealed class MoveTargetViewModel
{
    /// <summary>Creates a candidate.</summary>
    /// <param name="variant">The character.</param>
    /// <param name="existingFolderPath">Its folder, or null when nothing is filed under it yet.</param>
    public MoveTargetViewModel(MergedVariant variant, string? existingFolderPath)
    {
        ArgumentNullException.ThrowIfNull(variant);

        InternalName = variant.InternalName;
        DisplayName = variant.DisplayName;
        ModFilesName = variant.ModFilesName;
        ExistingFolderPath = existingFolderPath;
    }

    /// <summary>Creates the one candidate that is a place rather than a character: <c>Others</c>.</summary>
    /// <param name="displayName">What to call it in the list.</param>
    /// <param name="existingFolderPath">The Others folder as spelled on disk, or null when there is none.</param>
    /// <returns>The candidate; its <see cref="InternalName"/> is the Others folder's name.</returns>
    public static MoveTargetViewModel Others(string displayName, string? existingFolderPath = null) =>
        new(ModsFolderLayout.UnsortedFolderName, displayName, existingFolderPath);

    private MoveTargetViewModel(string folderName, string displayName, string? existingFolderPath)
    {
        InternalName = folderName;
        DisplayName = displayName;
        ModFilesName = folderName;
        ExistingFolderPath = existingFolderPath;
        IsOthers = true;
    }

    /// <summary>The character's id, or the Others folder's name for <see cref="Others"/>.</summary>
    public string InternalName { get; }

    /// <summary>Whether this is <c>Others</c> rather than a character.</summary>
    public bool IsOthers { get; }

    /// <summary>The name to show in the picker.</summary>
    public string DisplayName { get; }

    /// <summary>The canonical folder name auto-sort would file this character under.</summary>
    public string ModFilesName { get; }

    /// <summary>The character's folder, or null when it would have to be created.</summary>
    public string? ExistingFolderPath { get; }
}

/// <summary>Choose a character from a searchable list; choosing is the action, the caller decides the rest.</summary>
public sealed partial class CharacterPickerViewModel : ObservableObject
{
    private IReadOnlyList<MoveTargetViewModel> _all = [];
    private Func<MoveTargetViewModel, Task>? _choose;

    /// <summary>Whether the picker is showing.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>What the picker is for: "Move to…", or "Character for March 7th".</summary>
    [ObservableProperty]
    private string _heading = string.Empty;

    /// <summary>What has been typed into the search box.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>The candidates left after the search box narrows them.</summary>
    public ObservableCollection<MoveTargetViewModel> Targets { get; } = [];

    /// <summary>Whether any candidate is left.</summary>
    public bool HasTargets => Targets.Count > 0;

    /// <summary>Shows the picker.</summary>
    /// <param name="heading">What it is for.</param>
    /// <param name="candidates">The characters to choose from, in the order to show them.</param>
    /// <param name="choose">What choosing one does. The picker has closed by the time it runs.</param>
    public void Open(string heading, IEnumerable<MoveTargetViewModel> candidates, Func<MoveTargetViewModel, Task> choose)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(choose);

        Heading = heading;
        _all = [.. candidates];
        _choose = choose;
        SearchText = string.Empty;
        Refresh();
        IsOpen = true;
    }

    /// <summary>Closes the picker without choosing.</summary>
    [RelayCommand]
    public void Cancel()
    {
        IsOpen = false;
        _choose = null;
    }

    /// <summary>Chooses a character.</summary>
    [RelayCommand]
    private Task ChooseAsync(MoveTargetViewModel? target)
    {
        var choose = _choose;
        Cancel();

        return target is null || choose is null ? Task.CompletedTask : choose(target);
    }

    partial void OnSearchTextChanged(string value) => Refresh();

    private void Refresh()
    {
        Targets.Clear();

        foreach (var target in _all.Where(target =>
                     SearchText.Length == 0 ||
                     target.DisplayName.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)))
        {
            Targets.Add(target);
        }

        OnPropertyChanged(nameof(HasTargets));
    }
}
