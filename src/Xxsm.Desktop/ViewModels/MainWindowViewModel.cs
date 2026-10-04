using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Core;
using Xxsm.Core.GameBanana;
using Xxsm.Core.Mods;
using Xxsm.Core.Settings;
using Xxsm.Desktop.Services;
using Xxsm.Desktop.ViewModels.FirstRun;
using Xxsm.Desktop.ViewModels.Pages;
using Xxsm.Packs.Installation;
using Xxsm.Packs.Merge;
using Xxsm.Packs.Registry;

namespace Xxsm.Desktop.ViewModels;

/// <summary>The shell: the rail, the game selector and the page on screen. It alone loads the game.</summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly IGameIconProvider _icons;
    private readonly IAppSettingsStore _settings;
    private readonly IPackService _packs;
    private readonly IPackInstaller _installer;
    private readonly IGameDataService _gameData;
    private readonly IModRepository _mods;
    private readonly UpdateCheckRun _updates;
    private readonly IThemeApplier _themeApplier;
    private readonly INotificationService _notifications;
    private readonly ViewModelWorkRunner _runner;
    private readonly ITextCatalogue _text;
    private readonly NavigationItemViewModel _charactersItem;
    private readonly NavigationItemViewModel _notificationsItem;
    private readonly NavigationItemViewModel _modsItem;
    private readonly NavigationItemViewModel _packsItem;
    private readonly PackUpdateWatcher _packUpdates;
    private readonly AppUpdateWatcher _appUpdates;
    private readonly NavigationItemViewModel _settingsItem;
    private readonly StudioPageViewModel _studio;
    private readonly PacksPageViewModel _packsPage;
    private readonly NavigationItemViewModel _studioItem;
    private readonly StudioVisibility _studioVisibility;
    private readonly ColumnWidthMemory _columnWidths;
    private readonly IReadOnlyList<NavigationItemViewModel> _allItems;
    private readonly SortReviewViewModel _sortReview;
    private readonly ModInstallViewModel _install;

    /// <summary>What is downloading now, and what has been.</summary>
    public DownloadsViewModel Downloads { get; }

    /// <summary>The notices popped up over the window.</summary>
    public NoticePopupsViewModel NoticePopups { get; }

    private bool _isSwitchingGame;

    private bool _isDetailRemembered;

    /// <summary>Creates the shell.</summary>
    public MainWindowViewModel(
        GameContext game,
        IAppSettingsStore settings,
        IPackService packs,
        IPackInstaller installer,
        IGameDataService gameData,
        IModRepository mods,
        UpdateCheckRun updates,
        IThemeApplier themeApplier,
        INotificationService notifications,
        ViewModelWorkRunner runner,
        ITextCatalogue text,
        ShellPages pages,
        FirstRunViewModel firstRun,
        CharacterDetailPageViewModel detail,
        IGameIconProvider icons,
        StudioVisibility studioVisibility,
        ColumnWidthMemory columnWidths,
        DownloadsViewModel downloads,
        SortReviewViewModel sortReview,
        ModInstallViewModel install,
        PackUpdateWatcher packUpdates,
        AppUpdateWatcher appUpdates,
        NoticePopupsViewModel noticePopups)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(mods);
        ArgumentNullException.ThrowIfNull(themeApplier);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(firstRun);
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(icons);
        ArgumentNullException.ThrowIfNull(studioVisibility);
        ArgumentNullException.ThrowIfNull(columnWidths);
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(sortReview);
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(packUpdates);
        _packUpdates = packUpdates;
        ArgumentNullException.ThrowIfNull(appUpdates);
        _appUpdates = appUpdates;
        ArgumentNullException.ThrowIfNull(noticePopups);
        NoticePopups = noticePopups;
        _columnWidths = columnWidths;
        _sortReview = sortReview;
        _install = install;
        Downloads = downloads;

        Game = game;
        _icons = icons;
        _settings = settings;
        _packs = packs;
        _installer = installer;
        _gameData = gameData;
        _mods = mods;
        _updates = updates;
        _themeApplier = themeApplier;
        _notifications = notifications;
        _runner = runner;
        _text = text;
        FirstRun = firstRun;
        Detail = detail;
        _studio = pages.Studio;
        _packsPage = pages.Packs;

        Title = $"{AppInfo.DisplayName} {AppInfo.ShortVersion}";
        VersionText = text.Format(nameof(Strings.Common_Version), AppInfo.ShortVersion);

        _notificationsItem = new NavigationItemViewModel(
            _text[nameof(Strings.Nav_Notifications)], "notices", pages.Notifications);

        _modsItem = new NavigationItemViewModel(_text[nameof(Strings.Nav_Mods)], "mods", pages.Mods);

        _packsItem = new NavigationItemViewModel(_text[nameof(Strings.Nav_Packs)], "packs", pages.Packs);

        _studioItem = new NavigationItemViewModel(_text[nameof(Strings.Nav_Studio)], "studio", pages.Studio);

        _settingsItem = new NavigationItemViewModel(
            _text[nameof(Strings.Nav_Settings)], "settings", pages.Settings);

        _charactersItem = new NavigationItemViewModel(
            _text[nameof(Strings.Nav_Characters)], "characters", pages.Characters);

        _allItems =
        [
            _charactersItem,
            _modsItem,
            _packsItem,
            _studioItem,
            new NavigationItemViewModel(_text[nameof(Strings.Nav_Profiles)], "profiles", pages.Profiles),
            _notificationsItem,
            _settingsItem,
        ];

        _studioVisibility = studioVisibility;
        NavigationItems = [.. _allItems.Where(item => item != _studioItem || studioVisibility.IsShown)];

        // Both are singletons: this subscription lives as long as the shell.
        studioVisibility.PropertyChanged += OnStudioVisibilityChanged;

        game.PropertyChanged += OnGameChanged;

        _selectedNavigationItem = NavigationItems[0];
        _currentPage = NavigationItems[0].Page;
    }

    /// <summary>The window title. The short version: the full one carries a commit hash.</summary>
    public string Title { get; }

    /// <summary>"version 1.0.0", for the footer.</summary>
    public string VersionText { get; }

    /// <summary>The selected game and everything derived from it.</summary>
    public GameContext Game { get; }

    /// <summary>The setup flow.</summary>
    public FirstRunViewModel FirstRun { get; }

    /// <summary>The character detail view.</summary>
    public CharacterDetailPageViewModel Detail { get; }

    /// <summary>The rail's entries, in order. Pack Studio's is here only while it is switched on.</summary>
    public ObservableCollection<NavigationItemViewModel> NavigationItems { get; }

    /// <summary>The installed games, which is what the selector offers.</summary>
    public ObservableCollection<GameOptionViewModel> Games { get; } = [];

    /// <summary>Whether any game has a pack installed.</summary>
    public bool HasGames => Games.Count > 0;

    /// <summary>Whether the setup flow is showing instead of the shell.</summary>
    [ObservableProperty]
    private bool _isFirstRun;

    /// <summary>The rail entry that is selected.</summary>
    [ObservableProperty]
    private NavigationItemViewModel? _selectedNavigationItem;

    /// <summary>The page on screen.</summary>
    [ObservableProperty]
    private PageViewModel? _currentPage;

    /// <summary>Whether anything long-running is in flight, the shell's or the page's, as one answer.</summary>
    public bool IsWorking => IsBusy || CurrentPage is { IsBusy: true };

    /// <summary>What is happening, for the indicator. Never empty while it is showing.</summary>
    public string WorkingMessage =>
        BusyMessage ?? CurrentPage?.BusyMessage ?? _text[nameof(Strings.Shell_Busy)];

    /// <summary>The game the selector is showing.</summary>
    [ObservableProperty]
    private GameOptionViewModel? _selectedGame;

    /// <summary>Starts the shell: settings, theme, installed games; no network before the first frame.</summary>
    public Task InitializeAsync() =>
        _runner.RunAsync(
            AppInfo.DisplayName,
            async ct =>
            {
                var settings = await _settings.ReadAsync(ct).ConfigureAwait(true);

                _themeApplier.Apply(settings.Theme);
                _menuHiddenWhenWide = settings.MenuHidden;
                IsMenuOpen = !IsNarrow && !_menuHiddenWhenWide;
                _studioVisibility.IsShown = settings.ShowPackStudio;
                _columnWidths.Load(settings);
                IsFirstRun = !settings.FirstRunCompleted;

                await LoadGamesAsync(settings, ct).ConfigureAwait(true);

                await Downloads.LoadAsync(ct).ConfigureAwait(true);

                // Never the version in use, so nothing loaded above changes under the grid.
                await _packsPage.RemoveOldVersionsAsync(null, ct).ConfigureAwait(true);

                if (!IsFirstRun)
                {
                    _packUpdates.Start();
                }

                _appUpdates.Start();
            },
            ActivationToken);

    /// <summary>Rebuilds the game selector from what is installed, keeping the selection if it survives.</summary>
    public async Task ReloadGamesAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.ReadAsync(cancellationToken).ConfigureAwait(true);

        await LoadGamesAsync(settings, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Reloads the selected game's pack and rescans its Mods folder.</summary>
    public async Task ReloadGameAsync(CancellationToken cancellationToken)
    {
        if (SelectedGame is not { } selected)
        {
            Game.GameId = null;
            Game.DisplayName = null;
            Game.PackVersion = null;
            Game.Data = null;
            Game.Inventory = null;
            Game.ModsDirectory = null;
            return;
        }

        var settings = await _settings.ReadAsync(cancellationToken).ConfigureAwait(true);
        var forGame = settings.ForGame(selected.GameId);

        Game.GameId = selected.GameId;
        Game.DisplayName = selected.DisplayName;
        Game.PackVersion = selected.PackVersion;
        Game.SkinDisplayMode = forGame.SkinDisplayMode;
        Game.PinnedCharacters = new HashSet<string>(forGame.PinnedCharacters, StringComparer.OrdinalIgnoreCase);
        Game.Grid = forGame.Grid;
        Game.ModsDirectory = forGame.ModsDirectory;

        var packDirectory = await _installer
            .FindActivePackDirectoryAsync(selected.GameId, cancellationToken)
            .ConfigureAwait(true);

        // No pack directory is legitimate: the overlay alone can hold characters.
        Game.Data = await _gameData
            .LoadAsync(selected.GameId, packDirectory, cancellationToken)
            .ConfigureAwait(true);

        await RescanModsAsync(cancellationToken).ConfigureAwait(true);
        await CheckForUpdatesAsync(settings, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Runs the background update check when switched on and a mod is due; a failure only warns.</summary>
    private async Task CheckForUpdatesAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (settings.GameBanana is not { Enabled: true, CheckForUpdates: true }
            || Game.ModsDirectory is not { Length: > 0 } directory)
        {
            return;
        }

        // Shared with the pages' Check now, and not cancelled by switching game.
        cancellationToken.ThrowIfCancellationRequested();
        await _updates.RunAsync(directory, force: false).ConfigureAwait(true);
    }

    /// <summary>Opens the Mods page, for a notice that has a list to point at.</summary>
    [RelayCommand]
    private Task ShowModsPageAsync()
    {
        ShowMods();

        return Task.CompletedTask;
    }

    /// <summary>Rescans the selected game's Mods folder.</summary>
    public async Task RescanModsAsync(CancellationToken cancellationToken)
    {
        if (Game.ModsDirectory is not { Length: > 0 } directory)
        {
            Game.Inventory = null;
            return;
        }

        try
        {
            Game.Inventory = await _mods.ScanAsync(directory, cancellationToken).ConfigureAwait(true);
        }
        catch (ModOperationException ex)
        {
            Game.Inventory = null;

            _notifications.Add(NotificationSeverity.Warning, _text[nameof(Strings.Mods_Heading)], ex.Message);
        }
    }

    /// <summary>Sends the user to the Game Pack browser.</summary>
    [RelayCommand]
    public void ShowPacks() => SelectedNavigationItem = _packsItem;

    /// <summary>Sends the user to the Mods page.</summary>
    [RelayCommand]
    public void ShowMods() => SelectedNavigationItem = _modsItem;

    /// <summary>The installed game with this name in letters and digits, ignoring capitals and punctuation, or null.</summary>
    public GameOptionViewModel? InstalledGameNamed(string displayName)
    {
        ArgumentNullException.ThrowIfNull(displayName);

        return Games.FirstOrDefault(option => GameBananaGameNames.IsSame(displayName, null, option.DisplayName, null));
    }

    /// <summary>Selects another installed game and waits until it has loaded; an unknown one changes nothing.</summary>
    public async Task SwitchGameAsync(string gameId)
    {
        if (Games.FirstOrDefault(option => string.Equals(option.GameId, gameId, StringComparison.OrdinalIgnoreCase))
                is not { } wanted
            || ReferenceEquals(wanted, SelectedGame))
        {
            return;
        }

        SelectedGame = wanted;
        await WhenIdleAsync().ConfigureAwait(true);
    }

    /// <summary>Sends the user to one game's Mods page, switching game first when another is selected.</summary>
    /// <param name="gameId">The game; null, or one no longer installed, opens the current game's.</param>
    public void ShowMods(string? gameId)
    {
        if (gameId is { Length: > 0 }
            && !string.Equals(SelectedGame?.GameId, gameId, StringComparison.OrdinalIgnoreCase)
            && Games.FirstOrDefault(option => string.Equals(option.GameId, gameId, StringComparison.OrdinalIgnoreCase))
                is { } wanted)
        {
            SelectedGame = wanted;
        }

        ShowMods();
    }

    /// <summary>Opens the install panel on every current download, having first gone somewhere that can draw it.</summary>
    [RelayCommand]
    public void ShowDownload()
    {
        ShowPanelPage();
        Downloads.ShowList();
    }

    private void OnDownloadOpenRequested(object? sender, DownloadRowViewModel row) => ShowPanelPage();

    private void ShowPanelPage()
    {
        if (CurrentPage is CharactersPageViewModel or CharacterDetailPageViewModel or ModsPageViewModel)
        {
            return;
        }

        ShowMods();
    }

    /// <summary>Sends the user to Settings.</summary>
    [RelayCommand]
    public void ShowSettings() => SelectedNavigationItem = _settingsItem;

    /// <summary>F5: presses the current page's Refresh button, when it has one that can be pressed.</summary>
    /// <returns>Whether a refresh was started.</returns>
    public bool RefreshCurrentPage()
    {
        if (IsFirstRun || CurrentPage is not IRefreshablePage page || !page.RefreshCommand.CanExecute(null))
        {
            return false;
        }

        page.RefreshCommand.Execute(null);
        return true;
    }

    /// <summary>Ctrl+F: shows the current page's search box, for the window to focus.</summary>
    /// <returns>Whether the page has a search box.</returns>
    public bool OpenSearchOnCurrentPage()
    {
        if (IsFirstRun || CurrentPage is not ISearchablePage page)
        {
            return false;
        }

        page.OpenSearch();
        return true;
    }

    /// <summary>Opens the character detail view for the tile that was pressed.</summary>
    /// <remarks>The page is put on screen first, so its work runs on a live activation.</remarks>
    public void OpenCharacterDetail(CharacterTileViewModel tile)
    {
        ArgumentNullException.ThrowIfNull(tile);

        CurrentPage = Detail;
        Detail.Show(tile);
    }

    /// <summary>Goes to a mod: its character's page, or Unsorted or Others, with the mod selected.</summary>
    /// <returns>False when the last scan has no such mod; the screen is left as it was.</returns>
    public bool GoToMod(string modFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modFolder);

        var wasShowing = CurrentPage;
        var wasSelected = SelectedNavigationItem;

        // Characters first, then the page on screen, then the mod: the page's work runs on its activation.
        SelectedNavigationItem = _charactersItem;
        CurrentPage = Detail;

        if (Detail.ShowMod(modFolder))
        {
            return true;
        }

        SelectedNavigationItem = wasSelected;
        CurrentPage = wasShowing;

        return false;
    }

    /// <summary>Returns from the character detail view to whichever rail page is selected.</summary>
    public void CloseCharacterDetail()
    {
        _isDetailRemembered = false;
        CurrentPage = SelectedNavigationItem?.Page;
    }

    /// <summary>A press on the selected rail entry: back to its first screen.</summary>
    /// <param name="item">The entry pressed. Anything but the selected entry is left to the rail.</param>
    public void ReturnToTop(NavigationItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!ReferenceEquals(item, SelectedNavigationItem))
        {
            return;
        }

        if (ReferenceEquals(CurrentPage, Detail))
        {
            CloseCharacterDetail();
            return;
        }

        item.Page.ReturnToTop();
    }

    /// <summary>Goes to Pack Studio with a game's draft open; does nothing while Studio is switched off.</summary>
    public Task EditInStudioAsync(string gameId)
    {
        if (!_studioVisibility.IsShown)
        {
            return Task.CompletedTask;
        }

        SelectedNavigationItem = _studioItem;
        return _studio.OpenInstalledAsync(gameId);
    }

    /// <summary>Whether Pack Studio has a change that is not on disk yet.</summary>
    public bool HasUnsavedWork => _studio.HasUnsavedChanges;

    /// <summary>Says what a reset at this start did, on the first screen and in the notices.</summary>
    /// <param name="result">What the reset moved and could not move, or null when it could not run.</param>
    /// <param name="problem">Why it could not run, or null when it ran.</param>
    public void ReportReset(FactoryResetResult? result, string? problem)
    {
        var failures = result?.Failed ?? [];

        var note = result is null
            ? null
            : _text.Format(nameof(Strings.Reset_Done), _text.Items(result.Moved.Count));

        var trouble = problem is not null
            ? _text.Format(nameof(Strings.Reset_Problem), problem)
            : failures.Count > 0
                ? _text.Format(
                    nameof(Strings.Reset_Partial),
                    _text.Items(failures.Count),
                    string.Join(Environment.NewLine, failures.Select(failure => failure.Message)))
                : null;

        FirstRun.ResetNote = note;
        FirstRun.ResetProblem = trouble;

        _notifications.Add(
            trouble is null ? NotificationSeverity.Information : NotificationSeverity.Warning,
            _text[nameof(Strings.Settings_Reset_Heading)],
            string.Join(Environment.NewLine, new[] { note, trouble }.OfType<string>()));
    }

    /// <summary>Writes anything Pack Studio has not saved yet, before the window closes.</summary>
    /// <returns>A task that completes when Studio's work is on disk, or its write has failed.</returns>
    public Task PrepareToCloseAsync(CancellationToken cancellationToken) => _studio.FlushAsync(cancellationToken);

    /// <inheritdoc />
    protected override void OnActivated()
    {
        _notifications.PropertyChanged += OnNotificationsChanged;
        _packUpdates.PropertyChanged += OnPackUpdatesChanged;
        Downloads.OpenRequested += OnDownloadOpenRequested;
        NoticePopups.Activate();
        CurrentPage?.Activate();
    }

    /// <inheritdoc />
    protected override void OnDeactivated()
    {
        _notifications.PropertyChanged -= OnNotificationsChanged;
        _packUpdates.PropertyChanged -= OnPackUpdatesChanged;
        Downloads.OpenRequested -= OnDownloadOpenRequested;
        NoticePopups.Deactivate();

        foreach (var item in _allItems)
        {
            item.Page.Deactivate();
        }

        FirstRun.Deactivate();
    }

    /// <summary>Closes the shared sort review and install card when the game or its Mods folder changes.</summary>
    private void OnGameChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(GameContext.GameId) or nameof(GameContext.ModsDirectory)))
        {
            return;
        }

        if (_sortReview.IsOpen)
        {
            _sortReview.Close();
        }

        if (_install.IsOpen)
        {
            _install.Close();
        }
    }

    private void OnStudioVisibilityChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(StudioVisibility.IsShown))
        {
            return;
        }

        if (_studioVisibility.IsShown)
        {
            if (!NavigationItems.Contains(_studioItem))
            {
                NavigationItems.Insert(NavigationItems.IndexOf(_packsItem) + 1, _studioItem);
            }

            return;
        }

        if (ReferenceEquals(SelectedNavigationItem, _studioItem) || ReferenceEquals(CurrentPage, _studio))
        {
            SelectedNavigationItem = NavigationItems[0];
        }

        NavigationItems.Remove(_studioItem);
    }

    /// <summary>Shows the entry chosen where it was left; Characters returns to the character left, if any.</summary>
    partial void OnSelectedNavigationItemChanged(
        NavigationItemViewModel? oldValue, NavigationItemViewModel? newValue)
    {
        // Over the page, the menu is for choosing one: chosen, it gets out of the way.
        CloseMenuOver();

        if (ReferenceEquals(oldValue, _charactersItem))
        {
            _isDetailRemembered = ReferenceEquals(CurrentPage, Detail);
        }

        if (ReferenceEquals(newValue, _charactersItem) && _isDetailRemembered)
        {
            // On screen first: the page's work runs on its activation.
            CurrentPage = Detail;

            if (Detail.Resume())
            {
                return;
            }

            _isDetailRemembered = false;
        }

        CurrentPage = newValue?.Page;
    }

    partial void OnCurrentPageChanged(PageViewModel? oldValue, PageViewModel? newValue)
    {
        // Deactivate first, or a single change is handled twice for as long as the app runs.
        oldValue?.Deactivate();

        // Unsubscribed here too: the shell outlives every page and this handler is the shell's.
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnPageBusyChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnPageBusyChanged;
        }

        if (IsActive)
        {
            newValue?.Activate();
        }

        RaiseWorkingChanged();
    }

    private void OnPageBusyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IsBusy) or nameof(BusyMessage))
        {
            RaiseWorkingChanged();
        }
    }

    /// <inheritdoc />
    /// <remarks>Watches <c>IsBusy</c>, declared on the base class, whose generated hooks are the base's.</remarks>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName is nameof(IsBusy) or nameof(BusyMessage))
        {
            RaiseWorkingChanged();
        }
    }

    private void RaiseWorkingChanged()
    {
        OnPropertyChanged(nameof(IsWorking));
        OnPropertyChanged(nameof(WorkingMessage));
    }

    partial void OnIsFirstRunChanged(bool value)
    {
        if (value)
        {
            CurrentPage?.Deactivate();
            FirstRun.Activate();
        }
        else
        {
            FirstRun.Deactivate();

            if (IsActive)
            {
                CurrentPage?.Activate();
            }
        }
    }

    partial void OnSelectedGameChanged(GameOptionViewModel? oldValue, GameOptionViewModel? newValue)
    {
        if (!string.Equals(oldValue?.GameId, newValue?.GameId, StringComparison.OrdinalIgnoreCase)
            && !ReferenceEquals(CurrentPage, Detail))
        {
            _isDetailRemembered = false;
        }

        if (_isSwitchingGame)
        {
            return;
        }

        Track(_runner.RunAsync(
            _text[nameof(Strings.Shell_Game_Label)],
            async ct =>
            {
                await ReloadGameAsync(ct).ConfigureAwait(true);

                if (newValue is not null)
                {
                    await _settings
                        .UpdateAsync(current => current with { LastGameId = newValue.GameId }, ct)
                        .ConfigureAwait(true);
                }
            },
            ActivationToken));
    }

    /// <summary>A window at least this wide has the menu beside the page; narrower opens it over the page.</summary>
    public const double MenuBesideWidth = 960;

    private bool _menuHiddenWhenWide;

    /// <summary>Whether the menu is showing, beside the page or over it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMenuBeside), nameof(IsMenuOver), nameof(MenuToggleText))]
    private bool _isMenuOpen = true;

    /// <summary>Whether the window is narrower than <see cref="MenuBesideWidth"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMenuBeside), nameof(IsMenuOver))]
    private bool _isNarrow;

    /// <summary>The menu is open and beside the page, which starts where it ends.</summary>
    public bool IsMenuBeside => IsMenuOpen && !IsNarrow;

    /// <summary>The menu is open over the page, which is dimmed behind it.</summary>
    public bool IsMenuOver => IsMenuOpen && IsNarrow;

    /// <summary>What the ☰ button does, for its tooltip.</summary>
    public string MenuToggleText => _text[IsMenuOpen ? nameof(Strings.Shell_Menu_Hide) : nameof(Strings.Shell_Menu_Show)];

    /// <summary>The window says how wide it is; the menu's place follows.</summary>
    public void SetWindowWidth(double width) => IsNarrow = width < MenuBesideWidth;

    /// <summary>Narrowing the window puts the menu away; widening brings it back unless hidden on purpose.</summary>
    partial void OnIsNarrowChanged(bool value) => IsMenuOpen = !value && !_menuHiddenWhenWide;

    /// <summary>The ☰ button; hiding or showing the menu is remembered only in a wide window.</summary>
    [RelayCommand]
    public void ToggleMenu()
    {
        _menuOpenedForGame = false;
        IsMenuOpen = !IsMenuOpen;

        if (IsNarrow)
        {
            return;
        }

        _menuHiddenWhenWide = !IsMenuOpen;
        var hidden = _menuHiddenWhenWide;

        Track(_runner.RunAsync(
            _text[nameof(Strings.Shell_Menu_Hide)],
            async ct => await _settings.UpdateAsync(current => current with { MenuHidden = hidden }, ct).ConfigureAwait(true),
            ActivationToken));
    }

    private bool _menuOpenedForGame;

    /// <summary>Whether the game selector's list is dropped down.</summary>
    [ObservableProperty]
    private bool _isGameListOpen;

    /// <summary>The game's icon in the strip: opens the menu with the game list, closing again after.</summary>
    [RelayCommand]
    public void ChooseGame()
    {
        if (!IsMenuOpen)
        {
            _menuOpenedForGame = true;
            IsMenuOpen = true;
        }

        IsGameListOpen = true;
    }

    partial void OnIsGameListOpenChanged(bool value)
    {
        if (!value && _menuOpenedForGame)
        {
            _menuOpenedForGame = false;
            IsMenuOpen = false;
        }
    }

    partial void OnIsMenuOpenChanged(bool value)
    {
        if (!value)
        {
            _menuOpenedForGame = false;
        }
    }

    /// <summary>Closes the menu when it is over the page: a click on the dimmed page, Esc, or a page chosen.</summary>
    [RelayCommand]
    public void CloseMenuOver()
    {
        if (IsMenuOver)
        {
            IsMenuOpen = false;
        }
    }

    private void OnPackUpdatesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PackUpdateWatcher.WaitingCount))
        {
            _packsItem.BadgeCount = _packUpdates.WaitingCount;
        }
    }

    private void OnNotificationsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(INotificationService.UnreadCount))
        {
            _notificationsItem.BadgeCount = _notifications.UnreadCount;
        }
    }

    /// <summary>Fills the game selector from what is installed, and selects the game the user was last on.</summary>
    private async Task LoadGamesAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        // An empty registry list means consult none: null would reach the network.
        var catalog = await _packs.GetCatalogAsync([], cancellationToken).ConfigureAwait(true);

        _isSwitchingGame = true;

        Games.Clear();

        foreach (var entry in catalog.Entries.Where(entry => entry.IsInstalled))
        {
            Games.Add(new GameOptionViewModel(entry.GameId, entry.DisplayName, entry.ActiveVersion)
            {
                Icon = _icons.Installed(entry.GameId, entry.DisplayName),
            });
        }

        var chosen = Games.FirstOrDefault(option =>
                         string.Equals(option.GameId, settings.LastGameId, StringComparison.OrdinalIgnoreCase))
                     ?? Games.FirstOrDefault();

        SelectedGame = chosen;
        _isSwitchingGame = false;

        OnPropertyChanged(nameof(HasGames));

        await ReloadGameAsync(cancellationToken).ConfigureAwait(true);
    }
}

/// <summary>The shell's pages, in rail order.</summary>
public sealed record ShellPages(
    CharactersPageViewModel Characters,
    ModsPageViewModel Mods,
    PacksPageViewModel Packs,
    StudioPageViewModel Studio,
    ProfilesPageViewModel Profiles,
    NotificationsPageViewModel Notifications,
    SettingsPageViewModel Settings);
