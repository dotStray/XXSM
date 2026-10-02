using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Core.Profiles;
using Xxsm.Desktop.Services;

namespace Xxsm.Desktop.ViewModels.Pages;

/// <summary>Adding mods to profiles and taking them out from a mod's menu, and which profiles a mod is in.</summary>
public sealed partial class CharacterDetailPageViewModel
{
    private IReadOnlyList<ProfileContents> _profileContents = [];
    private IReadOnlyList<ModRowViewModel> _menuRows = [];
    private readonly Lock _profilesGate = new();
    private Task _newestProfilesRead = Task.CompletedTask;

    /// <summary>The <em>Add to profile</em> menu: every profile, greyed where it cannot take the mods.</summary>
    public ObservableCollection<ProfileMenuItemViewModel> AddToProfileItems { get; } = [];

    /// <summary>The <em>Remove from profile</em> menu: the profiles that have any of the mods.</summary>
    public ObservableCollection<ProfileMenuItemViewModel> RemoveFromProfileItems { get; } = [];

    /// <summary>Whether there is a profile to add to: none saved, no menu.</summary>
    public bool HasProfilesToAddTo => AddToProfileItems.Count > 0;

    /// <summary>Whether any profile has the mods.</summary>
    public bool HasProfilesToRemoveFrom => RemoveFromProfileItems.Count > 0;

    /// <summary>Whether the detail pane says which profiles the selected mod is in.</summary>
    public bool ShowsProfiles => _profileContents.Count > 0 && SelectedRow is not null;

    /// <summary>The profiles the selected mod is in, by name, or that it is in none.</summary>
    public string SelectedRowProfilesText => SelectedRow is not { } row
        ? string.Empty
        : string.Join(", ", ProfilesWith(row.Path).Select(contents => contents.Profile.Name)) is { Length: > 0 } names
            ? names
            : Text[nameof(Strings.CharacterDetail_Profiles_None)];

    /// <summary>Fills the profile menus for the mods a right-click is about, as the menu opens.</summary>
    /// <param name="clicked">The row right-clicked, or null for the picked rows.</param>
    public void PrepareRowMenu(ModRowViewModel? clicked)
    {
        _menuRows = TargetRows(clicked);
        FillProfileMenus();
    }

    private IEnumerable<ProfileContents> ProfilesWith(string modFolder) =>
        _profileContents.Where(contents => contents.Contains(modFolder));

    private void FillProfileMenus()
    {
        AddToProfileItems.Clear();
        RemoveFromProfileItems.Clear();
        var folders = _menuRows.Select(row => row.Path).ToList();

        if (folders.Count > 0)
        {
            foreach (var contents in _profileContents)
            {
                var profile = contents.Profile;
                var hasAll = folders.All(contents.Contains);
                var hasAny = folders.Any(contents.Contains);

                AddToProfileItems.Add(new ProfileMenuItemViewModel(
                    profile.ReadOnly ? Text.Format(nameof(Strings.CharacterDetail_Profile_ReadOnly), profile.Name)
                    : hasAll ? Text.Format(nameof(Strings.CharacterDetail_Profile_Has), profile.Name)
                    : profile.Name,
                    !profile.ReadOnly && !hasAll,
                    new AsyncRelayCommand(() => AddToProfileAsync(profile, folders))));

                if (hasAny)
                {
                    RemoveFromProfileItems.Add(new ProfileMenuItemViewModel(
                        profile.ReadOnly ? Text.Format(nameof(Strings.CharacterDetail_Profile_ReadOnly), profile.Name) : profile.Name,
                        !profile.ReadOnly,
                        new AsyncRelayCommand(() => RemoveFromProfileAsync(profile, folders))));
                }
            }
        }

        OnPropertyChanged(nameof(HasProfilesToAddTo));
        OnPropertyChanged(nameof(HasProfilesToRemoveFrom));
    }

    /// <summary>Reads which mods each profile has; only the newest read is kept.</summary>
    /// <remarks>An older read that comes back first waits for the newest, so whoever awaits either sees its
    /// result, and <see cref="ViewModelBase.WhenIdleAsync"/> is not over while the newest is still out.</remarks>
    private Task LoadProfilesAsync(CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_profilesGate)
        {
            _newestProfilesRead = done.Task;
        }

        return ReadProfilesAsync(done, cancellationToken);
    }

    private async Task ReadProfilesAsync(TaskCompletionSource done, CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<ProfileContents> read = [];

            if (_game.ModsDirectory is { Length: > 0 } modsDirectory)
            {
                try
                {
                    read = await _profiles
                        .ReadContentsAsync(modsDirectory, _game.Inventory, cancellationToken)
                        .ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Xxsm.Core.ModOperationException exception)
                {
                    // The page works without profiles; the reason is still said.
                    _notifications.Add(NotificationSeverity.Warning, Text[nameof(Strings.CharacterDetail_Profiles)], exception.Message);
                }
            }

            Task newer;

            lock (_profilesGate)
            {
                newer = _newestProfilesRead;

                if (newer == done.Task)
                {
                    _profileContents = read;
                }
            }

            if (newer == done.Task)
            {
                RaiseProfilesChanged();
            }
            else
            {
                await newer.ConfigureAwait(true);
            }
        }
        finally
        {
            done.TrySetResult();
        }
    }

    private void RaiseProfilesChanged()
    {
        OnPropertyChanged(nameof(ShowsProfiles));
        OnPropertyChanged(nameof(SelectedRowProfilesText));
    }

    private Task<bool> AddToProfileAsync(ModProfile profile, IReadOnlyList<string> folders) => _runner.RunAsync(
        Text[nameof(Strings.CharacterDetail_AddToProfile)],
        async ct =>
        {
            var modsDirectory = _game.ModsDirectory ?? throw new InvalidOperationException("There is no Mods folder.");
            var result = await _profiles.AddModsAsync(modsDirectory, profile.Id, folders, ct).ConfigureAwait(true);
            await LoadProfilesAsync(ct).ConfigureAwait(true);

            if (result.Changed.Count == 0)
            {
                _notifications.Add(
                    NotificationSeverity.Information,
                    profile.Name,
                    Text.Format(nameof(Strings.CharacterDetail_Profile_AlreadyHas), profile.Name));
                return;
            }

            Notification? notice = null;
            var undo = new AsyncRelayCommand(async () =>
            {
                if (await UndoProfileEditAsync(() => _profiles.RemoveEntriesAsync(modsDirectory, profile.Id, result.Changed, CancellationToken.None))
                        .ConfigureAwait(true) && notice is not null)
                {
                    _notifications.Dismiss(notice);
                }
            });

            notice = _notifications.Add(
                NotificationSeverity.Information,
                profile.Name,
                Text.ForCount(
                    result.Changed.Count,
                    nameof(Strings.CharacterDetail_Profile_Added_One),
                    nameof(Strings.CharacterDetail_Profile_Added),
                    Text.Mods(result.Changed.Count),
                    profile.Name),
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
        CancellationToken.None);

    private Task<bool> RemoveFromProfileAsync(ModProfile profile, IReadOnlyList<string> folders) => _runner.RunAsync(
        Text[nameof(Strings.CharacterDetail_RemoveFromProfile)],
        async ct =>
        {
            var modsDirectory = _game.ModsDirectory ?? throw new InvalidOperationException("There is no Mods folder.");
            var entries = _profileContents.FirstOrDefault(contents => contents.Profile.Id == profile.Id)?.EntriesFor(folders) ?? [];
            var result = await _profiles.RemoveEntriesAsync(modsDirectory, profile.Id, entries, ct).ConfigureAwait(true);
            await LoadProfilesAsync(ct).ConfigureAwait(true);

            if (result.Changed.Count == 0)
            {
                return;
            }

            Notification? notice = null;
            var undo = new AsyncRelayCommand(async () =>
            {
                if (await UndoProfileEditAsync(() => _profiles.AddEntriesAsync(modsDirectory, profile.Id, result.Changed, CancellationToken.None))
                        .ConfigureAwait(true) && notice is not null)
                {
                    _notifications.Dismiss(notice);
                }
            });

            notice = _notifications.Add(
                NotificationSeverity.Information,
                profile.Name,
                Text.Format(nameof(Strings.CharacterDetail_Profile_Removed), Text.Mods(result.Changed.Count), profile.Name),
                action: undo,
                actionText: Text[nameof(Strings.Notifications_Undo)]);
        },
        CancellationToken.None);

    /// <summary>Undoes a profile edit from its notice. Not cancelled with the page: the notice outlives it.</summary>
    private Task<bool> UndoProfileEditAsync(Func<Task<ProfileEditResult>> edit) => _runner.RunAsync(
        Text[nameof(Strings.Notifications_Undo)],
        async ct =>
        {
            await edit().ConfigureAwait(true);
            await LoadProfilesAsync(ct).ConfigureAwait(true);
        },
        CancellationToken.None);
}

/// <summary>One profile in a mod's <em>Add to profile</em> or <em>Remove from profile</em> menu.</summary>
/// <param name="Text">What the menu says: the profile's name, and why it is greyed if it is.</param>
/// <param name="IsEnabled">Whether it can be chosen.</param>
/// <param name="Command">Adds or removes the mods.</param>
public sealed record ProfileMenuItemViewModel(string Text, bool IsEnabled, IAsyncRelayCommand Command);
