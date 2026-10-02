using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Core.Profiles;
using Xxsm.Desktop.Services;

namespace Xxsm.Desktop.ViewModels;

/// <summary>What a profile's list of mods asks the Profiles page to do.</summary>
internal interface IProfileModsActions
{
    /// <summary>Every mod in the Mods folder, as the last scan found them.</summary>
    IReadOnlyList<InstalledMod> AllMods();

    /// <summary>The character a folder in the Mods folder is for, by name, or null.</summary>
    string? CharacterOf(string? folder);

    Task RemoveAsync(ProfileMember member);

    void GoTo(string modFolder);

    Task AddAsync(IReadOnlyList<string> modFolders);

    Task ReplaceAsync(ProfileMember member, string modFolder);
}

/// <summary>One profile's mods as tiles, with a way to each, out of the profile, or to a replacement.</summary>
/// <remarks>Adding mods and finding a replacement choose from every mod in the folder, in the same panel.</remarks>
public sealed partial class ProfileModsViewModel : ObservableObject, IDisposable
{
    private readonly ITextCatalogue _text;
    private readonly IModThumbnailCache _thumbnails;
    private readonly IProfileModsActions _actions;
    private readonly List<ModTileViewModel> _all = [];
    private readonly List<ModTileViewModel> _candidates = [];
    private ProfileMember? _replacing;
    private CancellationTokenSource _pictures = new();
    private CancellationTokenSource _pickPictures = new();

    internal ProfileModsViewModel(ITextCatalogue text, IModThumbnailCache thumbnails, IProfileModsActions actions)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(thumbnails);
        ArgumentNullException.ThrowIfNull(actions);

        _text = text;
        _thumbnails = thumbnails;
        _actions = actions;
    }

    /// <summary>Whether the panel is showing.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>The profile shown, or null.</summary>
    public ModProfile? Profile { get; private set; }

    /// <summary>The profile's name.</summary>
    public string Heading => Profile?.Name ?? string.Empty;

    /// <summary>How many mods it has.</summary>
    public string Subtitle => _text.Mods(_all.Count);

    /// <summary>Whether it is read-only, so nothing can be added, replaced or taken out.</summary>
    public bool IsReadOnly => Profile?.ReadOnly == true;

    /// <summary>Whether it can be changed.</summary>
    public bool CanEdit => Profile is { ReadOnly: false };

    /// <summary>What the search box over the profile's tiles holds.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>The profile's mods, after the search.</summary>
    public ObservableCollection<ModTileViewModel> Tiles { get; } = [];

    /// <summary>Whether the profile has no mods at all.</summary>
    public bool IsEmpty => _all.Count == 0;

    /// <summary>Whether the search matched nothing, though there are mods.</summary>
    public bool HasNoMatches => _all.Count > 0 && Tiles.Count == 0;

    // Choosing mods: Add mods… and Find replacement…

    /// <summary>Whether the panel is showing the picker instead of the profile's mods.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShowingProfile))]
    private bool _isPicking;

    /// <summary>Whether the panel is showing the profile's mods.</summary>
    public bool IsShowingProfile => !IsPicking;

    /// <summary>Whether the picker is choosing one mod for a missing one, rather than mods to add.</summary>
    public bool IsReplacing => _replacing is not null;

    /// <summary>The picker's heading: <em>Add mods to Abyss team</em>, or <em>Replace Old Outfit</em>.</summary>
    public string PickHeading => _replacing is { } member
        ? _text.Format(nameof(Strings.ProfileMods_Replace_Heading), member.Entry.Name ?? PathDisplay.Show(member.Entry.Path))
        : _text.Format(nameof(Strings.ProfileMods_Add_Heading), Heading);

    /// <summary>What the picker is for, in a sentence.</summary>
    public string PickBody => _text[IsReplacing ? nameof(Strings.ProfileMods_Replace_Body) : nameof(Strings.ProfileMods_Add_Body)];

    /// <summary>What the picker's search box holds.</summary>
    [ObservableProperty]
    private string _pickSearchText = string.Empty;

    /// <summary>Every mod the profile does not have, after the search.</summary>
    public ObservableCollection<ModTileViewModel> PickTiles { get; } = [];

    /// <summary>Whether there is nothing the profile does not have already.</summary>
    public bool HasNoCandidates => _candidates.Count == 0;

    /// <summary>Whether the picker's search matched nothing.</summary>
    public bool HasNoPickMatches => _candidates.Count > 0 && PickTiles.Count == 0;

    /// <summary>The picker's button: <em>Add 3 mods</em>, or <em>Use this mod</em>.</summary>
    public string ConfirmPickText
    {
        get
        {
            var picked = _candidates.Count(tile => tile.IsPicked);
            return IsReplacing
                ? _text[nameof(Strings.ProfileMods_Replace_Confirm)]
                : picked == 0
                    ? _text[nameof(Strings.ProfileMods_Add_None)]
                    : _text.Format(nameof(Strings.ProfileMods_Add_Confirm), _text.Mods(picked));
        }
    }

    /// <summary>Shows a profile, or shows it again after a change, keeping the search.</summary>
    /// <param name="contents">The profile and its mods.</param>
    public Task Show(ProfileContents contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        Release(_all, ref _pictures);
        Profile = contents.Profile;
        _all.Clear();

        foreach (var member in contents.Members)
        {
            _all.Add(ModTileViewModel.ForMember(member, _actions.CharacterOf(member.Mod?.VariantFolderName), CanEdit, _text, this));
        }

        Filter();
        IsOpen = true;
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(IsEmpty));
        AddModsCommand.NotifyCanExecuteChanged();

        return LoadPicturesAsync(_all, _pictures.Token);
    }

    /// <summary>Closes the panel and lets go of every picture.</summary>
    [RelayCommand]
    public void Close()
    {
        CancelPick();
        IsOpen = false;
        Release(_all, ref _pictures);
        _all.Clear();
        Tiles.Clear();
        Profile = null;
        SearchText = string.Empty;
    }

    /// <summary>Opens the picker to add mods: every mod the profile does not have.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task AddModsAsync() => StartPicking(null);

    /// <summary>Opens the picker to choose one mod for a missing one.</summary>
    internal Task StartReplacing(ProfileMember member) => CanEdit ? StartPicking(member) : Task.CompletedTask;

    private Task StartPicking(ProfileMember? replacing)
    {
        _replacing = replacing;
        Release(_candidates, ref _pickPictures);
        _candidates.Clear();

        var already = _all.Where(tile => tile.Mod is not null).Select(tile => tile.Mod!.Path).ToHashSet(StringComparer.Ordinal);

        foreach (var (mod, character) in _actions.AllMods()
                     .Where(mod => !already.Contains(mod.Path))
                     .Select(mod => (Mod: mod, Character: _actions.CharacterOf(mod.VariantFolderName)))
                     .OrderBy(pair => pair.Character ?? pair.Mod.VariantFolderName ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(pair => pair.Mod.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            _candidates.Add(ModTileViewModel.ForCandidate(mod, character, _text, this));
        }

        PickSearchText = string.Empty;
        FilterPick();
        IsPicking = true;
        OnPropertyChanged(nameof(IsReplacing));
        OnPropertyChanged(nameof(PickHeading));
        OnPropertyChanged(nameof(PickBody));
        OnPropertyChanged(nameof(HasNoCandidates));
        RefreshPicked();

        return LoadPicturesAsync(_candidates, _pickPictures.Token);
    }

    /// <summary>Goes back to the profile's mods without changing anything.</summary>
    [RelayCommand]
    private void CancelPick()
    {
        IsPicking = false;
        _replacing = null;
        Release(_candidates, ref _pickPictures);
        _candidates.Clear();
        PickTiles.Clear();
        OnPropertyChanged(nameof(IsReplacing));
    }

    /// <summary>Adds the chosen mods, or puts the chosen one in the missing one's place.</summary>
    [RelayCommand(CanExecute = nameof(CanConfirmPick))]
    private async Task ConfirmPickAsync()
    {
        var picked = _candidates.Where(tile => tile.IsPicked).Select(tile => tile.Mod!.Path).ToList();
        var replacing = _replacing;
        CancelPick();

        if (replacing is not null)
        {
            await _actions.ReplaceAsync(replacing, picked[0]).ConfigureAwait(true);
        }
        else
        {
            await _actions.AddAsync(picked).ConfigureAwait(true);
        }
    }

    private bool CanConfirmPick() => _candidates.Any(tile => tile.IsPicked);

    /// <summary>A tile in the picker was pressed: picked, or not; one at a time when replacing.</summary>
    internal void TogglePick(ModTileViewModel tile)
    {
        if (IsReplacing)
        {
            foreach (var other in _candidates.Where(other => !ReferenceEquals(other, tile)))
            {
                other.IsPicked = false;
            }
        }

        tile.IsPicked = !tile.IsPicked;
        RefreshPicked();
    }

    internal Task RemoveAsync(ModTileViewModel tile) => _actions.RemoveAsync(tile.Member!);

    internal void GoTo(ModTileViewModel tile) => _actions.GoTo(tile.Mod!.Path);

    private void RefreshPicked()
    {
        OnPropertyChanged(nameof(ConfirmPickText));
        ConfirmPickCommand.NotifyCanExecuteChanged();
    }

    partial void OnSearchTextChanged(string value) => Filter();

    partial void OnPickSearchTextChanged(string value) => FilterPick();

    private void Filter()
    {
        Refill(Tiles, _all, SearchText);
        OnPropertyChanged(nameof(HasNoMatches));
    }

    private void FilterPick()
    {
        Refill(PickTiles, _candidates, PickSearchText);
        OnPropertyChanged(nameof(HasNoPickMatches));
    }

    private static void Refill(ObservableCollection<ModTileViewModel> shown, List<ModTileViewModel> all, string search)
    {
        var words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        shown.Clear();

        foreach (var tile in all.Where(tile => words.All(tile.Matches)))
        {
            shown.Add(tile);
        }
    }

    /// <summary>Decodes each tile's picture, one at a time.</summary>
    private async Task LoadPicturesAsync(List<ModTileViewModel> tiles, CancellationToken cancellationToken)
    {
        foreach (var tile in tiles.ToList())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (tile.Mod is not { } mod)
            {
                continue;
            }

            IBitmapLease? lease;

            try
            {
                lease = await _thumbnails.AcquireAsync(mod, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                lease?.Dispose();
                return;
            }

            tile.TakePicture(lease);
        }
    }

    private static void Release(List<ModTileViewModel> tiles, ref CancellationTokenSource loading)
    {
        loading.Cancel();
        loading.Dispose();
        loading = new CancellationTokenSource();

        foreach (var tile in tiles)
        {
            tile.TakePicture(null);
        }
    }

    /// <summary>Lets go of the pictures for good.</summary>
    public void Dispose()
    {
        Release(_all, ref _pictures);
        Release(_candidates, ref _pickPictures);
        _pictures.Dispose();
        _pickPictures.Dispose();
    }
}

/// <summary>One mod as a tile: in a profile's list, or in the picker.</summary>
public sealed partial class ModTileViewModel : ObservableObject
{
    private IBitmapLease? _lease;

    private ModTileViewModel(InstalledMod? mod, ProfileMember? member, string name, string detail, bool canEdit, string? addedText)
    {
        Mod = mod;
        Member = member;
        Name = name;
        Detail = detail;
        CanEdit = canEdit;
        AddedText = addedText;
        Initial = name.Length > 0 ? name[..1].ToUpper(CultureInfo.CurrentCulture) : "?";
    }

    internal static ModTileViewModel ForMember(
        ProfileMember member, string? character, bool canEdit, ITextCatalogue text, ProfileModsViewModel panel)
    {
        var name = member.Mod?.DisplayName ?? member.Entry.Name ?? member.Entry.Path;
        var detail = member.Mod is not { } mod
            ? text.Format(nameof(Strings.ProfileMods_Missing), PathDisplay.Show(member.Entry.Path))
            : OnOrOff(text, mod, character);
        var added = member.Entry.AddedAt is { } at
            ? text.Format(nameof(Strings.ProfileMods_Added), Resources.TextDates.Date(at))
            : null;

        var tile = new ModTileViewModel(member.Mod, member, name, detail, canEdit, added);

        tile.OpenCommand = new AsyncRelayCommand(
            () => tile.IsMissing ? panel.StartReplacing(member) : Go(panel, tile),
            () => !tile.IsMissing || canEdit);
        tile.GoToCommand = new RelayCommand(() => panel.GoTo(tile), () => !tile.IsMissing);
        tile.RemoveCommand = new AsyncRelayCommand(() => panel.RemoveAsync(tile), () => canEdit);
        tile.ReplaceCommand = new AsyncRelayCommand(() => panel.StartReplacing(member), () => canEdit);

        return tile;
    }

    internal static ModTileViewModel ForCandidate(InstalledMod mod, string? character, ITextCatalogue text, ProfileModsViewModel panel)
    {
        var tile = new ModTileViewModel(mod, member: null, mod.DisplayName, OnOrOff(text, mod, character), canEdit: true, addedText: null);
        tile.OpenCommand = new RelayCommand(() => panel.TogglePick(tile));
        return tile;
    }

    private static string OnOrOff(ITextCatalogue text, InstalledMod mod, string? character) =>
        text.Format(mod.IsEnabled ? nameof(Strings.ProfileMods_On) : nameof(Strings.ProfileMods_Off),
            character ?? mod.VariantFolderName ?? string.Empty);

    private static Task Go(ProfileModsViewModel panel, ModTileViewModel tile)
    {
        panel.GoTo(tile);
        return Task.CompletedTask;
    }

    /// <summary>The mod on disk, or null for a profile's entry whose mod has gone.</summary>
    public InstalledMod? Mod { get; }

    /// <summary>The profile's entry, for a tile in a profile's list; null in the picker.</summary>
    public ProfileMember? Member { get; }

    /// <summary>The mod's name, or what it was called when the profile last saw it.</summary>
    public string Name { get; }

    /// <summary>Stands in for the picture.</summary>
    public string Initial { get; }

    /// <summary>Its character and whether it is on now, or where it was if it has gone.</summary>
    public string Detail { get; }

    /// <summary>When it was added by itself, or null.</summary>
    public string? AddedText { get; }

    /// <summary>Whether there is an added date to show.</summary>
    public bool HasAddedText => AddedText is not null;

    /// <summary>Whether the mod is not in the Mods folder any more.</summary>
    public bool IsMissing => Member is { IsMissing: true };

    /// <summary>Whether the mod is here: the tile's line is in the muted colour, not the warning one.</summary>
    public bool IsPresent => !IsMissing;

    /// <summary>Whether this is a tile in a profile's list, with a menu; false in the picker.</summary>
    public bool IsMember => Member is not null;

    /// <summary>Whether the profile can be changed.</summary>
    public bool CanEdit { get; }

    /// <summary>Whether the tile is chosen in the picker.</summary>
    [ObservableProperty]
    private bool _isPicked;

    /// <summary>The mod's picture, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPicture))]
    private Bitmap? _picture;

    /// <summary>Whether there is a picture to show.</summary>
    public bool HasPicture => Picture is not null;

    /// <summary>Pressing the tile: go to the mod, or replace a gone one; in the picker, choose it.</summary>
    public IRelayCommand OpenCommand { get; private set; } = null!;

    /// <summary>Opens the mod on its character's page. Null in the picker.</summary>
    public IRelayCommand? GoToCommand { get; private set; }

    /// <summary>Takes the mod out of the profile. Null in the picker.</summary>
    public IAsyncRelayCommand? RemoveCommand { get; private set; }

    /// <summary>Chooses another mod for this one's place. Null in the picker.</summary>
    public IAsyncRelayCommand? ReplaceCommand { get; private set; }

    /// <summary>Whether a word of the search is in the name or the line under it.</summary>
    internal bool Matches(string word) =>
        Name.Contains(word, StringComparison.CurrentCultureIgnoreCase) ||
        Detail.Contains(word, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>Holds a picture's lease, letting go of the one before; null lets go of all.</summary>
    internal void TakePicture(IBitmapLease? lease)
    {
        _lease?.Dispose();
        _lease = lease;
        Picture = lease?.Bitmap;
    }
}
