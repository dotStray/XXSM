using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Core.Mods;

namespace Xxsm.Desktop.ViewModels;

/// <summary>One character folder with no mods, and whether it will be deleted.</summary>
public sealed partial class EmptyFolderRowViewModel(VariantFolder folder, ITextCatalogue text) : ObservableObject
{
    private readonly ITextCatalogue _text = text;

    /// <summary>The folder.</summary>
    public VariantFolder Folder { get; } = folder;

    /// <summary>Whether it will be deleted. Every row starts ticked.</summary>
    [ObservableProperty]
    private bool _isSelected = true;

    /// <summary>The folder's name.</summary>
    public string Name => Folder.Name;

    /// <summary>Whether anything else is inside it, which would go to the trash with it.</summary>
    public string DetailText => Folder.OtherEntryCount == 0
        ? _text[nameof(Strings.EmptyFolders_Nothing)]
        : _text.Format(nameof(Strings.EmptyFolders_Holds), _text.Items(Folder.OtherEntryCount));
}

/// <summary>Deletes several empty character folders at once, each ticked; nothing until the button.</summary>
public sealed partial class EmptyFoldersReviewViewModel(
    ITextCatalogue text,
    Func<IReadOnlyList<VariantFolder>, Task> delete) : ObservableObject
{
    private readonly ITextCatalogue _text = text;
    private readonly Func<IReadOnlyList<VariantFolder>, Task> _delete = delete;

    /// <summary>Whether the window is on screen.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>The folders, each with a tickbox.</summary>
    public ObservableCollection<EmptyFolderRowViewModel> Rows { get; } = [];

    /// <summary>How many are ticked.</summary>
    public int SelectedCount => Rows.Count(row => row.IsSelected);

    /// <summary>What the button that deletes them says.</summary>
    public string ApplyText => _text.Format(nameof(Strings.EmptyFolders_Apply), _text.Folders(SelectedCount));

    /// <summary>Whether anything is ticked.</summary>
    public bool CanApply => SelectedCount > 0;

    /// <summary>Shows the window for these folders, all ticked.</summary>
    /// <param name="folders">Character folders with no mods.</param>
    public void Open(IEnumerable<VariantFolder> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);

        Clear();

        foreach (var folder in folders)
        {
            var row = new EmptyFolderRowViewModel(folder, _text);
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        }

        IsOpen = true;
        RaiseCounts();
    }

    /// <summary>Closes the window without deleting anything.</summary>
    [RelayCommand]
    public void Close()
    {
        IsOpen = false;
        Clear();
        RaiseCounts();
    }

    /// <summary>Deletes the ticked folders.</summary>
    [RelayCommand]
    private Task ApplyAsync()
    {
        var chosen = Rows.Where(row => row.IsSelected).Select(row => row.Folder).ToList();

        if (chosen.Count == 0)
        {
            return Task.CompletedTask;
        }

        Close();
        return _delete(chosen);
    }

    /// <summary>Ticks every row.</summary>
    [RelayCommand]
    private void SelectAll() => SetAll(selected: true);

    /// <summary>Unticks every row.</summary>
    [RelayCommand]
    private void SelectNone() => SetAll(selected: false);

    private void SetAll(bool selected)
    {
        foreach (var row in Rows)
        {
            row.IsSelected = selected;
        }
    }

    private void Clear()
    {
        foreach (var row in Rows)
        {
            row.PropertyChanged -= OnRowChanged;
        }

        Rows.Clear();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EmptyFolderRowViewModel.IsSelected))
        {
            RaiseCounts();
        }
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(ApplyText));
        OnPropertyChanged(nameof(CanApply));
    }
}
