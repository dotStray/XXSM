using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Core.Io;
using Xxsm.Desktop.Services;
using Xxsm.Packs.Installation;
using Xxsm.Packs.Merge;
using Xxsm.Packs.Registry;

namespace Xxsm.Desktop.ViewModels.Pages;

/// <summary>The Game Pack browser: what is on offer, what is installed, and moving between the two.</summary>
/// <remarks>Unlike the game selector, it reads the network; opening it is the refresh.</remarks>
public sealed partial class PacksPageViewModel : PageViewModel, IRefreshablePage
{
    private readonly IGameIconProvider _icons;
    private readonly IPackService _packs;
    private readonly IInstalledPacks _installed;
    private readonly INotificationService _notifications;
    private readonly IUiDispatcher _ui;
    private readonly ViewModelWorkRunner _runner;
    private readonly PackInstallFollowUp _followUp;
    private readonly ISkippedUpdatesStore _skippedStore;
    private readonly IPackChangesStore _changesStore;
    private readonly PackUpdateWatcher _packUpdates;
    private readonly IStoragePicker _picker;
    private readonly Func<CancellationToken, Task> _reloadGames;
    private readonly Func<string, Task> _editInStudio;

    /// <summary>Creates the page.</summary>
    public PacksPageViewModel(
        IPackService packs,
        IInstalledPacks installed,
        INotificationService notifications,
        ViewModelWorkRunner runner,
        PackInstallFollowUp followUp,
        ISkippedUpdatesStore skippedStore,
        IPackChangesStore changesStore,
        PackUpdateWatcher packUpdates,
        IStoragePicker picker,
        ITextCatalogue text,
        IGameIconProvider icons,
        StudioVisibility studio,
        IUiDispatcher ui,
        Func<CancellationToken, Task> reloadGames,
        Func<string, Task> editInStudio)
        : base(text)
    {
        ArgumentNullException.ThrowIfNull(ui);
        _ui = ui;
        ArgumentNullException.ThrowIfNull(icons);
        _icons = icons;
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(followUp);
        ArgumentNullException.ThrowIfNull(skippedStore);
        ArgumentNullException.ThrowIfNull(changesStore);
        ArgumentNullException.ThrowIfNull(packUpdates);
        ArgumentNullException.ThrowIfNull(picker);
        ArgumentNullException.ThrowIfNull(reloadGames);
        ArgumentNullException.ThrowIfNull(editInStudio);
        ArgumentNullException.ThrowIfNull(studio);

        _packs = packs;
        _installed = installed;
        _notifications = notifications;
        _runner = runner;
        _followUp = followUp;
        _skippedStore = skippedStore;
        _changesStore = changesStore;
        _packUpdates = packUpdates;
        _picker = picker;
        _reloadGames = reloadGames;
        _editInStudio = editInStudio;
        Studio = studio;
    }

    /// <summary>Whether Pack Studio is switched on; <em>Edit in Studio</em> shows only then.</summary>
    public StudioVisibility Studio { get; }

    /// <inheritdoc />
    public override string Heading => Text[nameof(Strings.Packs_Heading)];

    /// <summary>Every game any configured source offers, plus anything already installed.</summary>
    public ObservableCollection<PackChoiceViewModel> Packs { get; } = [];

    /// <summary>Sources that could not be read, and why.</summary>
    public ObservableCollection<RegistryFailure> Failures { get; } = [];

    /// <summary>Whether there is anything to show.</summary>
    public bool HasPacks => Packs.Count > 0;

    /// <summary>Whether a source failed. Not an error on its own.</summary>
    public bool HasFailures => Failures.Count > 0;

    /// <summary>Whether the empty state should be shown instead of the list.</summary>
    public bool IsEmpty => !IsBusy && Packs.Count == 0;

    /// <summary>Fetches the catalogue from every configured source.</summary>
    [RelayCommand]
    public Task RefreshAsync()
    {
        IsBusy = true;
        BusyMessage = Text[nameof(Strings.Packs_Loading)];
        Notify();

        return _runner.RunAsync(
            Text[nameof(Strings.Packs_Heading)],
            async ct =>
            {
                try
                {
                    var catalog = await _packs.GetCatalogAsync(cancellationToken: ct)
                        .ConfigureAwait(true);

                    _packUpdates.Recount(catalog);

                    var expanded = Packs
                        .Where(pack => pack.IsVersionsExpanded)
                        .Select(pack => pack.GameId)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    Packs.Clear();
                    Failures.Clear();

                    foreach (var entry in catalog.Entries)
                    {
                        Packs.Add(new PackChoiceViewModel(
                            entry,
                            Text,
                            await CountWithheldAsync(entry.GameId, ct).ConfigureAwait(true),
                            entry.IsInstalled && _installed.HasCorrections(entry.GameId),
                            catalog.RemoveOldVersionsAfterDays,
                            (row, keep) => Track(KeepVersionAsync(row, keep)))
                        {
                            Icon = _icons.Installed(entry.GameId, entry.DisplayName),
                            IsVersionsExpanded = expanded.Contains(entry.GameId),
                            HasWhatsNew = await HasWhatsNewAsync(entry, ct).ConfigureAwait(true),
                            Contents = await ContentsAsync(entry, ct).ConfigureAwait(true),
                        });
                    }

                    foreach (var failure in catalog.Failures)
                    {
                        Failures.Add(failure);
                    }
                }
                finally
                {
                    IsBusy = false;
                    BusyMessage = null;
                    Notify();
                }
            },
            ActivationToken);
    }

    /// <summary><em>What's new</em>: what a game's last update changed.</summary>
    public WhatsNewViewModel WhatsNew => _followUp.WhatsNew;

    /// <summary>The <em>Skipped updates</em> review, after an update that withheld changes from an edit.</summary>
    public SkippedUpdatesViewModel SkippedUpdates => _followUp.SkippedUpdates;

    /// <summary>The offer to clear corrections an installed pack now makes itself.</summary>
    public CaughtUpCorrectionsViewModel CaughtUp => _followUp.CaughtUp;

    /// <summary>Whether a panel is over the page.</summary>
    public bool IsAnyPanelOpen => _followUp.IsAnyPanelOpen;

    /// <summary>Downloads, verifies and installs one game's pack.</summary>
    /// <param name="choice">The game to install or update.</param>
    [RelayCommand]
    public Task InstallAsync(PackChoiceViewModel? choice)
    {
        if (choice is null)
        {
            return Task.CompletedTask;
        }

        IsBusy = true;
        BusyMessage = Text.Format(nameof(Strings.Packs_Working_Game), choice.DisplayName);
        Notify();
        choice.ShowProgress(new PackInstallProgress(PackInstallStage.Downloading, 0, null), Text);

        return _runner.RunAsync(
            choice.DisplayName,
            async ct =>
            {
                try
                {
                    var result = await _packs
                        .InstallAsync(choice.GameId, progress: choice.ProgressOn(_ui, Text), cancellationToken: ct)
                        .ConfigureAwait(true);

                    _notifications.Add(NotificationSeverity.Information, choice.DisplayName, Describe(result, choice.DisplayName));

                    // The selector is built from what is installed: rebuild it before the new game can be chosen.
                    await _reloadGames(ct).ConfigureAwait(true);
                    await _followUp.AfterInstallAsync(result, choice.DisplayName, ct).ConfigureAwait(true);
                    await RemoveOldVersionsAsync(choice.GameId, ct).ConfigureAwait(true);
                }
                finally
                {
                    IsBusy = false;
                    BusyMessage = null;
                    choice.ClearProgress();
                    Notify();
                }

                await RefreshAsync().ConfigureAwait(true);
            },
            ActivationToken);
    }

    /// <summary>What an install did, naming the game as the card does rather than by its id.</summary>
    /// <param name="result">What happened.</param>
    /// <param name="displayName">The game's name.</param>
    /// <returns>The sentence.</returns>
    internal string Describe(PackOperationResult result, string displayName) => result switch
    {
        { Outcome: PackOperationOutcome.Installed, PreviousVersion: { Length: > 0 } from, PackVersion: { } to }
            when !string.Equals(from, to, StringComparison.Ordinal) =>
            Text.Format(nameof(Strings.Packs_Updated_Notice), displayName, PackVersionText.Display(from), PackVersionText.Display(to)),
        { Outcome: PackOperationOutcome.Installed, PackVersion: { } version } =>
            Text.Format(nameof(Strings.Packs_Installed_Notice), displayName, PackVersionText.Display(version)),
        _ => result.Message,
    };

    /// <summary>Installs a pack zip someone handed over, with the same checks as one from a registry.</summary>
    [RelayCommand]
    public Task ImportFromFileAsync() =>
        _runner.RunAsync(
            Heading,
            async ct =>
            {
                var file = await _picker
                    .PickFileAsync(
                        Text[nameof(Strings.Packs_ImportFile_Title)],
                        [new FileTypeFilter(Text[nameof(Strings.Packs_ImportFile_Type)], [".zip"])],
                        null,
                        ct)
                    .ConfigureAwait(true);

                if (file is null)
                {
                    return;
                }

                IsBusy = true;
                BusyMessage = Text[nameof(Strings.Packs_Working)];
                Notify();

                try
                {
                    var result = await _packs.ImportAsync(file, cancellationToken: ct).ConfigureAwait(true);
                    var name = Packs.FirstOrDefault(p => string.Equals(p.GameId, result.GameId, StringComparison.OrdinalIgnoreCase))?.DisplayName
                               ?? result.GameId;
                    _notifications.Add(NotificationSeverity.Information, name, Describe(result, name));
                    await _reloadGames(ct).ConfigureAwait(true);
                    await _followUp.AfterInstallAsync(result, name, ct).ConfigureAwait(true);
                }
                finally
                {
                    IsBusy = false;
                    BusyMessage = null;
                    Notify();
                }

                await RefreshAsync().ConfigureAwait(true);
            },
            ActivationToken);

    /// <summary>Opens the <em>Skipped updates</em> review again, for a game whose update withheld something.</summary>
    /// <param name="choice">The game whose review to open.</param>
    [RelayCommand]
    public Task OpenSkippedUpdatesAsync(PackChoiceViewModel? choice)
    {
        if (choice is not { HasWithheldUpdates: true })
        {
            return Task.CompletedTask;
        }

        return _runner.RunAsync(
            choice.DisplayName,
            async ct =>
            {
                if (await _skippedStore.ReadAsync(choice.GameId, ct).ConfigureAwait(true) is { } record)
                {
                    SkippedUpdates.Show(record.Updates, record.PackVersion, record.GameId, choice.DisplayName);
                    return;
                }

                // Gone since the page was built: the card is stale, so rebuild it.
                await RefreshAsync().ConfigureAwait(true);
            },
            ActivationToken);
    }

    /// <summary>Opens <em>What's new</em> again: what a game's last update changed.</summary>
    [RelayCommand]
    public Task OpenWhatsNewAsync(PackChoiceViewModel? choice)
    {
        if (choice is not { HasWhatsNew: true })
        {
            return Task.CompletedTask;
        }

        return _runner.RunAsync(
            choice.DisplayName,
            async ct =>
            {
                if (await _changesStore.ReadAsync(choice.GameId, ct).ConfigureAwait(true) is { } changes)
                {
                    WhatsNew.Show(changes, choice.DisplayName);
                    return;
                }

                // Gone since the page was built: the card is stale, so rebuild it.
                await RefreshAsync().ConfigureAwait(true);
            },
            ActivationToken);
    }

    /// <summary>What the installed pack holds, for the line under its name, or null; unreadable is a notice.</summary>
    private async Task<string?> ContentsAsync(PackCatalogEntry entry, CancellationToken cancellationToken)
    {
        if (!entry.IsInstalled)
        {
            return null;
        }

        try
        {
            return await _packs.ReadContentsAsync(entry.GameId, cancellationToken).ConfigureAwait(true) is { } contents
                ? Text.PackContents(contents)
                : null;
        }
        catch (Xxsm.Core.XxsmException ex)
        {
            _notifications.Add(NotificationSeverity.Warning, entry.DisplayName, ex.Message);
            return null;
        }
    }

    /// <summary>Whether a card offers What's new: its last update is recorded and in use; unreadable is none.</summary>
    private async Task<bool> HasWhatsNewAsync(PackCatalogEntry entry, CancellationToken cancellationToken)
    {
        if (!entry.IsInstalled)
        {
            return false;
        }

        try
        {
            var changes = await _changesStore.ReadAsync(entry.GameId, cancellationToken).ConfigureAwait(true);

            return changes is not null && string.Equals(changes.ToVersion, entry.ActiveVersion, StringComparison.OrdinalIgnoreCase);
        }
        catch (Xxsm.Core.XxsmException ex)
        {
            _notifications.Add(NotificationSeverity.Warning, entry.DisplayName, ex.Message);
            return false;
        }
    }

    /// <summary>How many characters a game's last update withheld changes from; unreadable is a notice and 0.</summary>
    private async Task<int> CountWithheldAsync(string gameId, CancellationToken cancellationToken)
    {
        try
        {
            var record = await _skippedStore.ReadAsync(gameId, cancellationToken).ConfigureAwait(true);

            return record?.Updates.Count ?? 0;
        }
        catch (Xxsm.Core.XxsmException ex)
        {
            _notifications.Add(NotificationSeverity.Warning, gameId, ex.Message);
            return 0;
        }
    }

    /// <summary>Opens an installed game in Pack Studio, to change its pack and export it again.</summary>
    [RelayCommand]
    public Task EditInStudioAsync(PackChoiceViewModel? choice) =>
        choice is { IsInstalled: true } && Studio.IsShown ? _editInStudio(choice.GameId) : Task.CompletedTask;

    // Installed versions

    /// <summary>Moves old installed versions to the trash, one notice per game with an Undo for all of them.</summary>
    /// <param name="gameId">One game, or null for every game.</param>
    /// <param name="cancellationToken">Cancels between versions.</param>
    /// <returns>A task that completes when the old versions are gone and said so.</returns>
    public async Task RemoveOldVersionsAsync(string? gameId, CancellationToken cancellationToken)
    {
        PackPruneResult result;

        try
        {
            result = await _installed.RemoveOldVersionsAsync(gameId, cancellationToken).ConfigureAwait(true);
        }
        catch (Xxsm.Core.XxsmException ex)
        {
            _notifications.Add(NotificationSeverity.Warning, Text[nameof(Strings.Packs_Heading)], ex.Message);
            return;
        }

        var days = Text.Days(result.AfterDays);

        foreach (var game in result.Removed.GroupBy(removal => removal.GameId, StringComparer.OrdinalIgnoreCase))
        {
            var removals = game.ToList();
            var name = removals[0].DisplayName is { Length: > 0 } shown ? shown : game.Key;
            Notification? notice = null;

            var undo = new AsyncRelayCommand(async () =>
            {
                var all = true;

                foreach (var removal in removals)
                {
                    all &= await RestoreAsync(removal, name).ConfigureAwait(true);
                }

                if (all && notice is not null)
                {
                    _notifications.Dismiss(notice);
                }
            });

            notice = _notifications.Add(
                NotificationSeverity.Information,
                name,
                Text.Format(
                    nameof(Strings.Packs_OldVersions_Removed),
                    Text.OldVersions(removals.Count),
                    name,
                    days,
                    string.Join(", ", removals.Select(removal => PackVersionText.Display(removal.PackVersion)))),
                action: undo,
                actionText: Text[nameof(Strings.Notifications_Undo)]);
        }

        foreach (var failure in result.Failed)
        {
            _notifications.Add(
                NotificationSeverity.Warning,
                failure.GameId,
                Text.Format(nameof(Strings.Packs_OldVersions_Failed), PackVersionText.Display(failure.PackVersion), failure.Message));
        }

        // With every game's tidying, the folders crashed installs left behind go too.
        if (gameId is null)
        {
            var leftovers = await _installed.RemoveLeftoversAsync(cancellationToken).ConfigureAwait(true);

            foreach (var failure in leftovers.Failed)
            {
                _notifications.Add(
                    NotificationSeverity.Warning,
                    Text[nameof(Strings.Packs_Heading)],
                    Text.Format(nameof(Strings.Packs_Leftover_Failed), PathDisplay.Show(failure.Path), failure.Message));
            }
        }

        if (result.Removed.Count > 0 && IsActive)
        {
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Saves a version's <em>Keep</em> box, and says what it means.</summary>
    /// <param name="row">The version.</param>
    /// <param name="keep">Whether it is kept now.</param>
    internal Task KeepVersionAsync(PackVersionViewModel row, bool keep) =>
        _runner.RunAsync(
            row.Game.DisplayName,
            async ct =>
            {
                await _installed.KeepVersionAsync(row.Game.GameId, row.PackVersion, keep, ct).ConfigureAwait(true);

                _notifications.Add(
                    NotificationSeverity.Information,
                    row.Game.DisplayName,
                    Text.Format(keep ? nameof(Strings.Packs_Version_Kept) : nameof(Strings.Packs_Version_Unkept), row.VersionText));
            },
            ActivationToken);

    /// <summary>Makes one installed version the one the game uses.</summary>
    [RelayCommand]
    public Task UseVersionAsync(PackVersionViewModel? row)
    {
        if (row is not { CanUse: true })
        {
            return Task.CompletedTask;
        }

        return RunChangeAsync(
            row.Game.DisplayName,
            async ct =>
            {
                var preference = await _installed.UseVersionAsync(row.Game.GameId, row.PackVersion, ct)
                    .ConfigureAwait(true);

                _notifications.Add(
                    NotificationSeverity.Information,
                    row.Game.DisplayName,
                    Text.Format(
                        preference.PinnedVersion is null
                            ? nameof(Strings.Packs_Version_Used_Newest)
                            : nameof(Strings.Packs_Version_Used_Held),
                        row.PackVersion));
            });
    }

    /// <summary>Stops a game staying on an older version, as <em>Use</em> on the newest installed would.</summary>
    /// <param name="choice">The held game.</param>
    /// <returns>A task that completes when the game is loaded at the newest installed version.</returns>
    [RelayCommand]
    public Task FollowUpdatesAsync(PackChoiceViewModel? choice)
    {
        if (choice is not { IsHeld: true, Versions.Count: > 0 })
        {
            return Task.CompletedTask;
        }

        var held = choice.InstalledVersion;

        return RunChangeAsync(
            choice.DisplayName,
            async ct =>
            {
                await _installed.UseVersionAsync(choice.GameId, choice.Versions[0].PackVersion, ct).ConfigureAwait(true);

                _notifications.Add(
                    NotificationSeverity.Information,
                    choice.DisplayName,
                    Text.Format(nameof(Strings.Packs_FollowUpdates_Done), PackVersionText.Display(held)));
            });
    }

    /// <summary>Moves one installed version to the trash, with an Undo on the notice.</summary>
    [RelayCommand]
    public Task DeleteVersionAsync(PackVersionViewModel? row)
    {
        if (row is null)
        {
            return Task.CompletedTask;
        }

        return RunChangeAsync(
            row.Game.DisplayName,
            async ct =>
            {
                var removal = await _installed.RemoveVersionAsync(row.Game.GameId, row.PackVersion, ct)
                    .ConfigureAwait(true);

                NotifyWithUndo(
                    removal,
                    row.Game.DisplayName,
                    Text.Format(nameof(Strings.Packs_Version_Deleted), row.VersionText, row.Game.DisplayName));
            });
    }

    /// <summary>The game waiting on its "Remove it?" question, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRemoveOpen), nameof(RemoveQuestion), nameof(RemoveCorrectionsText), nameof(CanRemoveCorrections))]
    private PackChoiceViewModel? _packToRemove;

    /// <summary>Whether the user's corrections for the game go too. Unticked each time the question opens.</summary>
    [ObservableProperty]
    private bool _removeCorrections;

    /// <summary>Whether the question is showing.</summary>
    public bool IsRemoveOpen => PackToRemove is not null;

    /// <summary>"Remove the pack for Genshin Impact? …".</summary>
    public string RemoveQuestion => PackToRemove is { } choice
        ? Text.Format(nameof(Strings.Packs_Remove_Question), choice.DisplayName)
        : string.Empty;

    /// <summary>Whether there are corrections to offer to remove: the box shows only then.</summary>
    public bool CanRemoveCorrections => PackToRemove is { HasCorrections: true };

    /// <summary>"Also remove my corrections and characters for Genshin Impact".</summary>
    public string RemoveCorrectionsText => PackToRemove is { } choice
        ? Text.Format(nameof(Strings.Packs_Remove_Corrections), choice.DisplayName)
        : string.Empty;

    /// <summary>Asks whether to remove a game's pack. Nothing moves until the answer is yes.</summary>
    [RelayCommand]
    public void AskToRemove(PackChoiceViewModel? choice)
    {
        if (choice is { IsInstalled: true })
        {
            RemoveCorrections = false;
            PackToRemove = choice;
        }
    }

    /// <summary>Backs out of removing a pack.</summary>
    [RelayCommand]
    public void CancelRemove() => PackToRemove = null;

    /// <summary>Moves every installed version to the trash, the corrections too if ticked, with Undo.</summary>
    [RelayCommand]
    public Task ConfirmRemoveAsync()
    {
        if (PackToRemove is not { } choice)
        {
            return Task.CompletedTask;
        }

        var withCorrections = RemoveCorrections && choice.HasCorrections;
        PackToRemove = null;

        return RunChangeAsync(
            choice.DisplayName,
            async ct =>
            {
                var removal = await _installed.RemoveAsync(choice.GameId, withCorrections, ct).ConfigureAwait(true);

                NotifyWithUndo(
                    removal,
                    choice.DisplayName,
                    Text.Format(
                        removal.CorrectionsRemoved ? nameof(Strings.Packs_Removed_WithCorrections) : nameof(Strings.Packs_Removed),
                        choice.DisplayName));
            });
    }

    /// <summary>Runs a change to what is installed, then rebuilds the selector and the page even on failure.</summary>
    private Task<bool> RunChangeAsync(string title, Func<CancellationToken, Task> change)
    {
        IsBusy = true;
        BusyMessage = Text[nameof(Strings.Packs_Working)];
        Notify();

        return _runner.RunAsync(
            title,
            async ct =>
            {
                try
                {
                    try
                    {
                        await change(ct).ConfigureAwait(true);
                    }
                    finally
                    {
                        await _reloadGames(ct).ConfigureAwait(true);
                    }
                }
                finally
                {
                    IsBusy = false;
                    BusyMessage = null;
                    Notify();

                    await RefreshAsync().ConfigureAwait(true);
                }
            },
            ActivationToken);
    }

    private void NotifyWithUndo(PackRemoval removal, string displayName, string message)
    {
        Notification? notice = null;

        var undo = new AsyncRelayCommand(async () =>
        {
            if (await RestoreAsync(removal, displayName).ConfigureAwait(true) && notice is not null)
            {
                _notifications.Dismiss(notice);
            }
        });

        notice = _notifications.Add(
            NotificationSeverity.Information,
            displayName,
            message,
            action: undo,
            actionText: Text[nameof(Strings.Notifications_Undo)]);
    }

    /// <summary>Puts a removal back; not cancelled with the page, since the notice outlives it.</summary>
    /// <returns>True once the restore has run; false when it failed outright, and the notice keeps its Undo.</returns>
    private Task<bool> RestoreAsync(PackRemoval removal, string displayName) =>
        _runner.RunAsync(
            Text[nameof(Strings.Notifications_Undo)],
            async ct =>
            {
                try
                {
                    var result = await _installed.RestoreAsync(removal, ct).ConfigureAwait(true);

                    if (result.IsComplete)
                    {
                        _notifications.Add(
                            NotificationSeverity.Information,
                            Text[nameof(Strings.Notifications_Undo)],
                            Text.Format(nameof(Strings.Packs_Restored), displayName));
                    }
                    else
                    {
                        _notifications.Add(
                            NotificationSeverity.Warning,
                            Text[nameof(Strings.Notifications_Undo)],
                            Text.Format(
                                nameof(Strings.Packs_Restored_Partly),
                                displayName,
                                string.Join(
                                    Environment.NewLine,
                                    result.Skipped.Select(skip => $"{PathDisplay.Show(skip.Path)}: {skip.Reason}"))));
                    }
                }
                finally
                {
                    try
                    {
                        await _reloadGames(ct).ConfigureAwait(true);
                    }
                    finally
                    {
                        if (IsActive)
                        {
                            await RefreshAsync().ConfigureAwait(true);
                        }
                    }
                }
            },
            CancellationToken.None);

    /// <inheritdoc />
    protected override void OnActivated()
    {
        _followUp.PropertyChanged += OnPanelChanged;
        _followUp.QueueChanged += OnQueueChanged;
        SkippedUpdates.PropertyChanged += OnSkippedUpdatesChanged;
        Track(RefreshAsync());

        _followUp.ShowQueued();
    }

    /// <inheritdoc />
    protected override void OnDeactivated()
    {
        _followUp.PropertyChanged -= OnPanelChanged;
        _followUp.QueueChanged -= OnQueueChanged;
        SkippedUpdates.PropertyChanged -= OnSkippedUpdatesChanged;
    }

    private void OnPanelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PackInstallFollowUp.IsAnyPanelOpen))
        {
            OnPropertyChanged(nameof(IsAnyPanelOpen));

            if (!_followUp.IsAnyPanelOpen)
            {
                _followUp.ShowQueued();
            }
        }
    }

    private void OnQueueChanged(object? sender, EventArgs e)
    {
        if (_followUp.ShowQueued())
        {
            Track(RefreshAsync());
        }
    }

    private void OnSkippedUpdatesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Taking a field leaves fewer withheld: the card's button has to agree.
        if (e.PropertyName is nameof(SkippedUpdatesViewModel.IsOpen) && !SkippedUpdates.IsOpen)
        {
            Track(RefreshAsync());
        }
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(HasPacks));
        OnPropertyChanged(nameof(HasFailures));
        OnPropertyChanged(nameof(IsEmpty));
    }
}
