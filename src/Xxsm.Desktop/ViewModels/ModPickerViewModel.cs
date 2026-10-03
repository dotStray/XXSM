using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Core.Mods;
using Xxsm.Desktop.Services;

namespace Xxsm.Desktop.ViewModels;

/// <summary>What one use of the mod picker says and does.</summary>
/// <param name="Heading">The picker's heading.</param>
/// <param name="Body">What it is for, in a sentence.</param>
/// <param name="NothingText">What it says when there is no mod to choose from.</param>
/// <param name="SingleChoice">Whether one mod is chosen at a time.</param>
/// <param name="ConfirmText">The confirm button's text for how many mods are picked.</param>
/// <param name="Candidates">The mods to choose from, in the order shown, each with its character's name or null.</param>
/// <param name="Confirm">What choosing does, given the picked mods; the picker is closed first.</param>
/// <param name="OutsideText">The text of a button at the bottom for going elsewhere instead, or null for none.</param>
/// <param name="Outside">What that button does; the picker is closed first.</param>
internal sealed record ModPickerRequest(
    string Heading,
    string Body,
    string NothingText,
    bool SingleChoice,
    Func<int, string> ConfirmText,
    IReadOnlyList<(InstalledMod Mod, string? Character)> Candidates,
    Func<IReadOnlyList<InstalledMod>, Task> Confirm,
    string? OutsideText = null,
    Func<Task>? Outside = null);

/// <summary>Every mod in the Mods folder as tiles with their pictures, searchable, to choose one or several from.</summary>
public sealed partial class ModPickerViewModel : ObservableObject, IDisposable
{
    private readonly ITextCatalogue _text;
    private readonly IModThumbnailCache _thumbnails;
    private readonly List<ModTileViewModel> _candidates = [];
    private ModPickerRequest? _request;
    private CancellationTokenSource _pictures = new();

    internal ModPickerViewModel(ITextCatalogue text, IModThumbnailCache thumbnails)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(thumbnails);
        _text = text;
        _thumbnails = thumbnails;
    }

    /// <summary>Whether the picker is showing.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>The picker's heading.</summary>
    public string Heading => _request?.Heading ?? string.Empty;

    /// <summary>What the picker is for, in a sentence.</summary>
    public string Body => _request?.Body ?? string.Empty;

    /// <summary>What the picker says when there is nothing to choose from.</summary>
    public string NothingText => _request?.NothingText ?? string.Empty;

    /// <summary>What the search box holds.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>The mods to choose from, after the search.</summary>
    public ObservableCollection<ModTileViewModel> Tiles { get; } = [];

    /// <summary>Whether there is no mod to choose from.</summary>
    public bool HasNoCandidates => _candidates.Count == 0;

    /// <summary>Whether the search matched nothing.</summary>
    public bool HasNoMatches => _candidates.Count > 0 && Tiles.Count == 0;

    /// <summary>The confirm button's text for what is picked.</summary>
    public string ConfirmText => _request?.ConfirmText(_candidates.Count(tile => tile.IsPicked)) ?? string.Empty;

    /// <summary>The text of the button for going elsewhere, or null.</summary>
    public string? OutsideText => _request?.OutsideText;

    /// <summary>Whether there is a button for going elsewhere.</summary>
    public bool HasOutside => _request?.Outside is not null;

    /// <summary>Shows the picker with its own words and mods, nothing picked.</summary>
    /// <returns>A task that completes when every picture has been read.</returns>
    internal Task OpenAsync(ModPickerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        ModTiles.Release(_candidates, ref _pictures);
        _candidates.Clear();
        _request = request;

        foreach (var (mod, character) in request.Candidates)
        {
            _candidates.Add(ModTileViewModel.ForCandidate(mod, character, _text, this));
        }

        SearchText = string.Empty;
        Filter();
        IsOpen = true;
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(Body));
        OnPropertyChanged(nameof(NothingText));
        OnPropertyChanged(nameof(HasNoCandidates));
        OnPropertyChanged(nameof(OutsideText));
        OnPropertyChanged(nameof(HasOutside));
        RefreshPicked();

        return ModTiles.LoadPicturesAsync(_thumbnails, _candidates, _pictures.Token);
    }

    /// <summary>Closes the picker without choosing anything, and lets go of its pictures.</summary>
    [RelayCommand]
    public void Cancel()
    {
        IsOpen = false;
        _request = null;
        ModTiles.Release(_candidates, ref _pictures);
        _candidates.Clear();
        Tiles.Clear();
    }

    /// <summary>Closes the picker and hands over the picked mods.</summary>
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        if (_request is not { } request)
        {
            return;
        }

        var picked = _candidates.Where(tile => tile.IsPicked).Select(tile => tile.Mod!).ToList();
        Cancel();
        await request.Confirm(picked).ConfigureAwait(true);
    }

    private bool CanConfirm() => _candidates.Any(tile => tile.IsPicked);

    /// <summary>Closes the picker and goes elsewhere instead.</summary>
    [RelayCommand]
    private async Task OutsideAsync()
    {
        if (_request?.Outside is not { } outside)
        {
            return;
        }

        Cancel();
        await outside().ConfigureAwait(true);
    }

    /// <summary>A tile was pressed: picked, or not; the others let go when one is chosen at a time.</summary>
    internal void TogglePick(ModTileViewModel tile)
    {
        if (_request is { SingleChoice: true })
        {
            foreach (var other in _candidates.Where(other => !ReferenceEquals(other, tile)))
            {
                other.IsPicked = false;
            }
        }

        tile.IsPicked = !tile.IsPicked;
        RefreshPicked();
    }

    partial void OnSearchTextChanged(string value) => Filter();

    private void Filter()
    {
        ModTiles.Refill(Tiles, _candidates, SearchText);
        OnPropertyChanged(nameof(HasNoMatches));
    }

    private void RefreshPicked()
    {
        OnPropertyChanged(nameof(ConfirmText));
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Lets go of the pictures for good.</summary>
    public void Dispose()
    {
        ModTiles.Release(_candidates, ref _pictures);
        _pictures.Dispose();
    }
}
