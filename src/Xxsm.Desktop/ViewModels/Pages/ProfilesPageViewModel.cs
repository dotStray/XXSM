using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Core.Profiles;
using Xxsm.Desktop.Services;

namespace Xxsm.Desktop.ViewModels.Pages;

/// <summary>Profiles: save which mods are switched on, and switch back to a saved set later.</summary>
public sealed partial class ProfilesPageViewModel : PageViewModel, IProfileActions, IProfileModsActions
{
    private readonly GameContext _game;
    private readonly IProfileService _profiles;
    private readonly SwitchRunNotices _switchNotices;
    private readonly INotificationService _notifications;
    private readonly ViewModelWorkRunner _work;
    private readonly Func<CancellationToken, Task> _rescan;
    private readonly Func<string, bool> _goToMod;

    private int _reloadVersion;

    /// <summary>Creates the page.</summary>
    /// <param name="game">The shell's shared view of the selected game.</param>
    /// <param name="profiles">Saves, lists and applies profiles.</param>
    /// <param name="switchNotices">Says what applying did, with an Undo.</param>
    /// <param name="notifications">Where saving and deleting are reported.</param>
    /// <param name="work">Turns a failure into a notice rather than a crash.</param>
    /// <param name="text">The interface's wording.</param>
    /// <param name="rescan">Rescans the Mods folder after applying, so every page shows it.</param>
    /// <param name="thumbnails">Each mod's picture, small, for the tiles of a profile's mods and the picker.</param>
    /// <param name="goToMod">Opens a mod on its character's page; false when it is not there.</param>
    public ProfilesPageViewModel(
        GameContext game,
        IProfileService profiles,
        SwitchRunNotices switchNotices,
        INotificationService notifications,
        ViewModelWorkRunner work,
        ITextCatalogue text,
        Func<CancellationToken, Task> rescan,
        IModThumbnailCache thumbnails,
        Func<string, bool> goToMod)
        : base(text)
    {
        ArgumentNullException.ThrowIfNull(goToMod);
        _goToMod = goToMod;
        Mods = new ProfileModsViewModel(text, thumbnails, this);
        Mods.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProfileModsViewModel.IsOpen))
            {
                OnPropertyChanged(nameof(IsAnyPanelOpen));
            }
        };

        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(switchNotices);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(rescan);

        _game = game;
        _profiles = profiles;
        _switchNotices = switchNotices;
        _notifications = notifications;
        _work = work;
        _rescan = rescan;

        Apply = new ProfileApplyViewModel(text, ApplyPlanAsync);
        Apply.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProfileApplyViewModel.IsOpen))
            {
                OnPropertyChanged(nameof(IsAnyPanelOpen));
            }
        };
    }

    /// <inheritdoc />
    public override string Heading => Text[nameof(Strings.Profiles_Heading)];

    /// <summary>Whether the selected game has a Mods folder to keep profiles for.</summary>
    public bool HasFolder => _game.HasModsDirectory;

    /// <summary>The saved profiles, in name order.</summary>
    public ObservableCollection<ProfileRowViewModel> Profiles { get; } = [];

    /// <summary>Whether there are any.</summary>
    public bool HasProfiles => Profiles.Count > 0;

    /// <summary>Whether to say there are none yet.</summary>
    public bool ShowEmpty => HasFolder && !HasProfiles;

    /// <summary>Profile files that could not be read, each as a sentence.</summary>
    public ObservableCollection<string> Problems { get; } = [];

    /// <summary>Whether any could not be read.</summary>
    public bool HasProblems => Problems.Count > 0;

    /// <summary>What the next profile will be called.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveNewCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveEmptyCommand))]
    private string _newName = string.Empty;

    /// <summary>"Apply Evening?"</summary>
    public ProfileApplyViewModel Apply { get; }

    /// <summary>One profile's mods, with a way to take each off.</summary>
    public ProfileModsViewModel Mods { get; }

    /// <summary>Whether a panel is over the page, so the scrim is drawn.</summary>
    public bool IsAnyPanelOpen => Apply.IsOpen || Mods.IsOpen;

    /// <summary>Saves which mods are switched on now as a new profile.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveNew))]
    private Task SaveNewAsync() => SaveNewAsync(empty: false);

    /// <summary>Saves a new profile with no mods in it: applying it switches every mod off.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveNew))]
    private Task SaveEmptyAsync() => SaveNewAsync(empty: true);

    private Task SaveNewAsync(bool empty)
    {
        if (_game.ModsDirectory is not { Length: > 0 } mods)
        {
            return Task.CompletedTask;
        }

        var name = NewName;

        return _work.RunAsync(
            Heading,
            async ct =>
            {
                var saved = empty
                    ? await _profiles.SaveEmptyAsync(mods, name, ct).ConfigureAwait(true)
                    : await _profiles.SaveAsync(mods, name, ct).ConfigureAwait(true);

                NewName = string.Empty;
                ReportSaved(saved);
                await ReloadAsync(ct).ConfigureAwait(true);
            },
            ActivationToken);
    }

    private bool CanSaveNew() => HasFolder && !string.IsNullOrWhiteSpace(NewName);

    /// <summary>Reads the profiles folder again; only the last read asked for fills the list.</summary>
    internal async Task ReloadAsync(CancellationToken cancellationToken)
    {
        var version = ++_reloadVersion;
        ProfileList? list = null;

        if (_game.ModsDirectory is { Length: > 0 } mods)
        {
            list = await _profiles.ListAsync(mods, cancellationToken).ConfigureAwait(true);
        }

        if (version != _reloadVersion)
        {
            return;
        }

        Profiles.Clear();
        Problems.Clear();

        if (list is not null)
        {
            foreach (var profile in list.Profiles)
            {
                Profiles.Add(new ProfileRowViewModel(profile, this, Text));
            }

            foreach (var problem in list.Problems)
            {
                Problems.Add(problem.Message);
            }
        }

        OnPropertyChanged(nameof(HasFolder));
        OnPropertyChanged(nameof(HasProfiles));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(HasProblems));
        SaveNewCommand.NotifyCanExecuteChanged();
        SaveEmptyCommand.NotifyCanExecuteChanged();
    }

    /// <inheritdoc />
    Task IProfileActions.ApplyAsync(ProfileRowViewModel row) =>
        _game.ModsDirectory is not { Length: > 0 } mods
            ? Task.CompletedTask
            : _work.RunAsync(
                Heading,
                async ct => Apply.Open(await _profiles.PlanApplyAsync(mods, row.Profile.Id, ct).ConfigureAwait(true)),
                ActivationToken);

    /// <inheritdoc />
    Task IProfileActions.AskToReplaceAsync(ProfileRowViewModel row) =>
        _game.Inventory is not { } inventory
            ? Task.CompletedTask
            : AskToReplace(row, inventory.EnabledModCount);

    private Task AskToReplace(ProfileRowViewModel row, int enabledNow)
    {
        row.AskToReplace(Text.Format(nameof(Strings.Profiles_Replace_Question), row.Name, Text.Mods(enabledNow)));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    Task IProfileActions.ReplaceAsync(ProfileRowViewModel row) => Change(async (mods, ct) =>
        ReportSaved(await _profiles.UpdateAsync(mods, row.Profile.Id, ct).ConfigureAwait(true)));

    /// <inheritdoc />
    Task IProfileActions.RenameAsync(ProfileRowViewModel row, string name) => Change(async (mods, ct) =>
        await _profiles.RenameAsync(mods, row.Profile.Id, name, ct).ConfigureAwait(true));

    /// <inheritdoc />
    Task IProfileActions.SetReadOnlyAsync(ProfileRowViewModel row, bool isReadOnly) => Change(async (mods, ct) =>
        await _profiles.SetReadOnlyAsync(mods, row.Profile.Id, isReadOnly, ct).ConfigureAwait(true));

    /// <inheritdoc />
    Task IProfileActions.DeleteAsync(ProfileRowViewModel row) => Change(async (mods, ct) =>
    {
        var trashed = await _profiles.DeleteAsync(mods, row.Profile.Id, ct).ConfigureAwait(true);
        Notification? notice = null;

        var undo = new AsyncRelayCommand(async () =>
        {
            var ok = await _work.RunAsync(
                Text[nameof(Strings.Notifications_Undo)],
                async undoCt =>
                {
                    await _profiles.RestoreAsync(trashed, undoCt).ConfigureAwait(true);
                    await ReloadAsync(undoCt).ConfigureAwait(true);
                    _notifications.Add(
                        NotificationSeverity.Information,
                        Heading,
                        Text.Format(nameof(Strings.Profiles_Restored), row.Name));
                },
                CancellationToken.None).ConfigureAwait(true);

            if (ok && notice is not null)
            {
                _notifications.Dismiss(notice);
            }
        });

        notice = _notifications.Add(
            NotificationSeverity.Information,
            Heading,
            Text.Format(nameof(Strings.Profiles_Deleted), row.Name),
            action: undo,
            actionText: Text[nameof(Strings.Notifications_Undo)]);
    });

    /// <inheritdoc />
    Task IProfileActions.ShowModsAsync(ProfileRowViewModel row) =>
        _game.ModsDirectory is not { Length: > 0 }
            ? Task.CompletedTask
            : _work.RunAsync(Heading, ct => ShowModsAsync(row.Profile.Id, ct), ActivationToken);

    /// <summary>Opens a profile's mods, or shows them again; closes the panel if the profile has gone.</summary>
    private async Task ShowModsAsync(string profileId, CancellationToken cancellationToken)
    {
        if (_game.ModsDirectory is not { Length: > 0 } mods)
        {
            return;
        }

        var all = await _profiles.ReadContentsAsync(mods, _game.Inventory, cancellationToken).ConfigureAwait(true);

        if (all.FirstOrDefault(contents => contents.Profile.Id == profileId) is not { } found)
        {
            Mods.Close();
            return;
        }

        Track(Mods.Show(found));
    }

    /// <inheritdoc />
    IReadOnlyList<InstalledMod> IProfileModsActions.AllMods() => _game.Inventory?.AllMods.ToList() ?? [];

    /// <inheritdoc />
    void IProfileModsActions.GoTo(string modFolder) => GoToMod(modFolder);

    /// <inheritdoc />
    Task IProfileModsActions.AddAsync(IReadOnlyList<string> modFolders) => EditShownAsync(
        (mods, profile, ct) => _profiles.AddModsAsync(mods, profile.Id, modFolders, ct),
        (mods, profile, result) => _profiles.RemoveEntriesAsync(mods, profile.Id, result.Changed, CancellationToken.None),
        (profile, result) => Text.ForCount(
            result.Changed.Count,
            nameof(Strings.CharacterDetail_Profile_Added_One),
            nameof(Strings.CharacterDetail_Profile_Added),
            Text.Mods(result.Changed.Count),
            profile.Name));

    /// <inheritdoc />
    Task IProfileModsActions.ReplaceAsync(ProfileMember member, string modFolder) => EditShownAsync(
        (mods, profile, ct) => _profiles.ReplaceEntryAsync(mods, profile.Id, member.Entry, modFolder, ct),
        (mods, profile, result) => _profiles.SwapEntryAsync(mods, profile.Id, result.Changed[0], member.Entry, CancellationToken.None),
        (profile, result) => Text.Format(
            nameof(Strings.ProfileMods_Replaced), member.Entry.Name ?? PathDisplay.Show(member.Entry.Path), result.Changed[0].Name ?? PathDisplay.Show(result.Changed[0].Path), profile.Name));

    /// <inheritdoc />
    Task IProfileModsActions.RemoveAsync(ProfileMember member) => EditShownAsync(
        (mods, profile, ct) => _profiles.RemoveEntriesAsync(mods, profile.Id, [member.Entry], ct),
        (mods, profile, result) => _profiles.AddEntriesAsync(mods, profile.Id, result.Changed, CancellationToken.None),
        (profile, result) => Text.Format(nameof(Strings.CharacterDetail_Profile_Removed), Text.Mods(result.Changed.Count), profile.Name));

    /// <summary>Changes the shown profile, shows it again, and says so with an Undo that reverses it.</summary>
    private Task EditShownAsync(
        Func<string, ModProfile, CancellationToken, Task<ProfileEditResult>> edit,
        Func<string, ModProfile, ProfileEditResult, Task<ProfileEditResult>> undoEdit,
        Func<ModProfile, ProfileEditResult, string> message)
    {
        if (_game.ModsDirectory is not { Length: > 0 } mods || Mods.Profile is not { } profile)
        {
            return Task.CompletedTask;
        }

        return _work.RunAsync(
            Heading,
            async ct =>
            {
                ProfileEditResult result;

                try
                {
                    result = await edit(mods, profile, ct).ConfigureAwait(true);
                }
                finally
                {
                    await ShowModsAsync(profile.Id, ct).ConfigureAwait(true);
                    await ReloadAsync(ct).ConfigureAwait(true);
                }

                if (result.Changed.Count == 0)
                {
                    return;
                }

                Notification? notice = null;
                var undo = new AsyncRelayCommand(async () =>
                {
                    var ok = await _work.RunAsync(
                        Text[nameof(Strings.Notifications_Undo)],
                        async undoCt =>
                        {
                            await undoEdit(mods, profile, result).ConfigureAwait(true);
                            await ReloadAsync(undoCt).ConfigureAwait(true);

                            if (Mods.Profile?.Id == profile.Id)
                            {
                                await ShowModsAsync(profile.Id, undoCt).ConfigureAwait(true);
                            }
                        },
                        CancellationToken.None).ConfigureAwait(true);

                    if (ok && notice is not null)
                    {
                        _notifications.Dismiss(notice);
                    }
                });

                notice = _notifications.Add(
                    NotificationSeverity.Information,
                    profile.Name,
                    message(profile, result),
                    action: undo,
                    actionText: Text[nameof(Strings.Notifications_Undo)]);

                if (result.RecordedByFolder.Count > 0)
                {
                    _notifications.Add(
                        NotificationSeverity.Warning,
                        profile.Name,
                        Text.ForCount(result.RecordedByFolder.Count, nameof(Strings.Profiles_Saved_ByFolder_One), nameof(Strings.Profiles_Saved_ByFolder), Text.Mods(result.RecordedByFolder.Count)));
                }
            },
            ActivationToken);
    }

    /// <inheritdoc />
    string? IProfileModsActions.CharacterOf(string? folder) => CharacterOf(folder);

    /// <summary>The character a folder in the Mods folder is for, by its display name.</summary>
    private string? CharacterOf(string? folder) =>
        folder is null || _game.Data is not { } data
            ? null
            : data.VisibleVariants.FirstOrDefault(variant => PathComparer.AreEqual(variant.ModFilesName, folder))?.DisplayName;

    /// <summary>Opens a mod on its character's page, or says why it cannot.</summary>
    private void GoToMod(string modFolder)
    {
        if (!_goToMod(modFolder))
        {
            _notifications.Add(NotificationSeverity.Warning, Heading, Text[nameof(Strings.Mods_Updates_Gone)]);
        }
    }

    /// <summary>Makes one change to the profiles, reports a refusal as a notice, and reads the list again.</summary>
    private Task Change(Func<string, CancellationToken, Task> change)
    {
        if (_game.ModsDirectory is not { Length: > 0 } mods)
        {
            return Task.CompletedTask;
        }

        return _work.RunAsync(
            Heading,
            async ct =>
            {
                try
                {
                    await change(mods, ct).ConfigureAwait(true);
                }
                finally
                {
                    await ReloadAsync(ct).ConfigureAwait(true);
                }
            },
            ActivationToken);
    }

    private async Task ApplyPlanAsync(ProfileApplyPlan plan)
    {
        await _work.RunAsync(
            Heading,
            async ct =>
            {
                var result = await _profiles.ApplyAsync(plan, ct).ConfigureAwait(true);

                await _rescan(ct).ConfigureAwait(true);
                _switchNotices.Report(
                    plan.ModsDirectory, result, Text.Format(nameof(Strings.Profiles_Applied), plan.Profile.Name));
            },
            CancellationToken.None).ConfigureAwait(true);
    }

    private void ReportSaved(ProfileSaveResult saved)
    {
        var message = Text.Format(
            nameof(Strings.Profiles_Saved), Text.Mods(saved.Profile.Enabled.Count), saved.Profile.Name);

        if (saved.RecordedByFolder.Count > 0)
        {
            message += " " + Text.ForCount(saved.RecordedByFolder.Count, nameof(Strings.Profiles_Saved_ByFolder_One), nameof(Strings.Profiles_Saved_ByFolder), Text.Mods(saved.RecordedByFolder.Count));
        }

        _notifications.Add(
            saved.RecordedByFolder.Count > 0 ? NotificationSeverity.Warning : NotificationSeverity.Information,
            Heading,
            message);
    }

    /// <inheritdoc />
    protected override void OnActivated()
    {
        _game.PropertyChanged += OnGameChanged;
        Track(ReloadAsync(ActivationToken));
    }

    /// <inheritdoc />
    protected override void OnDeactivated()
    {
        _game.PropertyChanged -= OnGameChanged;
        Apply.Close();
        Mods.Close();
    }

    private void OnGameChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameContext.ModsDirectory))
        {
            Apply.Close();
            Mods.Close();
            Track(ReloadAsync(ActivationToken));
        }
        else if (e.PropertyName == nameof(GameContext.Inventory) && Mods is { IsOpen: true, Profile: { } shown })
        {
            // A mod renamed, moved or switched while its profile is open: the panel says so.
            Track(_work.RunAsync(Heading, ct => ShowModsAsync(shown.Id, ct), ActivationToken));
        }
    }
}
