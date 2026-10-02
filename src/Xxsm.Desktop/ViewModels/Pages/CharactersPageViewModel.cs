using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Core.Settings;
using Xxsm.Desktop.Services;
using Xxsm.Packs.Characters;
using Xxsm.Packs.Merge;
using Xxsm.Packs.Model;

namespace Xxsm.Desktop.ViewModels.Pages;

/// <summary>The character grid: search, attribute chips, sorting, pinning, both skin modes, and portraits.</summary>
public sealed partial class CharactersPageViewModel : PageViewModel, IRefreshablePage, ISearchablePage
{
    private readonly GameContext _game;
    private readonly IGameIconProvider _icons;
    private readonly IFolderLauncher _launcher;
    private readonly IModFileOperations _modFiles;
    private readonly IAppSettingsStore _settings;
    private readonly IPortraitCache _portraits;
    private readonly INotificationService _notifications;
    private readonly ViewModelWorkRunner _runner;
    private readonly ICharacterEditor _characters;
    private readonly SortReviewViewModel _sortReview;
    private readonly ModInstallViewModel _install;
    private readonly CharacterManagerViewModel _manager;
    private readonly PackInstallFollowUp _followUp;
    private readonly Action<CharacterTileViewModel> _openDetail;
    private readonly Func<CancellationToken, Task> _rescan;
    private readonly Func<CancellationToken, Task> _reload;

    private readonly SemaphoreSlim _viewSaveLock = new(1, 1);

    private GameData? _filterGroupsBuiltFor;
    private bool _applyingSavedView;

    /// <summary>Creates the page.</summary>
    public CharactersPageViewModel(
        GameContext game,
        IFolderLauncher launcher,
        IModFileOperations modFiles,
        IAppSettingsStore settings,
        IPortraitCache portraits,
        INotificationService notifications,
        ViewModelWorkRunner runner,
        ICharacterEditor characters,
        SortReviewViewModel sortReview,
        ModInstallViewModel install,
        CharacterManagerViewModel manager,
        PackInstallFollowUp followUp,
        ITextCatalogue text,
        IGameIconProvider icons,
        Action<CharacterTileViewModel> openDetail,
        Func<CancellationToken, Task> rescan,
        Func<CancellationToken, Task> reload)
        : base(text)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(modFiles);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(portraits);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(characters);
        ArgumentNullException.ThrowIfNull(sortReview);
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(followUp);
        ArgumentNullException.ThrowIfNull(icons);
        ArgumentNullException.ThrowIfNull(openDetail);
        ArgumentNullException.ThrowIfNull(rescan);
        ArgumentNullException.ThrowIfNull(reload);

        _game = game;
        _icons = icons;
        _launcher = launcher;
        _modFiles = modFiles;
        _settings = settings;
        _portraits = portraits;
        _notifications = notifications;
        _runner = runner;
        _characters = characters;
        _sortReview = sortReview;
        _install = install;
        _manager = manager;
        _followUp = followUp;
        _openDetail = openDetail;
        _rescan = rescan;
        _reload = reload;
    }

    /// <inheritdoc />
    public override string Heading => Text[nameof(Strings.Characters_Heading)];

    /// <summary>What the page's heading says: the selected game's name, or <em>Characters</em> with no game.</summary>
    public string HeadingText => _game.DisplayName is { Length: > 0 } name ? name : Heading;

    /// <summary>The selected game's icon, before its name in the heading; none with no game.</summary>
    public GameIconViewModel? GameIcon => _game.GameId is { Length: > 0 } id
        ? _icons.Installed(id, _game.DisplayName ?? id)
        : null;

    /// <summary>Whether there is an icon to draw.</summary>
    public bool HasGameIcon => GameIcon is not null;

    /// <summary>The tiles, after search and filters, in sort order with pins first.</summary>
    public ObservableCollection<CharacterTileViewModel> Tiles { get; } = [];

    /// <summary>The filter chips, one group per attribute the pack declares.</summary>
    public ObservableCollection<AttributeFilterGroupViewModel> AttributeFilterGroups { get; } = [];

    /// <summary>Whether the pack declares any attributes to filter by.</summary>
    public bool HasAttributeFilters => AttributeFilterGroups.Count > 0;

    /// <summary>What the user has typed into the search box.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>How the grid is ordered, beyond pinning.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortModeText))]
    private CharacterSortMode _sortMode;

    /// <summary>Whether the sort runs the other way; choosing a sort resets it to that sort's natural way.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortDirectionText))]
    private bool _sortDescending;

    /// <summary>Whether the custom, hidden and no-hashes labels are drawn over the portraits. On by default.</summary>
    [ObservableProperty]
    private bool _showLabels = true;

    /// <summary>Whether characters the user hid are in the grid too, faded, so one can be shown again.</summary>
    [ObservableProperty]
    private bool _showHidden;

    /// <summary>How many characters are hidden from the grid.</summary>
    public int HiddenCount => _game.Data?.Variants.Count(variant => variant.Hidden) ?? 0;

    /// <summary>Whether there is anything hidden, so the chip that shows it is worth drawing.</summary>
    public bool HasHidden => HiddenCount > 0;

    /// <summary>The label on the chip that shows hidden characters.</summary>
    public string ShowHiddenText => Text.Format(nameof(Strings.Characters_ShowHidden), HiddenCount);

    /// <summary>Whether GameBanana is on, so tiles carry the update mark.</summary>
    private bool _showsUpdates;

    /// <summary>Whether the grid shows only the characters with a mod that has an update.</summary>
    [ObservableProperty]
    private bool _updatesOnly;

    /// <summary>How many mods in the Mods folder have an update marked; zero while GameBanana is off.</summary>
    public int UpdateModCount =>
        _showsUpdates ? _game.Inventory?.AllMods.Count(HasUpdate) ?? 0 : 0;

    /// <summary>Whether anything has an update, so the chip that narrows the grid to them is worth drawing.</summary>
    public bool HasUpdateMarks => UpdateModCount > 0;

    /// <summary>The label on that chip.</summary>
    public string UpdatesOnlyText => Text.Format(nameof(Strings.Characters_UpdatesOnly), UpdateModCount);

    /// <summary>The current sort's name, shown on the toolbar's sort button.</summary>
    public string SortModeText => Text[SortMode == CharacterSortMode.ModCount
        ? nameof(Strings.Characters_Sort_ModCount)
        : nameof(Strings.Characters_Sort_Name)];

    /// <summary>Which way the sort runs, for the direction button's tooltip.</summary>
    public string SortDirectionText => Text[SortDescending
        ? nameof(Strings.Characters_Sort_Descending)
        : nameof(Strings.Characters_Sort_Ascending)];

    /// <summary>Whether a pack is installed at all.</summary>
    public bool HasPack => _game.Data is not null;

    /// <summary>Whether a pack is installed but the grid, after filtering, has nothing to show.</summary>
    public bool IsEmptyPack => HasPack && Tiles.Count == 0 && !HasActiveFilter;

    /// <summary>Whether a search or filter is active and matched nothing.</summary>
    public bool IsNoMatches => HasPack && Tiles.Count == 0 && HasActiveFilter;

    /// <summary>Whether there are tiles to show.</summary>
    public bool HasTiles => Tiles.Count > 0;

    /// <summary>Whether the search box or any filter chip is narrowing the grid.</summary>
    public bool HasActiveFilter => IsSearchingOrFiltering || UpdatesOnly;

    /// <summary>Whether search or a filter chip is narrowing the grid; Unsorted and Others never match.</summary>
    private bool IsSearchingOrFiltering =>
        SearchText.Length > 0 || AttributeFilterGroups.Any(group => group.SelectedIds.Count > 0);

    /// <summary>The count line under the heading.</summary>
    public string CountText => CharacterTileCount == 1
        ? Text[nameof(Strings.Characters_Count_One)]
        : Text.Format(nameof(Strings.Characters_Count_Many), CharacterTileCount);

    /// <summary>How many tiles are characters. The <em>Unsorted</em> and <em>Others</em> tiles are not.</summary>
    private int CharacterTileCount => Tiles.Count(tile => !tile.IsPlace);

    /// <summary>Opens the character detail view.</summary>
    /// <param name="tile">The tile that was pressed.</param>
    [RelayCommand]
    public void OpenCharacter(CharacterTileViewModel? tile)
    {
        if (tile is not null)
        {
            _openDetail(tile);
        }
    }

    /// <summary>Opens a character's folder in the desktop's file manager. On the tile's context menu.</summary>
    /// <param name="tile">The tile whose folder should open.</param>
    [RelayCommand]
    public Task OpenFolderAsync(CharacterTileViewModel? tile)
    {
        if (tile is null)
        {
            return Task.CompletedTask;
        }

        // No folder yet is normal for most of a roster: say so rather than open nothing.
        if (tile.FolderPath is not { Length: > 0 } path)
        {
            _notifications.Add(
                NotificationSeverity.Information,
                tile.DisplayName,
                Text.Format(nameof(Strings.Characters_Tile_NoFolder), tile.DisplayName));

            return Task.CompletedTask;
        }

        return _runner.RunAsync(
            tile.DisplayName,
            ct => _launcher.OpenAsync(path, ct),
            ActivationToken);
    }

    /// <summary>Disables every mod filed under a character. On the tile's context menu.</summary>
    /// <param name="tile">The tile whose mods should all be disabled.</param>
    [RelayCommand]
    public Task DisableAllAsync(CharacterTileViewModel? tile)
    {
        if (tile is null || _game.Inventory is not { } inventory)
        {
            return Task.CompletedTask;
        }

        var byName = CountByFolderName(inventory);

        var data = _game.Data;

        IEnumerable<InstalledMod> candidates = tile switch
        {
            { IsUnsorted: true } when data is not null => Xxsm.Packs.Sorting.UnsortedMods.Find(inventory, data),
            { IsOthers: true } when data is not null =>
                Xxsm.Packs.Sorting.UnsortedMods.OthersFolder(inventory, data)?.Mods ?? [],
            _ => tile.Family
                .Select(member => byName.GetValueOrDefault(member.ModFilesName))
                .Where(folder => folder is not null)
                .Cast<VariantFolder>()
                .DistinctBy(folder => folder.Path, PathComparer.Instance)
                .SelectMany(folder => folder.Mods),
        };

        var mods = candidates.Where(mod => mod.IsEnabled).ToList();

        return _runner.RunAsync(
            tile.DisplayName,
            async ct =>
            {
                foreach (var mod in mods)
                {
                    await _modFiles.SetEnabledAsync(mod.Path, enabled: false, ct).ConfigureAwait(true);
                }

                await _rescan(ct).ConfigureAwait(true);
            },
            ActivationToken);
    }

    /// <summary>Pins or unpins a character. The hover control on the tile.</summary>
    /// <param name="tile">The tile to pin or unpin.</param>
    [RelayCommand]
    public Task TogglePinAsync(CharacterTileViewModel? tile)
    {
        if (tile is null || _game.GameId is not { Length: > 0 } gameId)
        {
            return Task.CompletedTask;
        }

        return _runner.RunAsync(
            tile.DisplayName,
            async ct =>
            {
                var updated = await _settings.UpdateGameAsync(
                    gameId,
                    current =>
                    {
                        var pinned = new List<string>(current.PinnedCharacters);

                        if (tile.IsPinned)
                        {
                            pinned.RemoveAll(name =>
                                string.Equals(name, tile.InternalName, StringComparison.OrdinalIgnoreCase));
                        }
                        else if (!pinned.Contains(tile.InternalName, StringComparer.OrdinalIgnoreCase))
                        {
                            pinned.Add(tile.InternalName);
                        }

                        return current with { PinnedCharacters = pinned };
                    },
                    ct).ConfigureAwait(true);

                _game.PinnedCharacters = new HashSet<string>(
                    updated.ForGame(gameId).PinnedCharacters, StringComparer.OrdinalIgnoreCase);
            },
            ActivationToken);
    }

    /// <summary>Shows a hidden character in the grid again, as unticking <em>Hide</em> in the editor does.</summary>
    [RelayCommand]
    private Task UnhideCharacterAsync(CharacterTileViewModel? tile)
    {
        if (tile is null || _game.Data is not { } data)
        {
            return Task.CompletedTask;
        }

        return _runner.RunAsync(
            tile.DisplayName,
            async ct =>
            {
                await _characters
                    .EditAsync(data, tile.InternalName, new CharacterEdit { Hidden = EditField<bool>.To(false) }, ct)
                    .ConfigureAwait(true);

                await _reload(ct).ConfigureAwait(true);
            },
            ActivationToken);
    }

    /// <summary>The sort preview, shown over the grid when a character's auto-sort is asked for.</summary>
    public SortReviewViewModel SortReview => _sortReview;

    /// <summary>The install confirm step, shown over the grid when something is dropped or chosen.</summary>
    public ModInstallViewModel Install => _install;

    /// <summary>Whether something is being dragged across the grid.</summary>
    [ObservableProperty]
    private bool _isDragOver;

    /// <summary>Reads a dropped folder or archive and shows what it would install.</summary>
    /// <param name="source">The folder or archive that was dropped.</param>
    /// <param name="tile">The character it was dropped on, which overrides the sorter, or null for the grid.</param>
    public Task DropAsync(string source, CharacterTileViewModel? tile)
    {
        IsDragOver = false;

        // Dropped on the Unsorted or Others tile is dropped on no character.
        var target = tile is { IsPlace: false } ? tile : null;

        return string.IsNullOrWhiteSpace(source)
            ? Task.CompletedTask
            : _install.OpenAsync(source, target?.InternalName, target?.DisplayName, ActivationToken);
    }

    /// <summary>Chooses a mod folder, or a folder of mods, and reads it.</summary>
    [RelayCommand]
    private async Task AddFromFolderAsync() =>
        await _runner.RunAsync(Heading, ct => _install.PickFolderAsync(null, null, ct), ActivationToken);

    /// <summary>Whether <em>Add mod</em> offers <em>From GameBanana…</em>: switched on, and not turned off.</summary>
    [ObservableProperty]
    private bool _canAddFromGameBanana;

    /// <summary>Whether a paste with nothing focused opens the GameBanana prompt.</summary>
    private bool _pasteStartsLookup;

    /// <summary>Opens the install panel asking for a GameBanana address; auto-sort chooses the character.</summary>
    [RelayCommand]
    private void AddFromGameBanana() => _install.AskForAddress(null, null);

    /// <summary>A paste on the grid with nothing focused: a GameBanana address opens the prompt.</summary>
    /// <returns>A task that completes when the address has been read, or refused.</returns>
    public Task PasteAddressAsync() =>
        _pasteStartsLookup && CanAddFromGameBanana && !IsAnyPanelOpen
            ? _install.PasteAddressAsync(null, null, ActivationToken)
            : Task.CompletedTask;

    /// <summary>Reads what XXSM is allowed to do with GameBanana, for the entries that depend on it.</summary>
    private async Task ReadGameBananaSettingsAsync()
    {
        var gameBanana = (await _settings.ReadAsync(ActivationToken).ConfigureAwait(true)).GameBanana;

        CanAddFromGameBanana = gameBanana.Enabled && gameBanana.AddFromUrlOrDefault;
        _pasteStartsLookup = gameBanana.Enabled && gameBanana.PasteStartsLookupOrDefault;

        // Rebuilt only when it changes, or every portrait reloads for nothing.
        if (_showsUpdates != gameBanana.Enabled)
        {
            _showsUpdates = gameBanana.Enabled;
            RebuildTiles();
        }
    }

    /// <summary>Chooses a mod archive from the file picker and reads it.</summary>
    [RelayCommand]
    private async Task AddFromArchiveAsync() =>
        await _runner.RunAsync(Heading, ct => _install.PickArchiveAsync(null, null, ct), ActivationToken);

    /// <summary>Shows what auto-sort would do for one character: mods to move out of it and into it.</summary>
    [RelayCommand]
    private Task SortCharacterAsync(CharacterTileViewModel? tile) =>
        tile switch
        {
            null => Task.CompletedTask,
            { IsUnsorted: true } => _sortReview.OpenUnsortedAsync(ActivationToken),
            { IsOthers: true } => _sortReview.OpenOthersAsync(ActivationToken),
            _ => _sortReview.OpenAsync(tile.InternalName, tile.DisplayName, ActivationToken),
        };

    // The Character Manager

    /// <summary>The Character Manager, shared with the detail view, so either can edit a character or skin.</summary>
    public CharacterManagerViewModel CharacterManager => _manager;

    /// <summary>Whether anything is covering the grid, so the scrim behind it should be drawn.</summary>
    public bool IsAnyPanelOpen => _manager.IsOpen || SortReview.IsOpen || Install.IsOpen || WhatsNew.IsOpen;

    /// <summary><em>What's new</em> for the selected game's pack when it updated by itself.</summary>
    public WhatsNewViewModel WhatsNew => _followUp.WhatsNew;

    /// <inheritdoc />
    protected override void OnActivated()
    {
        ApplySavedView();

        _game.PropertyChanged += OnGameChanged;

        // Shared singletons: heard while on screen and let go of below, or they keep the page alive.
        _sortReview.PropertyChanged += OnPanelChanged;
        _install.PropertyChanged += OnPanelChanged;
        _manager.PropertyChanged += OnPanelChanged;
        WhatsNew.PropertyChanged += OnPanelChanged;
        _followUp.QueueChanged += OnQueueChanged;

        Rebuild();
        Track(ReadGameBananaSettingsAsync());
        ShowQueuedWhatsNew();
    }

    /// <inheritdoc />
    protected override void OnDeactivated()
    {
        _game.PropertyChanged -= OnGameChanged;
        _sortReview.PropertyChanged -= OnPanelChanged;
        _install.PropertyChanged -= OnPanelChanged;
        _manager.PropertyChanged -= OnPanelChanged;
        WhatsNew.PropertyChanged -= OnPanelChanged;
        _followUp.QueueChanged -= OnQueueChanged;
        _manager.Close();
    }

    private void OnPanelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SortReviewViewModel.IsOpen) or nameof(ModInstallViewModel.IsOpen))
        {
            OnPropertyChanged(nameof(IsAnyPanelOpen));
            ShowQueuedWhatsNew();
        }
    }

    private void OnQueueChanged(object? sender, EventArgs e) => ShowQueuedWhatsNew();

    private void ShowQueuedWhatsNew()
    {
        if (!IsAnyPanelOpen && _game.GameId is { Length: > 0 } gameId)
        {
            _followUp.ShowQueued(gameId);
        }
    }

    partial void OnSearchTextChanged(string value) => RebuildTiles();

    /// <summary>Does nothing: the grid's search box is always shown.</summary>
    public void OpenSearch()
    {
    }

    /// <summary>Rescans the Mods folder, for changes made outside XXSM.</summary>
    [RelayCommand]
    private async Task RefreshAsync() =>
        await _runner.RunAsync(Heading, _rescan, ActivationToken).ConfigureAwait(true);

    /// <summary>Orders the grid by something else.</summary>
    [RelayCommand]
    private void SetSort(CharacterSortMode mode) => SortMode = mode;

    /// <summary>Runs the current sort the other way round.</summary>
    [RelayCommand]
    private void ToggleSortDirection() => SortDescending = !SortDescending;

    partial void OnSortModeChanged(CharacterSortMode value)
    {
        if (_applyingSavedView)
        {
            return;
        }

        var natural = value == CharacterSortMode.ModCount;

        // Changing the direction rebuilds the grid itself.
        if (SortDescending != natural)
        {
            SortDescending = natural;
        }
        else
        {
            RebuildTiles();
            SaveView();
        }
    }

    partial void OnSortDescendingChanged(bool value)
    {
        if (_applyingSavedView)
        {
            return;
        }

        RebuildTiles();
        SaveView();
    }

    partial void OnShowHiddenChanged(bool value)
    {
        if (_applyingSavedView)
        {
            return;
        }

        RebuildTiles();
        SaveView();
    }

    partial void OnShowLabelsChanged(bool value) => SaveView();

    /// <summary>Puts the grid back the way this game was last left; rebuilds nothing itself.</summary>
    private void ApplySavedView()
    {
        var saved = _game.Grid;

        _applyingSavedView = true;

        try
        {
            SortMode = saved.SortBy;
            SortDescending = saved.SortDescending;
            ShowLabels = !saved.HideLabels;
            ShowHidden = saved.ShowHidden;
        }
        finally
        {
            _applyingSavedView = false;
        }
    }

    /// <summary>Saves the grid's order and chips, keeping unknown keys; not cancelled by leaving.</summary>
    private void SaveView()
    {
        if (_applyingSavedView || _game.GameId is not { Length: > 0 } gameId)
        {
            return;
        }

        var wanted = _game.Grid with
        {
            SortBy = SortMode,
            SortDescending = SortDescending,
            HideLabels = !ShowLabels,
            ShowHidden = ShowHidden,
        };

        if (wanted == _game.Grid)
        {
            return;
        }

        _game.Grid = wanted;

        Track(SaveViewAsync(gameId, wanted));
    }

    private async Task SaveViewAsync(string gameId, CharacterGridSettings view)
    {
        await _viewSaveLock.WaitAsync(CancellationToken.None).ConfigureAwait(true);

        try
        {
            await _runner.RunAsync(
                Heading,
                ct => _settings.UpdateGameAsync(gameId, current => current with { Grid = view }, ct),
                CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            _viewSaveLock.Release();
        }
    }

    private void OnGameChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GameContext.GameId) or nameof(GameContext.DisplayName))
        {
            OnPropertyChanged(nameof(HeadingText));
            OnPropertyChanged(nameof(GameIcon));
            OnPropertyChanged(nameof(HasGameIcon));

            ShowQueuedWhatsNew();
            return;
        }

        if (e.PropertyName is nameof(GameContext.Grid))
        {
            // Its own saved view replaces the one on screen, without saving it straight back.
            ApplySavedView();
            return;
        }

        if (e.PropertyName is nameof(GameContext.Data)
            or nameof(GameContext.Inventory)
            or nameof(GameContext.SkinDisplayMode)
            or nameof(GameContext.PinnedCharacters))
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        RebuildFilterGroups();
        RebuildTiles();
    }

    /// <summary>Rebuilds the filter chips, unless the same game data is current, so selected chips survive.</summary>
    private void RebuildFilterGroups()
    {
        if (ReferenceEquals(_game.Data, _filterGroupsBuiltFor))
        {
            return;
        }

        _filterGroupsBuiltFor = _game.Data;
        AttributeFilterGroups.Clear();

        if (_game.Data is not { } data || data.Game.Attributes is not { Count: > 0 } attributes)
        {
            OnPropertyChanged(nameof(HasAttributeFilters));
            return;
        }

        foreach (var (id, definition) in attributes)
        {
            var values = AttributeValues(data, id, definition)
                .Select(value => new AttributeFilterValueViewModel(value.Id, value.DisplayName, RebuildTiles))
                .ToList();

            if (values.Count == 0)
            {
                continue;
            }

            AttributeFilterGroups.Add(new AttributeFilterGroupViewModel(
                id, definition.DisplayName is { Length: > 0 } name ? name : id, values));
        }

        OnPropertyChanged(nameof(HasAttributeFilters));
    }

    private void RebuildTiles()
    {
        // The last hidden character was shown again: the chip and the mode go.
        if (ShowHidden && HiddenCount == 0)
        {
            ShowHidden = false;
            return;
        }

        // The same for the last update dealt with, or GameBanana switched off.
        if (UpdatesOnly && !HasUpdateMarks)
        {
            UpdatesOnly = false;
            return;
        }

        foreach (var tile in Tiles)
        {
            tile.Dispose();
        }

        Tiles.Clear();

        if (_game.Data is { } data)
        {
            var query = new CharacterGridQuery
            {
                SearchText = SearchText,
                AttributeFilters = AttributeFilterGroups.ToDictionary(
                    group => group.Id, group => group.SelectedIds, StringComparer.OrdinalIgnoreCase),
                SortMode = SortMode,
                SortDescending = SortDescending,
                PinnedInternalNames = _game.PinnedCharacters,
                IncludeHidden = ShowHidden,
                CountUpdates = _showsUpdates,
                UpdatesOnly = UpdatesOnly,
            };

            foreach (var tile in Build(data, _game.Inventory, _game.SkinDisplayMode, Text, query, _portraits))
            {
                Tiles.Add(tile);
                Track(tile.LoadPortraitAsync());
            }

            // Unsorted then Others, last, and only while nothing is looking for a character.
            if (!IsSearchingOrFiltering && _game.Inventory is { } inventory)
            {
                if (Xxsm.Packs.Sorting.UnsortedMods.Find(inventory, data) is { Count: > 0 } unsorted)
                {
                    AddPlace(CharacterTileViewModel.Unsorted(
                        unsorted.Count,
                        unsorted.Count(mod => mod.IsEnabled),
                        Text,
                        _showsUpdates ? unsorted.Count(HasUpdate) : 0));
                }

                if (Xxsm.Packs.Sorting.UnsortedMods.OthersFolder(inventory, data) is { Mods.Count: > 0 } others)
                {
                    AddPlace(CharacterTileViewModel.Others(
                        others.Mods.Count,
                        others.EnabledCount,
                        others.Path,
                        Text,
                        _showsUpdates ? others.Mods.Count(HasUpdate) : 0));
                }
            }
        }

        OnPropertyChanged(nameof(HasPack));
        OnPropertyChanged(nameof(HasTiles));
        OnPropertyChanged(nameof(IsEmptyPack));
        OnPropertyChanged(nameof(IsNoMatches));
        OnPropertyChanged(nameof(HasActiveFilter));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(HiddenCount));
        OnPropertyChanged(nameof(HasHidden));
        OnPropertyChanged(nameof(ShowHiddenText));
        OnPropertyChanged(nameof(UpdateModCount));
        OnPropertyChanged(nameof(HasUpdateMarks));
        OnPropertyChanged(nameof(UpdatesOnlyText));
    }

    private void AddPlace(CharacterTileViewModel place)
    {
        if (UpdatesOnly && !place.HasUpdates)
        {
            place.Dispose();
            return;
        }

        Tiles.Add(place);
    }

    partial void OnUpdatesOnlyChanged(bool value) => RebuildTiles();

    /// <summary>Turns the merged game data into tiles, filtered, sorted and pinned. Pure, for tests.</summary>
    /// <param name="data">The merged pack and overlay.</param>
    /// <param name="inventory">What is on disk, or null when the folder has not been scanned.</param>
    /// <param name="mode">Grouped or separate.</param>
    /// <param name="text">The interface's wording, for what a tile says.</param>
    /// <param name="query">Search, filters, sort and pins. Defaults to none of any of them.</param>
    /// <param name="portraits">The portrait cache tiles load from, or null to leave initials.</param>
    /// <returns>The tiles, filtered and ordered.</returns>
    internal static IReadOnlyList<CharacterTileViewModel> Build(
        GameData data,
        ModsInventory? inventory,
        SkinDisplayMode mode,
        ITextCatalogue text,
        CharacterGridQuery? query = null,
        IPortraitCache? portraits = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(text);

        var effectiveQuery = query ?? CharacterGridQuery.Default;
        var counts = CountByFolderName(inventory);

        var shown = effectiveQuery.IncludeHidden ? data.Variants : data.VisibleVariants;

        // A skin of a hidden base, or a hidden skin while hidden ones show, gets a tile of its own.
        var tiles = (mode == SkinDisplayMode.Separate
                ? shown.Select(variant =>
                    Tile(variant, [variant], counts, text, data, portraits, effectiveQuery))
                : shown
                    .Where(variant => IsFamilyTile(data, variant) || variant.Hidden || !HasVisibleBase(data, variant))
                    .Select(variant => IsFamilyTile(data, variant) && !variant.Hidden
                        ? Tile(variant, VisibleFamily(data, variant), counts, text, data, portraits, effectiveQuery)
                        : Tile(variant, [variant], counts, text, data, portraits, effectiveQuery)))
            .Where(tile => Matches(tile, effectiveQuery))
            .Where(tile => !effectiveQuery.UpdatesOnly || tile.HasUpdates);

        // Pinned lead either way; ties on mod count stay A to Z.
        var pinnedFirst = tiles.OrderByDescending(tile => tile.IsPinned);
        var descending = effectiveQuery.SortDescending;

        var ordered = effectiveQuery.SortMode == CharacterSortMode.ModCount
            ? (descending
                    ? pinnedFirst.ThenByDescending(tile => tile.ModCount)
                    : pinnedFirst.ThenBy(tile => tile.ModCount))
                .ThenBy(tile => tile.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(tile => tile.InternalName, StringComparer.OrdinalIgnoreCase)
            : descending
                ? pinnedFirst
                    .ThenByDescending(tile => tile.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ThenByDescending(tile => tile.InternalName, StringComparer.OrdinalIgnoreCase)
                : pinnedFirst
                    .ThenBy(tile => tile.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(tile => tile.InternalName, StringComparer.OrdinalIgnoreCase);

        return [.. ordered];
    }

    /// <summary>The values the chips for one attribute come from: declared, or observed for a number.</summary>
    private static IEnumerable<(string Id, string DisplayName)> AttributeValues(
        GameData data, string attributeId, AttributeDefinition definition)
    {
        if (definition.Values is { Count: > 0 } declared)
        {
            return declared.Select(value =>
                (value.Id, value.DisplayName is { Length: > 0 } name ? name : value.Id));
        }

        return data.VisibleVariants
            .SelectMany(variant => variant.Attributes.TryGetValue(attributeId, out var value) ? value.Ids : [])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => double.TryParse(id, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                ? n
                : double.MaxValue)
            .Select(id => (id, id));
    }

    private static bool Matches(CharacterTileViewModel tile, CharacterGridQuery query)
    {
        if (query.SearchText is { Length: > 0 } search
            && tile.DisplayName.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) < 0)
        {
            return false;
        }

        foreach (var (attributeId, selected) in query.AttributeFilters)
        {
            if (selected.Count == 0)
            {
                continue;
            }

            if (tile.Variant is not { } variant
                || !variant.Attributes.TryGetValue(attributeId, out var value)
                || !value.Ids.Any(selected.Contains))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether a variant heads a tile in grouped mode: its family's default, or alone in it.</summary>
    private static bool IsFamilyTile(GameData data, MergedVariant variant) =>
        variant.IsDefaultVariant || data.GetFamily(variant.FamilyId).Count == 0;

    /// <summary>Whether a skin has a visible base character whose tile it is folded into.</summary>
    private static bool HasVisibleBase(GameData data, MergedVariant variant) =>
        data.GetFamily(variant.FamilyId)
            .Any(member => member.IsDefaultVariant && !member.Hidden && !ReferenceEquals(member, variant));

    private static List<MergedVariant> VisibleFamily(GameData data, MergedVariant variant)
    {
        var members = data.GetFamily(variant.FamilyId).Where(member => !member.Hidden).ToList();

        return members.Count == 0 ? [variant] : members;
    }

    private static CharacterTileViewModel Tile(
        MergedVariant variant,
        IReadOnlyList<MergedVariant> family,
        Dictionary<string, VariantFolder> folders,
        ITextCatalogue text,
        GameData data,
        IPortraitCache? portraits,
        CharacterGridQuery query)
    {
        var total = 0;
        var enabled = 0;
        var updates = 0;
        var clashing = new List<string>();
        string? path = null;

        foreach (var member in family)
        {
            if (!folders.TryGetValue(member.ModFilesName, out var found))
            {
                continue;
            }

            total += found.Mods.Count;
            enabled += found.EnabledCount;

            // Per character or outfit, not per tile: different models can both be on.
            if (found.EnabledCount > 1)
            {
                clashing.Add(member.DisplayName);
            }

            updates += query.CountUpdates ? found.Mods.Count(HasUpdate) : 0;

            // Never a path built here: the scan knows the real one, case and all.
            path ??= found.Path;
        }

        if (folders.TryGetValue(variant.ModFilesName, out var own))
        {
            path = own.Path;
        }

        var isPinned = query.PinnedInternalNames.Contains(variant.InternalName);

        return new CharacterTileViewModel(variant, family, total, enabled, text, path, isPinned, data, portraits)
        {
            UpdateCount = updates,
            ClashingVariants = clashing,
        };
    }

    /// <summary>Whether the last update check left this mod marked as having a newer version.</summary>
    internal static bool HasUpdate(InstalledMod mod) => mod.Config?.GameBanana?.UpdateAvailable == true;

    private static Dictionary<string, VariantFolder> CountByFolderName(ModsInventory? inventory)
    {
        var folders = new Dictionary<string, VariantFolder>(StringComparer.OrdinalIgnoreCase);

        if (inventory is null)
        {
            return folders;
        }

        foreach (var folder in inventory.VariantFolders)
        {
            folders[folder.Name] = folder;
        }

        return folders;
    }
}
