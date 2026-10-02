using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Core;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Core.Profiles;
using Xxsm.Core.Settings;
using Xxsm.Desktop.Services;
using Xxsm.Packs.GameBanana;

namespace Xxsm.Desktop.ViewModels.Pages;

/// <summary>What is in the game's Mods folder, and whether XXSM is watching it.</summary>
public sealed partial class ModsPageViewModel : PageViewModel, IRefreshablePage
{
    private readonly GameContext _game;
    private readonly IAppSettingsStore _settings;
    private readonly IModUpdateChecker _updates;
    private readonly IUrlLauncher _urls;
    private readonly INotificationService _notifications;
    private readonly IModsFolderWatcher _watcher;
    private readonly IUiDispatcher _dispatcher;
    private readonly ViewModelWorkRunner _runner;
    private readonly SortReviewViewModel _sortReview;
    private readonly ModInstallViewModel _install;
    private readonly DownloadsViewModel _downloads;
    private readonly Func<CancellationToken, Task> _rescan;
    private readonly Func<string, bool> _goToMod;
    private readonly ModUpdateViewModel _update;
    private readonly IProfileService _profiles;
    private readonly SwitchRunNotices _switchNotices;
    private readonly UpdateCheckRun _check;

    private IModsFolderWatch? _watch;

    /// <summary>Creates the page.</summary>
    public ModsPageViewModel(
        GameContext game,
        IModsFolderWatcher watcher,
        IUiDispatcher dispatcher,
        ViewModelWorkRunner runner,
        SortReviewViewModel sortReview,
        ModInstallViewModel install,
        DownloadsViewModel downloads,
        IAppSettingsStore settings,
        IModUpdateChecker updates,
        IUrlLauncher urls,
        INotificationService notifications,
        ITextCatalogue text,
        Func<CancellationToken, Task> rescan,
        Func<string, bool> goToMod,
        ModUpdateViewModel update,
        RandomiserViewModel randomiser,
        ModExportViewModel export,
        ModImportViewModel import,
        CharacterManagerViewModel characterManager,
        IProfileService profiles,
        SwitchRunNotices switchNotices,
        UpdateCheckRun check)
        : base(text)
    {
        ArgumentNullException.ThrowIfNull(check);
        _check = check;
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(switchNotices);
        _profiles = profiles;
        _switchNotices = switchNotices;
        AllOff = new ProfileApplyViewModel(text, ApplyAllOffAsync);
        ArgumentNullException.ThrowIfNull(goToMod);
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(randomiser);
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(import);
        ArgumentNullException.ThrowIfNull(characterManager);
        _update = update;
        Randomiser = randomiser;
        Export = export;
        Import = import;
        CharacterManager = characterManager;
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(urls);
        ArgumentNullException.ThrowIfNull(notifications);

        _settings = settings;
        _updates = updates;
        _urls = urls;
        _notifications = notifications;
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(watcher);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(sortReview);
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(rescan);

        _game = game;
        _watcher = watcher;
        _dispatcher = dispatcher;
        _runner = runner;
        _sortReview = sortReview;
        _install = install;
        _downloads = downloads;
        _rescan = rescan;
        _goToMod = goToMod;
    }

    /// <inheritdoc />
    public override string Heading => Text[nameof(Strings.Mods_Heading)];

    /// <summary>Whether the user has pointed this game at a folder.</summary>
    public bool HasFolder => _game.HasModsDirectory;

    /// <summary>The folder, for display.</summary>
    public string? Folder => _game.ModsDirectory;

    /// <summary>How many mods were found.</summary>
    public int ModCount => _game.Inventory?.ModCount ?? 0;

    /// <summary>How many of those 3DMigoto will load.</summary>
    public int EnabledModCount => _game.Inventory?.EnabledModCount ?? 0;

    /// <summary>How many character folders there are.</summary>
    public int VariantFolderCount => _game.Inventory?.VariantFolders.Count ?? 0;

    /// <summary>How many mods are still loose at the top level.</summary>
    public int UnfiledModCount => _game.Inventory?.UnfiledMods.Count ?? 0;

    /// <summary>Whether a scan has happened yet.</summary>
    public bool HasInventory => _game.Inventory is not null;

    /// <summary>The sort preview, shared with the character grid.</summary>
    public SortReviewViewModel SortReview => _sortReview;

    /// <summary>The install confirm step, shared with the character pages; finished downloads install here.</summary>
    public ModInstallViewModel Install => _install;

    /// <summary>What has been downloaded, and what is downloading now.</summary>
    public DownloadsViewModel Downloads => _downloads;

    /// <summary>Whether a panel is over the page, so the scrim is drawn.</summary>
    public bool IsAnyPanelOpen =>
        SortReview.IsOpen || Install.IsOpen || Update.IsOpen || Randomiser.IsOpen || Export.IsOpen ||
        Import.IsOpen || CharacterManager.IsOpen || AllOff.IsOpen;

    /// <summary>"Switch every mod off?": the apply panel over a profile of nothing, never saved.</summary>
    public ProfileApplyViewModel AllOff { get; }

    /// <summary>Opens <see cref="AllOff"/> on what is switched on now.</summary>
    [RelayCommand]
    private Task<bool> OpenAllOffAsync() => _runner.RunAsync(
        Heading,
        async ct =>
        {
            if (_game.ModsDirectory is { Length: > 0 } modsDirectory)
            {
                AllOff.OpenAllOff(await _profiles.PlanAllOffAsync(modsDirectory, ct).ConfigureAwait(true));
            }
        },
        ActivationToken);

    private Task<bool> ApplyAllOffAsync(ProfileApplyPlan plan) => _runner.RunAsync(
        Heading,
        async ct =>
        {
            var result = await _profiles.ApplyAsync(plan, ct).ConfigureAwait(true);

            await _rescan(ct).ConfigureAwait(true);
            _switchNotices.Report(plan.ModsDirectory, result, Text[nameof(Strings.AllOff_Done)]);
        },
        CancellationToken.None);

    /// <summary>What JASM or XX-Mod-Manager knew about the mods.</summary>
    public ModImportViewModel Import { get; }

    /// <summary>The Character Manager, for a character the import needs.</summary>
    public CharacterManagerViewModel CharacterManager { get; }

    /// <summary>Opens the import panel.</summary>
    [RelayCommand]
    private void OpenImport() => Track(Import.OpenAsync());

    /// <summary>Export.</summary>
    public ModExportViewModel Export { get; }

    /// <summary>Opens the export panel.</summary>
    [RelayCommand]
    private void OpenExport() => Track(Export.OpenAsync());

    /// <summary>"Update this mod?"</summary>
    public ModUpdateViewModel Update => _update;

    /// <summary>The randomiser.</summary>
    public RandomiserViewModel Randomiser { get; }

    /// <summary>Opens the randomiser over every character.</summary>
    [RelayCommand]
    private void OpenRandomiser() => Randomiser.Open();

    /// <summary>Shows what an auto-sort of the whole Mods folder would do; nothing moves until confirmed.</summary>
    [RelayCommand]
    private Task SortAllAsync() => _sortReview.OpenAsync(null, null, ActivationToken);

    /// <summary>Whether XXSM is watching the folder, or the user has to press Refresh (the watch limit hit).</summary>
    [ObservableProperty]
    private bool _isWatching;

    /// <summary>Why the watch is not running, in the OS's own words. Null when it is.</summary>
    [ObservableProperty]
    private string? _watchProblem;

    /// <summary>Rescans the folder.</summary>
    [RelayCommand]
    public Task RefreshAsync() =>
        _runner.RunAsync(Text[nameof(Strings.Mods_Heading)], _rescan, ActivationToken);

    // What is newer on GameBanana

    /// <summary>Every mod a check has found a newer version for; not dismissible, unlike the notice.</summary>
    public ObservableCollection<ModUpdateRowViewModel> Updates { get; } = [];

    /// <summary>Whether the GameBanana section has anything to say at all.</summary>
    [ObservableProperty]
    private bool _gameBananaEnabled;

    /// <summary>How many mods in this folder record a GameBanana page.</summary>
    [ObservableProperty]
    private int _linkedModCount;

    /// <summary>Whether a check is running now, started here, in Settings or at start-up.</summary>
    public bool IsCheckingUpdates => _check.IsRunning;

    /// <summary>Whether any mod has an update waiting.</summary>
    public bool HasUpdates => Updates.Count > 0;

    /// <summary>Whether anything at all is linked to a GameBanana page.</summary>
    public bool HasLinkedMods => LinkedModCount > 0;

    /// <summary>Whether to say nothing is waiting: only once something is linked.</summary>
    public bool ShowNothingWaiting => HasLinkedMods && !HasUpdates;

    /// <summary>What the card says when nothing is waiting, including how many are watched.</summary>
    public string UpdatesNoneText =>
        Text.ForCount(LinkedModCount, nameof(Strings.Mods_Updates_None_One), nameof(Strings.Mods_Updates_None), Text.Mods(LinkedModCount));

    /// <summary>A row took itself off the list: rescan, so its character's tile loses the mark too.</summary>
    private Task<bool> AfterDismissAsync() => _runner.RunAsync(
        Text[nameof(Strings.Mods_Updates_Heading)],
        async ct =>
        {
            await _rescan(ct).ConfigureAwait(true);
            await ReadUpdatesAsync(ct).ConfigureAwait(true);
        },
        CancellationToken.None);

    /// <summary>Goes to a mod on the list, or says why it cannot.</summary>
    private void GoToMod(string modFolder)
    {
        if (!_goToMod(modFolder))
        {
            _notifications.Add(
                NotificationSeverity.Warning,
                Text[nameof(Strings.Mods_Updates_Heading)],
                Text[nameof(Strings.Mods_Updates_Gone)]);
        }
    }

    /// <summary>Asks GameBanana about every linked mod, however recently it was asked.</summary>
    [RelayCommand]
    public Task CheckForUpdatesAsync() => CheckAsync(force: true);

    /// <summary>Reads the marks previous checks left, without asking GameBanana anything.</summary>
    internal Task ReloadUpdatesAsync() => ReadUpdatesAsync(ActivationToken);

    private async Task ReadUpdatesAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.ReadAsync(cancellationToken).ConfigureAwait(true);
        GameBananaEnabled = settings.GameBanana.Enabled;

        if (_game.ModsDirectory is not { Length: > 0 } mods)
        {
            Show([]);

            return;
        }

        var known = await _updates.ReadAsync(mods, cancellationToken).ConfigureAwait(true);
        Show(known);
    }

    /// <summary>Runs the shared check, or joins it, then shows what it found; leaving does not stop it.</summary>
    private async Task CheckAsync(bool force)
    {
        if (_game.ModsDirectory is not { Length: > 0 } mods)
        {
            return;
        }

        await _check.RunAsync(mods, force).ConfigureAwait(true);
        await _runner.RunAsync(
            Text[nameof(Strings.Mods_Updates_Heading)], ReadUpdatesAsync, CancellationToken.None).ConfigureAwait(true);
    }

    private void Show(IReadOnlyList<ModUpdateStatus> known)
    {
        Updates.Clear();

        foreach (var status in known.Where(status => status.HasUpdate))
        {
            Updates.Add(new ModUpdateRowViewModel(
                status, _urls, _updates, AfterDismissAsync, GoToMod, folder => _update.OpenAsync(folder)));
        }

        LinkedModCount = known.Count;

        OnPropertyChanged(nameof(HasUpdates));
        OnPropertyChanged(nameof(HasLinkedMods));
        OnPropertyChanged(nameof(UpdatesNoneText));
        OnPropertyChanged(nameof(ShowNothingWaiting));
    }

    /// <inheritdoc />
    protected override void OnActivated()
    {
        _game.PropertyChanged += OnGameChanged;

        // Shared singletons: heard while on screen, and let go of again, or they keep the page alive.
        _install.PropertyChanged += OnPanelChanged;
        _sortReview.PropertyChanged += OnPanelChanged;
        _update.PropertyChanged += OnPanelChanged;
        Randomiser.PropertyChanged += OnPanelChanged;
        Export.PropertyChanged += OnPanelChanged;
        Import.PropertyChanged += OnPanelChanged;
        CharacterManager.PropertyChanged += OnPanelChanged;
        AllOff.PropertyChanged += OnPanelChanged;
        _check.PropertyChanged += OnCheckChanged;
        OnPropertyChanged(nameof(IsCheckingUpdates));

        Refresh();
        Track(Import.RefreshAvailabilityAsync(ActivationToken));
        StartWatching();

        Track(_downloads.LoadAsync(ActivationToken));

        Track(ActivateUpdatesAsync());
    }

    /// <inheritdoc />
    protected override void OnDeactivated()
    {
        _game.PropertyChanged -= OnGameChanged;
        _install.PropertyChanged -= OnPanelChanged;
        _sortReview.PropertyChanged -= OnPanelChanged;
        _update.PropertyChanged -= OnPanelChanged;
        Randomiser.PropertyChanged -= OnPanelChanged;
        Export.PropertyChanged -= OnPanelChanged;
        Import.PropertyChanged -= OnPanelChanged;
        CharacterManager.PropertyChanged -= OnPanelChanged;
        AllOff.PropertyChanged -= OnPanelChanged;
        _check.PropertyChanged -= OnCheckChanged;
        _update.Close();
        AllOff.Close();
        Randomiser.Close();
        Export.Close();
        Import.Close();
        CharacterManager.Close();
        StopWatching();
    }

    private void OnCheckChanged(object? sender, PropertyChangedEventArgs e) =>
        OnPropertyChanged(nameof(IsCheckingUpdates));

    private void OnPanelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ModInstallViewModel.IsOpen) or nameof(SortReviewViewModel.IsOpen))
        {
            OnPropertyChanged(nameof(IsAnyPanelOpen));
        }
    }

    /// <summary>Shows what is already known, then runs the due check when the user has opted in.</summary>
    private async Task ActivateUpdatesAsync()
    {
        await ReloadUpdatesAsync().ConfigureAwait(true);

        var settings = await _settings.ReadAsync(ActivationToken).ConfigureAwait(true);

        if (settings.GameBanana is { Enabled: true, CheckForUpdates: true } && HasLinkedMods)
        {
            await CheckAsync(force: false).ConfigureAwait(true);
        }
    }

    private void OnGameChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Each panel holds a plan for the folder it opened on; export stays while copying.
        if (e.PropertyName is nameof(GameContext.GameId) or nameof(GameContext.ModsDirectory))
        {
            Randomiser.Close();
            Import.Close();
            Export.Close();
            AllOff.Close();
        }

        if (e.PropertyName is nameof(GameContext.Inventory) or nameof(GameContext.ModsDirectory))
        {
            Refresh();

            // Not while a check runs: it reloads the list itself when done.
            if (!IsCheckingUpdates)
            {
                Track(ReloadUpdatesAsync());
            }

            Track(Import.RefreshAvailabilityAsync(ActivationToken));

            if (e.PropertyName == nameof(GameContext.ModsDirectory))
            {
                // The folder itself changed, so the old watch is watching the wrong place.
                StopWatching();
                StartWatching();
            }
        }
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(HasFolder));
        OnPropertyChanged(nameof(Folder));
        OnPropertyChanged(nameof(ModCount));
        OnPropertyChanged(nameof(EnabledModCount));
        OnPropertyChanged(nameof(VariantFolderCount));
        OnPropertyChanged(nameof(UnfiledModCount));
        OnPropertyChanged(nameof(HasInventory));
    }

    /// <summary>Starts the debounced watch; its handle owns an OS resource, released on deactivation.</summary>
    private void StartWatching()
    {
        if (_game.ModsDirectory is not { Length: > 0 } directory)
        {
            IsWatching = false;
            WatchProblem = null;
            return;
        }

        try
        {
            var watch = _watcher.Watch(directory);
            watch.Changed += OnFolderChanged;
            watch.Degraded += OnWatchDegraded;
            _watch = watch;

            // A refused watch is data: the handle comes back degraded.
            IsWatching = !watch.IsDegraded;
            WatchProblem = watch.IsDegraded ? Degraded(watch.DegradedReason) : null;
        }
        catch (ModOperationException ex)
        {
            IsWatching = false;
            WatchProblem = Degraded(ex.Message);
        }
    }

    private string Degraded(string? reason) =>
        Text.Format(nameof(Strings.Mods_Watching_Degraded), reason ?? string.Empty);

    private void StopWatching()
    {
        if (_watch is { } watch)
        {
            // Both halves on purpose: unsubscribe, or the watcher keeps this alive; dispose, or the handle leaks.
            watch.Changed -= OnFolderChanged;
            watch.Degraded -= OnWatchDegraded;
            watch.Dispose();
        }

        _watch = null;
        IsWatching = false;
    }

    /// <summary>Raised on a worker thread, so the rescan is posted to the UI thread.</summary>
    private void OnFolderChanged(object? sender, ModsFolderChangedEventArgs e) =>
        _dispatcher.Post(() => Track(RefreshAsync()));

    /// <summary>The watch stopped working after it started; only the watch still held is believed.</summary>
    private void OnWatchDegraded(object? sender, EventArgs e) =>
        _dispatcher.Post(() =>
        {
            if (sender is IModsFolderWatch watch && ReferenceEquals(watch, _watch))
            {
                IsWatching = false;
                WatchProblem = Degraded(watch.DegradedReason);
            }
        });
}
