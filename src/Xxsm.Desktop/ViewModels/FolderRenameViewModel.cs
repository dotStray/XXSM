using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Core;
using Xxsm.Core.Mods;
using Xxsm.Desktop.Services;

namespace Xxsm.Desktop.ViewModels;

/// <summary>One of the folders that differ only by capitals, which could be the one renamed.</summary>
public sealed partial class RenameChoiceViewModel : ObservableObject
{
    /// <summary>Creates the choice.</summary>
    /// <param name="path">The folder's path.</param>
    /// <param name="name">Its name, exactly as it is on disk.</param>
    public RenameChoiceViewModel(string path, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Path = path;
        Name = name;
    }

    /// <summary>The folder's path.</summary>
    public string Path { get; }

    /// <summary>Its name, exactly as it is on disk.</summary>
    public string Name { get; }

    /// <summary>Whether this is the folder to rename.</summary>
    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>Renames one of two folders that differ only by capitals; offered, never forced.</summary>
public sealed partial class FolderRenameViewModel(
    IModFileOperations files,
    INotificationService notifications,
    ViewModelWorkRunner runner,
    ITextCatalogue text,
    Func<CancellationToken, Task> rescan) : ObservableObject
{
    private readonly IModFileOperations _files = files;
    private readonly INotificationService _notifications = notifications;
    private readonly ViewModelWorkRunner _runner = runner;
    private readonly ITextCatalogue _text = text;
    private readonly Func<CancellationToken, Task> _rescan = rescan;

    /// <summary>Whether the window is on screen.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>The folders that differ only by capitals.</summary>
    public ObservableCollection<RenameChoiceViewModel> Choices { get; } = [];

    /// <summary>The folder that will be renamed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRename))]
    private RenameChoiceViewModel? _selected;

    /// <summary>The name it will get.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRename))]
    private string _newName = string.Empty;

    /// <summary>Why the rename was refused, in the file operation's own words. Null otherwise.</summary>
    [ObservableProperty]
    private string? _error;

    /// <summary>Whether a rename is under way.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRename))]
    private bool _isRenaming;

    /// <summary>Whether there is a folder chosen and a different name for it.</summary>
    public bool CanRename =>
        Selected is { } choice
        && !IsRenaming
        && NewName.Trim() is { Length: > 0 } wanted
        && !string.Equals(wanted, choice.Name, StringComparison.Ordinal);

    /// <summary>Shows the window with the first folder chosen and its name ready to edit.</summary>
    /// <param name="choices">The folders that differ only by capitals.</param>
    public void Open(IReadOnlyList<RenameChoiceViewModel> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);

        Clear();

        foreach (var choice in choices)
        {
            choice.PropertyChanged += OnChoiceChanged;
            Choices.Add(choice);
        }

        if (Choices.Count > 0)
        {
            Choices[0].IsSelected = true;
        }

        IsOpen = true;
    }

    /// <summary>Closes the window without renaming anything.</summary>
    [RelayCommand]
    public void Close()
    {
        IsOpen = false;
        Clear();
    }

    /// <summary>Renames the chosen folder.</summary>
    [RelayCommand]
    private Task RenameAsync()
    {
        if (Selected is not { } choice || !CanRename)
        {
            return Task.CompletedTask;
        }

        var wanted = NewName.Trim();

        return _runner.RunAsync(
            _text[nameof(Strings.FolderRename_Heading)],
            async ct =>
            {
                IsRenaming = true;

                try
                {
                    var result = await _files.RenameAsync(choice.Path, wanted, ct).ConfigureAwait(true);

                    Close();
                    await _rescan(ct).ConfigureAwait(true);

                    if (result.Changed)
                    {
                        _notifications.Add(
                            NotificationSeverity.Information,
                            _text[nameof(Strings.Findings_Renamed_Title)],
                            _text.Format(nameof(Strings.Findings_Renamed), choice.Name, result.ToName));
                    }
                }
                catch (ModOperationException exception)
                {
                    // A taken name is the expected refusal: said where the name was typed, and the window stays open.
                    Error = exception.Message;
                }
                finally
                {
                    IsRenaming = false;
                }
            },
            CancellationToken.None);
    }

    partial void OnNewNameChanged(string value) => Error = null;

    private void OnChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RenameChoiceViewModel.IsSelected)
            || sender is not RenameChoiceViewModel { IsSelected: true } choice)
        {
            return;
        }

        foreach (var other in Choices.Where(other => !ReferenceEquals(other, choice)))
        {
            other.IsSelected = false;
        }

        Selected = choice;
        NewName = choice.Name;
    }

    private void Clear()
    {
        foreach (var choice in Choices)
        {
            choice.PropertyChanged -= OnChoiceChanged;
        }

        Choices.Clear();
        Selected = null;
        NewName = string.Empty;
        Error = null;
    }
}
