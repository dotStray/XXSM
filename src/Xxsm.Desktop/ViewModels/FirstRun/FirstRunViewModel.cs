using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Core.Mods;
using Xxsm.Core.Settings;
using Xxsm.Desktop.Services;
using Xxsm.Packs.Registry;

namespace Xxsm.Desktop.ViewModels.FirstRun;

/// <summary>Which part of the first-run flow is on screen.</summary>
public enum FirstRunStep
{
    /// <summary>What XXSM is about to do.</summary>
    Welcome = 0,

    /// <summary>Choosing and installing a Game Pack.</summary>
    Pack = 1,

    /// <summary>Choosing the Mods folder.</summary>
    ModsFolder = 2,

    /// <summary>A summary of what was set up.</summary>
    Done = 3,
}

/// <summary>Walks an empty install to a working pack and Mods folder. Every step can be skipped.</summary>
public sealed partial class FirstRunViewModel(
    IPackService packs,
    IAppSettingsStore settings,
    IModsFolderProbe probe,
    IStoragePicker folders,
    INotificationService notifications,
    ViewModelWorkRunner runner,
    ITextCatalogue text,
    IGameIconProvider icons,
    IUiDispatcher ui,
    Func<Task> completed) : ViewModelBase
{
    private readonly IGameIconProvider _icons = icons;
    private readonly IUiDispatcher _ui = ui;
    private readonly IPackService _packs = packs;
    private readonly IAppSettingsStore _settings = settings;
    private readonly IModsFolderProbe _probe = probe;
    private readonly IStoragePicker _folders = folders;
    private readonly INotificationService _notifications = notifications;
    private readonly ViewModelWorkRunner _runner = runner;
    private readonly ITextCatalogue _text = text;
    private readonly Func<Task> _completed = completed;

    /// <summary>The step on screen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcome))]
    [NotifyPropertyChangedFor(nameof(IsPack))]
    [NotifyPropertyChangedFor(nameof(IsModsFolder))]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    [NotifyPropertyChangedFor(nameof(StepText))]
    [NotifyPropertyChangedFor(nameof(Heading))]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    private FirstRunStep _step = FirstRunStep.Welcome;

    /// <summary>The packs on offer.</summary>
    public ObservableCollection<PackChoiceViewModel> Packs { get; } = [];

    /// <summary>The one the user has selected.</summary>
    [ObservableProperty]
    private PackChoiceViewModel? _selectedPack;

    /// <summary>The game id of the pack that was installed, when one was.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInstalledPack))]
    private string? _installedGameId;

    /// <summary>What was installed, in words.</summary>
    [ObservableProperty]
    private string? _installedPackSummary;

    /// <summary>Set when every configured registry failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOffline))]
    private string? _registryProblem;

    /// <summary>Every installed game, each with its own Mods folder.</summary>
    public ObservableCollection<FirstRunGameFolderViewModel> GameFolders { get; } = [];

    /// <summary>Whether any game is installed to choose a folder for.</summary>
    public bool HasGameFolders => GameFolders.Count > 0;

    /// <summary>What a reset did, when this start follows one; null otherwise.</summary>
    [ObservableProperty]
    private string? _resetNote;

    /// <summary>What a reset could not do, in the operating system's words; null when it did everything.</summary>
    [ObservableProperty]
    private string? _resetProblem;

    /// <summary>Whether the welcome step is showing.</summary>
    public bool IsWelcome => Step == FirstRunStep.Welcome;

    /// <summary>Whether the pack step is showing.</summary>
    public bool IsPack => Step == FirstRunStep.Pack;

    /// <summary>Whether the Mods-folder step is showing.</summary>
    public bool IsModsFolder => Step == FirstRunStep.ModsFolder;

    /// <summary>Whether the summary is showing.</summary>
    public bool IsDone => Step == FirstRunStep.Done;

    /// <summary>Whether a pack was installed during the flow.</summary>
    public bool HasInstalledPack => InstalledGameId is { Length: > 0 };

    /// <summary>Whether no registry could be reached.</summary>
    public bool IsOffline => RegistryProblem is { Length: > 0 };

    /// <summary>Whether there is anything on offer.</summary>
    public bool HasPacks => Packs.Count > 0;

    /// <summary>The step's own title; only the welcome step says "Welcome to XXSM".</summary>
    public string Heading => Step switch
    {
        FirstRunStep.Welcome => _text[nameof(Strings.FirstRun_Welcome_Heading)],
        FirstRunStep.Pack => _text[nameof(Strings.FirstRun_Pack_Heading)],
        FirstRunStep.ModsFolder => _text[nameof(Strings.FirstRun_Mods_Heading)],
        FirstRunStep.Done => _text[nameof(Strings.FirstRun_Done_Heading)],
        _ => _text[nameof(Strings.FirstRun_Title)],
    };

    /// <summary>"Step 2 of 4".</summary>
    public string StepText => _text.Format(nameof(Strings.FirstRun_Step), (int)Step + 1, 4);

    /// <summary>Whether Back does anything.</summary>
    public bool CanGoBack => Step > FirstRunStep.Welcome;

    /// <summary>Whether Next does anything.</summary>
    public bool CanGoNext => Step < FirstRunStep.Done;

    /// <summary>Moves to the next step, loading it if it needs loading.</summary>
    [RelayCommand]
    public Task NextAsync()
    {
        if (!CanGoNext)
        {
            return Task.CompletedTask;
        }

        Step++;

        return Step switch
        {
            FirstRunStep.Pack => LoadPacksAsync(),
            FirstRunStep.ModsFolder => LoadGameFoldersAsync(),
            _ => Task.CompletedTask,
        };
    }

    /// <summary>Moves back a step.</summary>
    [RelayCommand]
    public void Back()
    {
        if (CanGoBack)
        {
            Step--;
        }
    }

    /// <summary>Downloads, verifies and installs the selected pack.</summary>
    [RelayCommand]
    public Task InstallSelectedPackAsync()
    {
        if (SelectedPack is not { CanInstall: true } choice)
        {
            return Task.CompletedTask;
        }

        IsBusy = true;
        BusyMessage = _text.Format(nameof(Strings.Packs_Working_Game), choice.DisplayName);
        choice.ShowProgress(new PackInstallProgress(PackInstallStage.Downloading, 0, null), _text);

        return Run(async ct =>
        {
            try
            {
                var result = await _packs
                    .InstallAsync(choice.GameId, progress: choice.ProgressOn(_ui, _text), cancellationToken: ct)
                    .ConfigureAwait(true);

                InstalledGameId = result.GameId;

                InstalledPackSummary =
                    _text.Format(nameof(Strings.FirstRun_Pack_Installed), choice.DisplayName, PackVersionText.Display(result.PackVersion));

                _notifications.Add(
                    NotificationSeverity.Information,
                    choice.DisplayName,
                    InstalledPackSummary);

                await LoadPacksAsync().ConfigureAwait(true);
            }
            finally
            {
                IsBusy = false;
                BusyMessage = null;
                choice.ClearProgress();
            }
        });
    }

    /// <summary>Asks for one game's Mods folder, describes it, and saves it if it will work.</summary>
    /// <param name="game">The game the folder is for.</param>
    [RelayCommand]
    public async Task BrowseForModsFolderAsync(FirstRunGameFolderViewModel? game)
    {
        if (game is null)
        {
            return;
        }

        // The games' Mods folders usually sit side by side, so the second opens next to the first.
        var startAt = game.ModsDirectory ?? GameFolders.FirstOrDefault(other => other.HasModsDirectory)?.ModsDirectory;

        var chosen = await _folders
            .PickFolderAsync(_text.Format(nameof(Strings.FirstRun_Mods_PickerTitle), game.DisplayName), startAt, ActivationToken)
            .ConfigureAwait(true);

        if (chosen is not { Length: > 0 })
        {
            return;
        }

        await Run(async ct =>
        {
            var probe = await _probe.ProbeAsync(chosen, ct).ConfigureAwait(true);

            game.Note = probe.Summary;

            if (!probe.IsUsable)
            {
                return;
            }

            game.ModsDirectory = probe.Path;

            await _settings.UpdateGameAsync(
                game.GameId, current => current with { ModsDirectory = probe.Path }, ct)
                .ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    /// <summary>Lists every installed game for the Mods folder step, with the folder saved for each.</summary>
    internal Task LoadGameFoldersAsync() => Run(async ct =>
    {
        var settings = await _settings.ReadAsync(ct).ConfigureAwait(true);
        var before = GameFolders.ToDictionary(game => game.GameId, StringComparer.OrdinalIgnoreCase);

        GameFolders.Clear();

        foreach (var pack in Packs.Where(pack => pack.IsInstalled))
        {
            GameFolders.Add(new FirstRunGameFolderViewModel(
                pack.GameId,
                pack.DisplayName,
                _icons.Installed(pack.GameId, pack.DisplayName),
                settings.ForGame(pack.GameId).ModsDirectory)
            {
                Note = before.TryGetValue(pack.GameId, out var earlier) ? earlier.Note : null,
            });
        }

        OnPropertyChanged(nameof(HasGameFolders));
    });

    /// <summary>Marks setup complete and hands over to the shell.</summary>
    [RelayCommand]
    public Task FinishAsync() => CompleteAsync();

    /// <summary>Leaves setup without finishing it. Nothing is lost; settings can finish it.</summary>
    [RelayCommand]
    public Task SkipAsync() => CompleteAsync();

    /// <inheritdoc />
    protected override void OnActivated()
    {
        if (Step == FirstRunStep.Pack)
        {
            Track(LoadPacksAsync());
        }
    }

    /// <summary>Fetches the catalogue from every configured registry.</summary>
    internal Task LoadPacksAsync()
    {
        IsBusy = true;
        BusyMessage = _text[nameof(Strings.FirstRun_Pack_Loading)];

        return Run(async ct =>
        {
            try
            {
                var catalog = await _packs.GetCatalogAsync(cancellationToken: ct).ConfigureAwait(true);

                Packs.Clear();

                foreach (var entry in catalog.Entries)
                {
                    Packs.Add(new PackChoiceViewModel(entry, _text)
                    {
                        Icon = _icons.Installed(entry.GameId, entry.DisplayName),
                    });
                }

                SelectedPack = Packs.FirstOrDefault(pack => pack.CanInstall) ?? Packs.FirstOrDefault();

                RegistryProblem = catalog.Failures.Count > 0 && catalog.Entries.Count == 0
                    ? string.Join(" ", catalog.Failures.Select(failure => failure.Message))
                    : null;

                foreach (var failure in catalog.Failures)
                {
                    _notifications.Add(
                        NotificationSeverity.Warning, failure.Registry, failure.Message);
                }

                OnPropertyChanged(nameof(HasPacks));
            }
            finally
            {
                IsBusy = false;
                BusyMessage = null;
            }
        });
    }

    /// <summary>Whether to show the Pack Studio tab — the last step offers it.</summary>
    [ObservableProperty]
    private bool _showPackStudio;

    /// <summary>Whether to let XXSM look mods up on GameBanana — the last step offers it.</summary>
    [ObservableProperty]
    private bool _enableGameBanana;

    /// <summary>Whether Game Packs update by themselves — the last step offers it.</summary>
    [ObservableProperty]
    private bool _autoUpdatePacks;

    private async Task CompleteAsync()
    {
        await Run(async ct =>
        {
            // The three switches only ever turn something on.
            await _settings
                .UpdateAsync(
                    current => current with
                    {
                        FirstRunCompleted = true,
                        LastGameId = InstalledGameId ?? current.LastGameId,
                        ShowPackStudio = current.ShowPackStudio || ShowPackStudio,
                        GameBanana = EnableGameBanana ? current.GameBanana with { Enabled = true } : current.GameBanana,
                    },
                    ct)
                .ConfigureAwait(true);

            if (AutoUpdatePacks)
            {
                await _packs.SetAutoUpdateAsync(true, ct).ConfigureAwait(true);
            }
        }).ConfigureAwait(true);

        // Runs even when the write failed, so a settings file cannot trap the user in setup.
        await _completed().ConfigureAwait(true);
    }

    private Task<bool> Run(Func<CancellationToken, Task> work) =>
        _runner.RunAsync(_text[nameof(Strings.FirstRun_Title)], work, ActivationToken);
}
