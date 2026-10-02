using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Core;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Core.Settings;
using Xxsm.Core.Text;
using Xxsm.Desktop.Services;
using Xxsm.Packs.Downloads;
using Xxsm.Packs.Registry;
using Xxsm.Packs.Sorting;

namespace Xxsm.Desktop.ViewModels.Pages;

/// <summary>The settings page: every change is written as it is made, and all of it is in <c>xxsm config</c>.</summary>
public sealed partial class SettingsPageViewModel : PageViewModel
{
    private readonly IGameIconProvider _icons;
    private readonly GameContext _game;
    private readonly IAppSettingsStore _settings;
    private readonly IPackPreferencesStore _packPreferences;
    private readonly IPackService _packs;
    private readonly IModsFolderProbe _probe;
    private readonly IStoragePicker _folders;
    private readonly IThemeApplier _themeApplier;
    private readonly INotificationService _notifications;
    private readonly ViewModelWorkRunner _runner;
    private readonly IAppPaths _paths;
    private readonly Func<CancellationToken, Task> _rescan;
    private readonly IDownloadManager _downloads;
    private readonly UpdateCheckRun _updates;
    private readonly Func<CancellationToken, Task> _reloadGame;
    private readonly StudioVisibility _studio;
    private readonly IFactoryReset _reset;
    private readonly IApplicationRestarter _restarter;

    /// <summary>Serialises this page's writes, so two quick changes cannot both read the old file.</summary>
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    private readonly ITextOverrideStore _textOverrides;
    private readonly IFolderLauncher _launcher;

    private bool _isLoading;

    /// <summary>Creates the page.</summary>
    public SettingsPageViewModel(
        GameContext game,
        IAppSettingsStore settings,
        IPackPreferencesStore packPreferences,
        IPackService packs,
        IModsFolderProbe probe,
        IStoragePicker folders,
        IThemeApplier theme,
        INotificationService notifications,
        ViewModelWorkRunner runner,
        IAppPaths paths,
        ITextCatalogue text,
        ITextOverrideStore textOverrides,
        IFolderLauncher launcher,
        IGameIconProvider icons,
        StudioVisibility studio,
        UpdateCheckRun updates,
        IDownloadManager downloads,
        IFactoryReset reset,
        IApplicationRestarter restarter,
        Func<CancellationToken, Task> reloadGame,
        Func<CancellationToken, Task> rescan)
        : base(text)
    {
        ArgumentNullException.ThrowIfNull(rescan);
        _rescan = rescan;
        ArgumentNullException.ThrowIfNull(reset);
        _reset = reset;
        ArgumentNullException.ThrowIfNull(restarter);
        _restarter = restarter;
        ArgumentNullException.ThrowIfNull(updates);
        _updates = updates;
        ArgumentNullException.ThrowIfNull(downloads);
        _downloads = downloads;
        ArgumentNullException.ThrowIfNull(icons);
        _icons = icons;
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(packPreferences);
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(textOverrides);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(studio);
        ArgumentNullException.ThrowIfNull(reloadGame);

        _game = game;
        _studio = studio;
        _settings = settings;
        _packPreferences = packPreferences;
        _packs = packs;
        _probe = probe;
        _folders = folders;
        _themeApplier = theme;
        _notifications = notifications;
        _runner = runner;
        _paths = paths;
        _textOverrides = textOverrides;
        _launcher = launcher;
        _reloadGame = reloadGame;

        SettingsPath = settings.SettingsPath;
        PacksPath = packPreferences.PreferencesPath;
        PacksDirectory = paths.PacksDirectory;
        OverlaysDirectory = paths.OverlaysDirectory;
        LogsDirectory = paths.LogsDirectory;
        CacheDirectory = paths.CacheDirectory;
    }

    /// <inheritdoc />
    public override string Heading => Text[nameof(Strings.Settings_Heading)];

    // Appearance

    /// <summary>The chosen colour scheme.</summary>
    [ObservableProperty]
    private AppTheme _theme = AppTheme.System;

    // Pack Studio

    /// <summary>Whether Pack Studio is in the rail. Off by default: it is for making or publishing a pack.</summary>
    [ObservableProperty]
    private bool _showPackStudio;

    /// <summary>Whether XXSM asks at start and daily if a newer version is out. On by default.</summary>
    [ObservableProperty]
    private bool _checkForAppUpdates;

    // This game

    /// <summary>Whether a game is selected, so the per-game section has a subject.</summary>
    public bool HasGame => _game.HasGame;

    /// <summary>The selected game's name.</summary>
    public string? GameDisplayName => _game.DisplayName;

    /// <summary>The selected game's icon, before its name.</summary>
    public GameIconViewModel? GameIcon => _game.GameId is { Length: > 0 } id
        ? _icons.Installed(id, _game.DisplayName ?? id)
        : null;

    /// <summary>The saved Mods folder, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModsDirectory))]
    private string? _modsDirectory;

    /// <summary>Whether a Mods folder has been chosen for this game.</summary>
    public bool HasModsDirectory => ModsDirectory is { Length: > 0 };

    /// <summary>What the last probe of that folder said, for display under the path.</summary>
    [ObservableProperty]
    private string? _modsDirectoryNote;

    /// <summary>How this game's families are shown.</summary>
    [ObservableProperty]
    private SkinDisplayMode _skinDisplayMode = SkinDisplayMode.Grouped;

    // Registries

    /// <summary>The registries the user has configured, in order.</summary>
    public ObservableCollection<RegistrySourceViewModel> Registries { get; } = [];

    /// <summary>Whether the user has configured any, or is on the built-in default.</summary>
    public bool UsesDefaultRegistry => Registries.Count == 0;

    /// <summary>The built-in default, shown when the user has configured none.</summary>
    public static string DefaultRegistry => AppInfo.DefaultRegistryUrl;

    /// <summary>The registry being typed into the add box.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddOrBrowseLabel))]
    private string _newRegistry = string.Empty;

    /// <summary>The button beside the box: <em>Browse…</em> while empty, <em>Add</em> once there is text.</summary>
    public string AddOrBrowseLabel =>
        NewRegistry.Trim().Length == 0
            ? Text[nameof(Strings.Settings_Registries_Browse)]
            : Text[nameof(Strings.Settings_Registries_Add)];

    // Auto-sort

    /// <summary>How many characters a hash may match before it is ignored.</summary>
    [ObservableProperty]
    private decimal? _ambiguityThreshold;

    /// <summary>How much evidence is needed before a mod is filed at all.</summary>
    [ObservableProperty]
    private decimal? _minScore;

    /// <summary>How far the best match must beat the runner-up.</summary>
    [ObservableProperty]
    private decimal? _marginRatio;

    /// <summary>The most confident XXSM will claim to be when it guessed the outfit.</summary>
    [ObservableProperty]
    private decimal? _confidenceCeiling;

    /// <summary>Whether any of the four differs from its measured default.</summary>
    [ObservableProperty]
    private bool _sortIsOverridden;

    // GameBanana

    /// <summary>Whether XXSM may contact GameBanana at all; off until switched on, hiding the other switches.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GameBananaCanCheckNow))]
    private bool _gameBananaEnabled;

    /// <summary>Whether a mod installed from an address is filled in from its page.</summary>
    [ObservableProperty]
    private bool _gameBananaFetchOnInstall = true;

    /// <summary>Whether <em>Add mod</em> offers <em>From GameBanana…</em>.</summary>
    [ObservableProperty]
    private bool _gameBananaAddFromUrl = true;

    /// <summary>Whether a paste on a character page starts a look-up.</summary>
    [ObservableProperty]
    private bool _gameBananaPasteStartsLookup = true;

    /// <summary>What a look-up on a mod that is already installed does.</summary>
    [ObservableProperty]
    private GameBananaPasteAction _gameBananaPasteIntoExisting = GameBananaPasteAction.Ask;

    /// <summary>Whether a mod's preview picture is fetched along with its words.</summary>
    [ObservableProperty]
    private bool _gameBananaDownloadPictures = true;

    /// <summary>How many days a finished download stays on the list.</summary>
    [ObservableProperty]
    private decimal? _gameBananaKeepDownloadsDays = 7;

    /// <summary>Whether the background update check runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GameBananaCanCheckNow))]
    private bool _gameBananaCheckForUpdates;

    /// <summary>How many hours between update checks.</summary>
    [ObservableProperty]
    private decimal? _gameBananaCheckIntervalHours = 24;

    /// <summary>How many hours a fetched page stays usable from disk.</summary>
    [ObservableProperty]
    private decimal? _gameBananaCacheHours = 6;

    /// <summary>How many seconds XXSM waits between two requests.</summary>
    [ObservableProperty]
    private decimal? _gameBananaSecondsBetweenRequests = 1;

    /// <summary>The API address, so a future API version is a settings change.</summary>
    [ObservableProperty]
    private string _gameBananaApiBaseUrl = GameBananaSettings.DefaultApiBaseUrl;

    /// <summary>What the last <em>Check now</em> found, or null when it has not been pressed.</summary>
    [ObservableProperty]
    private string? _gameBananaCheckNote;

    /// <summary>Whether <em>Check now</em> can run: GameBanana is on and this game has a Mods folder.</summary>
    public bool GameBananaCanCheckNow => GameBananaEnabled && _game.ModsDirectory is { Length: > 0 } && !_updates.IsRunning;

    // Where things are

    /// <summary>Where the settings file is.</summary>
    public string SettingsPath { get; }

    /// <summary>The folder the settings file is in, which its row's <em>Open</em> shows.</summary>
    public string SettingsFolder => _paths.ConfigDirectory;

    /// <summary>Where the registries and pins are.</summary>
    public string PacksPath { get; }

    /// <summary>Where installed packs are.</summary>
    public string PacksDirectory { get; }

    /// <summary>Where the user's own corrections are. The irreplaceable ones.</summary>
    public string OverlaysDirectory { get; }

    /// <summary>Where the logs are.</summary>
    public string LogsDirectory { get; }

    /// <summary>Where downloads and thumbnails are cached.</summary>
    public string CacheDirectory { get; }

    // Reset

    /// <summary>Whether the reset card is asking to be sure, and showing what would go.</summary>
    [ObservableProperty]
    private bool _isConfirmingReset;

    /// <summary>How much would go to the Trash, in words: "15 items will go to the Trash."</summary>
    [ObservableProperty]
    private string? _resetSummary;

    /// <summary>The folders a reset empties, so it is plain where the files come from.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _resetDirectories = [];

    // Wording

    /// <summary>Whether this build reads <c>text.json</c>; the wording card shows only when it does.</summary>
    public bool CanEditText => _textOverrides.IsEnabled;

    /// <summary>The line of a reset's list that covers settings, naming the wording only when it can be changed.</summary>
    public string ResetSettingsLine => CanEditText
        ? Text[nameof(Strings.Settings_Reset_Goes_Settings)]
        : Text[nameof(Strings.Settings_Reset_Goes_SettingsOnly)];

    /// <summary>Where the editable wording file is, whether or not there is one.</summary>
    public string TextPath => Text.OverridesPath;

    /// <summary>Whether there is a file there yet.</summary>
    public bool HasTextFile => Text.OverridesExist;

    /// <summary>How many entries the file replaces, in words. Empty when it replaces none.</summary>
    public string TextSummary => Text.OverridesExist
        ? Text.Format(
            nameof(Strings.Settings_Text_Replaced), Text.ReplacedKeys.Count, Text.BuiltIn.Count)
        : Text[nameof(Strings.Settings_Text_None)];

    /// <summary>Names in the file that match nothing, or empty when they all match.</summary>
    public string TextUnknown => Text.UnknownKeys.Count is 0
        ? string.Empty
        : Text.Format(nameof(Strings.Settings_Text_Unknown), string.Join(", ", Text.UnknownKeys));

    /// <summary>Why the file could not be fully used, or null when it could.</summary>
    public string? TextProblem => Text.Problem;

    /// <summary>What happened the last time the Create button was pressed.</summary>
    [ObservableProperty]
    private string? _textNote;

    /// <summary>Shown briefly after a change is written.</summary>
    [ObservableProperty]
    private bool _showSaved;

    /// <summary>Asks for a Mods folder, checks it, and saves it if it will work.</summary>
    [RelayCommand]
    public async Task BrowseForModsFolderAsync()
    {
        if (_game.GameId is not { Length: > 0 } gameId)
        {
            return;
        }

        var token = ActivationToken;

        var chosen = await _folders
            .PickFolderAsync(Text[nameof(Strings.Settings_ModsFolder_Label)], ModsDirectory, token)
            .ConfigureAwait(true);

        if (chosen is not { Length: > 0 })
        {
            return;
        }

        await SaveSerializedAsync(
            Text[nameof(Strings.Settings_ModsFolder_Label)],
            async ct =>
            {
                var probe = await _probe.ProbeAsync(chosen, ct).ConfigureAwait(true);

                ModsDirectoryNote = probe.Summary;

                if (!probe.IsUsable)
                {
                    // Refused rather than saved: every operation is a move inside this folder.
                    _notifications.Add(
                        NotificationSeverity.Warning,
                        Text[nameof(Strings.Settings_ModsFolder_Label)],
                        probe.Summary);

                    return;
                }

                await _settings.UpdateGameAsync(
                    gameId,
                    current => current with { ModsDirectory = probe.Path },
                    ct).ConfigureAwait(true);

                _isLoading = true;
                ModsDirectory = probe.Path;
                _isLoading = false;

                _game.ModsDirectory = probe.Path;
                Saved();

                await _reloadGame(ct).ConfigureAwait(true);
            }).ConfigureAwait(true);
    }

    /// <summary>Adds what is in the box, or opens the folder picker when the box is empty.</summary>
    /// <returns>A task that completes when a source has been added, or the user cancelled.</returns>
    [RelayCommand]
    public async Task AddOrBrowseRegistryAsync()
    {
        var typed = NewRegistry.Trim();

        if (typed.Length > 0)
        {
            await AddRegistryAsync(typed).ConfigureAwait(true);
            return;
        }

        var chosen = await _folders
            .PickFolderAsync(Text[nameof(Strings.Settings_Registries_Heading)], null, ActivationToken)
            .ConfigureAwait(true);

        if (chosen is { Length: > 0 })
        {
            await AddRegistryAsync(chosen).ConfigureAwait(true);
        }
    }

    /// <summary>Asks whether to remove a source, rather than removing it.</summary>
    /// <param name="registry">The row to ask about.</param>
    [RelayCommand]
    public void AskRemoveRegistry(RegistrySourceViewModel? registry)
    {
        if (registry is null)
        {
            return;
        }

        // Only one row asks at a time.
        foreach (var other in Registries)
        {
            other.IsConfirmingRemoval = ReferenceEquals(other, registry);
        }
    }

    /// <summary>Puts a row back after the user declined to remove it.</summary>
    [RelayCommand]
    public void CancelRemoveRegistry(RegistrySourceViewModel? registry)
    {
        foreach (var row in Registries)
        {
            if (registry is null || ReferenceEquals(row, registry))
            {
                row.IsConfirmingRemoval = false;
            }
        }
    }

    /// <summary>Removes one registry, once it has been confirmed.</summary>
    [RelayCommand]
    public Task RemoveRegistryAsync(RegistrySourceViewModel? registry)
    {
        if (registry is null || !Registries.Remove(registry))
        {
            return Task.CompletedTask;
        }

        return SaveRegistriesAsync();
    }

    /// <summary>Puts every auto-sort constant back to its measured default.</summary>
    [RelayCommand]
    public Task ResetSortAsync()
    {
        var defaults = SortSettings.Default;

        _isLoading = true;
        AmbiguityThreshold = defaults.AmbiguityThreshold;
        MinScore = defaults.MinScore;
        MarginRatio = (decimal)defaults.MarginRatio;
        ConfidenceCeiling = (decimal)defaults.DefaultVariantConfidenceCeiling;
        _isLoading = false;

        return SaveSerializedAsync(
            Text[nameof(Strings.Settings_Sort_Heading)],
            async ct =>
            {
                await _settings.UpdateAsync(
                    current => current with { Sort = SortThresholdSettings.Default },
                    ct).ConfigureAwait(true);

                SortIsOverridden = false;
                Saved();
            });
    }

    /// <summary>Writes a starter <c>text.json</c>, every entry present and off; never over an existing one.</summary>
    [RelayCommand]
    public Task CreateTextFileAsync() =>
        _runner.RunAsync(
            Text[nameof(Strings.Settings_Text_Heading)],
            async ct =>
            {
                var written = await _textOverrides
                    .CreateOverridesAsync(Text.BuiltIn, overwrite: false, ct)
                    .ConfigureAwait(true);

                TextNote = written
                    ? Text[nameof(Strings.Settings_Text_Created)]
                    : Text[nameof(Strings.Settings_Text_Exists)];

                OnPropertyChanged(nameof(HasTextFile));
            },
            ActivationToken);

    /// <summary>Opens one of the places "Where XXSM keeps things" lists, in the desktop's file manager.</summary>
    /// <param name="folder">The folder, as its row shows it.</param>
    [RelayCommand]
    public Task OpenPlaceAsync(string? folder) =>
        folder is not { Length: > 0 } path
            ? Task.CompletedTask
            : _runner.RunAsync(Text[nameof(Strings.Settings_Files_Heading)], ct => _launcher.OpenAsync(path, ct), ActivationToken);

    /// <summary>Opens the configuration folder in the desktop's file manager.</summary>
    [RelayCommand]
    public Task OpenConfigFolderAsync() =>
        _runner.RunAsync(
            Text[nameof(Strings.Settings_Text_Heading)],
            ct => _launcher.OpenAsync(_paths.ConfigDirectory, ct),
            ActivationToken);

    /// <summary>Works out what a reset would clear, and asks whether to go ahead; nothing happens yet.</summary>
    [RelayCommand]
    public Task AskResetAsync() =>
        _runner.RunAsync(
            Text[nameof(Strings.Settings_Reset_Heading)],
            async ct =>
            {
                var plan = await _reset.PlanAsync(ct).ConfigureAwait(true);

                ResetSummary = Text.Format(nameof(Strings.Settings_Reset_Count), Text.Items(plan.Items.Count));
                ResetDirectories = plan.Directories;
                IsConfirmingReset = true;
            },
            ActivationToken);

    /// <summary>Puts the card back without resetting anything.</summary>
    [RelayCommand]
    public void CancelReset() => IsConfirmingReset = false;

    /// <summary>Schedules the reset and restarts XXSM to carry it out; if scheduling fails, nothing restarts.</summary>
    [RelayCommand]
    public async Task ConfirmResetAsync()
    {
        var scheduled = await _runner.RunAsync(
            Text[nameof(Strings.Settings_Reset_Heading)],
            ct => _reset.ScheduleAsync(ct),
            ActivationToken).ConfigureAwait(true);

        IsConfirmingReset = false;

        if (scheduled)
        {
            _restarter.Restart();
        }
    }

    /// <inheritdoc />
    protected override void OnActivated()
    {
        _game.PropertyChanged += OnGameChanged;
        _updates.PropertyChanged += OnCheckChanged;
        OnPropertyChanged(nameof(GameBananaCanCheckNow));
        Track(ReloadAsync());
    }

    /// <summary>A check started anywhere greys Check now until it ends.</summary>
    private void OnCheckChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        OnPropertyChanged(nameof(GameBananaCanCheckNow));

    /// <inheritdoc />
    protected override void OnDeactivated()
    {
        _game.PropertyChanged -= OnGameChanged;
        _updates.PropertyChanged -= OnCheckChanged;

        IsConfirmingReset = false;
    }

    /// <summary>Reloads every value from disk.</summary>
    internal Task ReloadAsync() =>
        _runner.RunAsync(
            Text[nameof(Strings.Settings_Heading)],
            async ct =>
            {
                var settings = await _settings.ReadAsync(ct).ConfigureAwait(true);
                var packs = await _packPreferences.ReadAsync(ct).ConfigureAwait(true);
                var sort = settings.Sort.ToSortSettings();

                _isLoading = true;

                Theme = settings.Theme;
                ShowPackStudio = settings.ShowPackStudio;
                CheckForAppUpdates = !settings.UpdateCheckOff;
                AutoUpdatePacks = packs.AutoUpdate;

                var gameBanana = settings.GameBanana;
                GameBananaEnabled = gameBanana.Enabled;
                GameBananaFetchOnInstall = gameBanana.FetchOnInstallOrDefault;
                GameBananaAddFromUrl = gameBanana.AddFromUrlOrDefault;
                GameBananaPasteStartsLookup = gameBanana.PasteStartsLookupOrDefault;
                GameBananaPasteIntoExisting = gameBanana.PasteIntoExisting;
                GameBananaDownloadPictures = gameBanana.DownloadPicturesOrDefault;
                GameBananaCheckForUpdates = gameBanana.CheckForUpdates;
                GameBananaCheckIntervalHours = (decimal)gameBanana.CheckInterval.TotalHours;
                GameBananaKeepDownloadsDays = (decimal)gameBanana.KeepDownloads.TotalDays;
                GameBananaCacheHours = (decimal)gameBanana.CacheLifetime.TotalHours;
                GameBananaSecondsBetweenRequests = (decimal)gameBanana.RequestGap.TotalSeconds;
                GameBananaApiBaseUrl = gameBanana.ApiBase.AbsoluteUri;

                SortIsOverridden = !settings.Sort.IsDefault;
                AmbiguityThreshold = sort.AmbiguityThreshold;
                MinScore = sort.MinScore;
                MarginRatio = (decimal)sort.MarginRatio;
                ConfidenceCeiling = (decimal)sort.DefaultVariantConfidenceCeiling;

                if (_game.GameId is { Length: > 0 } gameId)
                {
                    var forGame = settings.ForGame(gameId);
                    ModsDirectory = forGame.ModsDirectory;
                    SkinDisplayMode = forGame.SkinDisplayMode;
                }
                else
                {
                    ModsDirectory = null;
                    SkinDisplayMode = SkinDisplayMode.Grouped;
                }

                Registries.Clear();

                foreach (var registry in packs.Registries)
                {
                    Registries.Add(new RegistrySourceViewModel(registry));
                }

                _isLoading = false;

                OnPropertyChanged(nameof(HasGame));
                OnPropertyChanged(nameof(GameDisplayName));
                OnPropertyChanged(nameof(GameIcon));
                OnPropertyChanged(nameof(UsesDefaultRegistry));
                OnPropertyChanged(nameof(GameBananaCanCheckNow));
            },
            ActivationToken);

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            _saveLock.Dispose();
        }
    }

    partial void OnThemeChanged(AppTheme value)
    {
        // Applied before it is saved, so the change shows at once.
        _themeApplier.Apply(value);

        if (_isLoading)
        {
            return;
        }

        Save(
            Text[nameof(Strings.Settings_Theme_Label)],
            async ct =>
            {
                await _settings.UpdateAsync(current => current with { Theme = value }, ct)
                    .ConfigureAwait(true);

                Saved();
            });
    }

    /// <summary>Whether newer Game Packs install by themselves: one switch, kept in <c>packs.json</c>.</summary>
    [ObservableProperty]
    private bool _autoUpdatePacks;

    partial void OnAutoUpdatePacksChanged(bool value)
    {
        if (_isLoading)
        {
            return;
        }

        Save(
            Text[nameof(Strings.Settings_PackUpdates_Auto)],
            async ct =>
            {
                await _packs.SetAutoUpdateAsync(value, ct).ConfigureAwait(true);
                Saved();
            });
    }

    partial void OnShowPackStudioChanged(bool value)
    {
        if (_isLoading)
        {
            return;
        }

        _studio.IsShown = value;

        Save(
            Text[nameof(Strings.Settings_Studio_Show)],
            async ct =>
            {
                await _settings.UpdateAsync(current => current with { ShowPackStudio = value }, ct)
                    .ConfigureAwait(true);

                Saved();
            });
    }

    partial void OnCheckForAppUpdatesChanged(bool value)
    {
        if (_isLoading)
        {
            return;
        }

        Save(
            Text[nameof(Strings.Settings_AppUpdates_Check)],
            async ct =>
            {
                await _settings.UpdateAsync(current => current with { UpdateCheckOff = !value }, ct)
                    .ConfigureAwait(true);

                Saved();
            });
    }

    partial void OnSkinDisplayModeChanged(SkinDisplayMode value)
    {
        if (_isLoading || _game.GameId is not { Length: > 0 } gameId)
        {
            return;
        }

        Save(
            Text[nameof(Strings.Settings_SkinDisplay_Label)],
            async ct =>
            {
                await _settings.UpdateGameAsync(
                    gameId, current => current with { SkinDisplayMode = value }, ct)
                    .ConfigureAwait(true);

                // Presentation only: the grid regroups, nothing on disk moves.
                _game.SkinDisplayMode = value;
                Saved();
            });
    }

    partial void OnGameBananaEnabledChanged(bool value)
    {
        SaveGameBanana(Text[nameof(Strings.Settings_GameBanana_Enabled)], current => current with { Enabled = value });

        // Off stops what is running; the list, archives and links stay for switching back on.
        if (!value && !_isLoading && _downloads.CancelAll() is > 0 and var stopped)
        {
            _notifications.Add(
                NotificationSeverity.Information,
                Text[nameof(Strings.Settings_GameBanana_Enabled)],
                Text.Format(nameof(Strings.Settings_GameBanana_Off_Stopped), Text.Downloads(stopped)));
        }
    }

    partial void OnGameBananaFetchOnInstallChanged(bool value) => SaveGameBanana(
        Text[nameof(Strings.Settings_GameBanana_FetchOnInstall)],
        current => current with { FetchOnInstall = value });

    partial void OnGameBananaAddFromUrlChanged(bool value) => SaveGameBanana(
        Text[nameof(Strings.Settings_GameBanana_AddFromUrl)],
        current => current with { AddFromUrl = value });

    partial void OnGameBananaPasteStartsLookupChanged(bool value) => SaveGameBanana(
        Text[nameof(Strings.Settings_GameBanana_Paste)],
        current => current with { PasteStartsLookup = value });

    partial void OnGameBananaPasteIntoExistingChanged(GameBananaPasteAction value) => SaveGameBanana(
        Text[nameof(Strings.Settings_GameBanana_PasteInto)],
        current => current with { PasteIntoExisting = value });

    partial void OnGameBananaDownloadPicturesChanged(bool value) => SaveGameBanana(
        Text[nameof(Strings.Settings_GameBanana_Pictures)],
        current => current with { DownloadPictures = value });

    partial void OnGameBananaCheckForUpdatesChanged(bool value) => SaveGameBanana(
        Text[nameof(Strings.Settings_GameBanana_CheckUpdates)],
        current => current with { CheckForUpdates = value });

    partial void OnGameBananaCheckIntervalHoursChanged(decimal? value) => SaveGameBanana(
        Text[nameof(Strings.Settings_GameBanana_CheckInterval)],
        current => current with { CheckIntervalHours = value is { } hours ? (double)hours : null });

    partial void OnGameBananaKeepDownloadsDaysChanged(decimal? value) => SaveGameBanana(
        Text[nameof(Strings.Settings_GameBanana_KeepDownloads)],
        current => current with { KeepDownloadsDays = value is { } days ? (double)days : null });

    partial void OnGameBananaCacheHoursChanged(decimal? value) => SaveGameBanana(
        Text[nameof(Strings.Settings_GameBanana_CacheHours)],
        current => current with { CacheHours = value is { } hours ? (double)hours : null });

    partial void OnGameBananaSecondsBetweenRequestsChanged(decimal? value) => SaveGameBanana(
        Text[nameof(Strings.Settings_GameBanana_Rate)],
        current => current with { SecondsBetweenRequests = value is { } seconds ? (double)seconds : null });

    partial void OnGameBananaApiBaseUrlChanged(string value) => SaveGameBanana(
        Text[nameof(Strings.Settings_GameBanana_Api)],
        current => current with
        {
            ApiBaseUrl = value.Trim() is { Length: > 0 } address ? address : null,
        });

    /// <summary>Asks GameBanana about every linked mod, forced, and says what it found; downloads nothing.</summary>
    [RelayCommand]
    public async Task CheckForUpdatesNowAsync()
    {
        if (_game.ModsDirectory is not { Length: > 0 } mods)
        {
            return;
        }

        GameBananaCheckNote = Text[nameof(Strings.Settings_GameBanana_Checking)];

        var report = await _updates.RunAsync(mods, force: true).ConfigureAwait(true);

        if (report is null)
        {
            // The runner has already said what went wrong.
            GameBananaCheckNote = null;

            return;
        }

        GameBananaCheckNote = report.Linked == 0
            ? Text[nameof(Strings.Settings_GameBanana_NoneLinked)]
            : report.HasUpdates
                ? Text.ForCount(report.Updates.Count, nameof(Strings.Settings_GameBanana_FoundUpdates_One), nameof(Strings.Settings_GameBanana_FoundUpdates), Text.Mods(report.Updates.Count))
                : Text[nameof(Strings.Settings_GameBanana_AllCurrent)];
    }

    private void SaveGameBanana(string what, Func<GameBananaSettings, GameBananaSettings> change)
    {
        if (_isLoading)
        {
            return;
        }

        Save(what, async ct =>
        {
            await _settings
                .UpdateAsync(current => current with { GameBanana = change(current.GameBanana) }, ct)
                .ConfigureAwait(true);

            Saved();
        });
    }

    partial void OnAmbiguityThresholdChanged(decimal? value) => SaveThresholds();

    partial void OnMinScoreChanged(decimal? value) => SaveThresholds();

    partial void OnMarginRatioChanged(decimal? value) => SaveThresholds();

    partial void OnConfidenceCeilingChanged(decimal? value) => SaveThresholds();

    private void OnGameChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GameContext.GameId) or nameof(GameContext.DisplayName))
        {
            Track(ReloadAsync());
        }
    }

    private void SaveThresholds()
    {
        if (_isLoading)
        {
            return;
        }

        var thresholds = new SortThresholdSettings
        {
            AmbiguityThreshold = (int?)AmbiguityThreshold,
            MinScore = (int?)MinScore,
            MarginRatio = MarginRatio is { } margin ? (double)margin : null,
            DefaultVariantConfidenceCeiling =
                ConfidenceCeiling is { } ceiling ? (double)ceiling : null,
        };

        // Reduced against the defaults, so a default typed back in is stored as unset.
        var reduced = thresholds.ToSortSettings().ToThresholdSettings();

        Save(
            Text[nameof(Strings.Settings_Sort_Heading)],
            async ct =>
            {
                await _settings.UpdateAsync(current => current with { Sort = reduced }, ct)
                    .ConfigureAwait(true);

                SortIsOverridden = !reduced.IsDefault;
                Saved();
            });
    }

    /// <summary>Queues a write behind every write already queued.</summary>
    private void Save(string title, Func<CancellationToken, Task> work) =>
        Track(SaveSerializedAsync(title, work));

    private async Task SaveSerializedAsync(string title, Func<CancellationToken, Task> work)
    {
        // Not the activation token: a save the user made finishes even if they leave.
        await _saveLock.WaitAsync(CancellationToken.None).ConfigureAwait(true);

        try
        {
            await _runner.RunAsync(title, work, ActivationToken).ConfigureAwait(true);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>Adds a source whether or not it answers, saves it, and says which it was.</summary>
    private async Task AddRegistryAsync(string location)
    {
        var registry = location.Trim();

        if (registry.Length == 0
            || Registries.Any(existing =>
                string.Equals(existing.Location, registry, StringComparison.OrdinalIgnoreCase)))
        {
            NewRegistry = string.Empty;
            return;
        }

        Registries.Add(new RegistrySourceViewModel(registry));
        NewRegistry = string.Empty;

        await SaveRegistriesAsync().ConfigureAwait(true);

        await _runner.RunAsync(
            Text[nameof(Strings.Settings_Registries_Heading)],
            async ct =>
            {
                var catalog = await _packs.GetCatalogAsync([registry], ct).ConfigureAwait(true);

                var failure = catalog.Failures.Count > 0 ? catalog.Failures[0] : null;

                _notifications.Add(
                    failure is null ? NotificationSeverity.Information : NotificationSeverity.Warning,
                    registry,
                    failure is null
                        ? Text.Format(
                            nameof(Strings.Settings_Registries_Added),
                            registry,
                            catalog.Entries.Count)
                        : Text.Format(
                            nameof(Strings.Settings_Registries_Unreachable), failure.Message));
            },
            ActivationToken).ConfigureAwait(true);
    }

    private async Task SaveRegistriesAsync()
    {
        var registries = Registries.Select(entry => entry.Location).ToList();

        await SaveSerializedAsync(
            Text[nameof(Strings.Settings_Registries_Heading)],
            async ct =>
            {
                var current = await _packPreferences.ReadAsync(ct).ConfigureAwait(true);

                await _packPreferences
                    .WriteAsync(current with { Registries = registries }, ct)
                    .ConfigureAwait(true);

                OnPropertyChanged(nameof(UsesDefaultRegistry));
                OnPropertyChanged(nameof(GameBananaCanCheckNow));
                Saved();
            }).ConfigureAwait(true);
    }

    private void Saved() => ShowSaved = true;
}
