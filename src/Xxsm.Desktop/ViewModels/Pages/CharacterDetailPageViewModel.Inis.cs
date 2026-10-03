using CommunityToolkit.Mvvm.Input;
using Xxsm.Core;
using Xxsm.Core.Ini;
using Xxsm.Core.Io;
using Xxsm.Desktop.Services;

namespace Xxsm.Desktop.ViewModels.Pages;

/// <summary>The selected mod's INIs from Save's and Revert's menus: current toggles as defaults, and the author's back.</summary>
public sealed partial class CharacterDetailPageViewModel
{
    private ModIniChanges? _iniChanges;
    private bool _keepsSettings;
    private int _iniVersion;

    /// <summary>Whether the selected mod keeps settings between game sessions, so the game's can become its defaults.</summary>
    public bool CanUseInGameDefaults => HasSelectedRow && _keepsSettings;

    /// <summary>Whether a default of the selected mod differs from its author's.</summary>
    public bool CanRestoreDefaults => HasSelectedRow && _iniChanges?.HasDefaultChanges == true;

    /// <summary>Whether a key of the selected mod differs from its author's.</summary>
    public bool CanRestoreKeys => HasSelectedRow && _iniChanges?.HasKeyChanges == true;

    /// <summary>Whether any INI of the selected mod differs from its author's.</summary>
    public bool CanRestoreIni => HasSelectedRow && _iniChanges?.HasChanges == true;

    /// <summary>Makes the values the game saved for the selected mod's settings its defaults, with an Undo.</summary>
    [RelayCommand(CanExecute = nameof(CanUseInGameDefaults))]
    private Task UseInGameDefaultsAsync() =>
        RewriteIniAsync(
            (folder, ct) => _iniOriginals.UseInGameDefaultsAsync(folder, ct),
            nameof(Strings.CharacterDetail_Ini_InGame_Done),
            NothingInGameAsync);

    /// <summary>Puts back the selected mod's own defaults, with an Undo.</summary>
    [RelayCommand(CanExecute = nameof(CanRestoreDefaults))]
    private Task RestoreDefaultsAsync() =>
        RewriteIniAsync(
            (folder, ct) => _iniOriginals.RevertAsync(folder, IniRevertScope.Defaults, ct),
            nameof(Strings.CharacterDetail_Ini_Defaults_Done),
            nothing: null);

    /// <summary>Puts back the selected mod's own keys, with an Undo.</summary>
    [RelayCommand(CanExecute = nameof(CanRestoreKeys))]
    private Task RestoreKeysAsync() =>
        RewriteIniAsync(
            (folder, ct) => _iniOriginals.RevertAsync(folder, IniRevertScope.Keys, ct),
            nameof(Strings.CharacterDetail_Ini_Keys_Done),
            nothing: null);

    /// <summary>Puts back the selected mod's INIs exactly as their author shipped them, with an Undo.</summary>
    [RelayCommand(CanExecute = nameof(CanRestoreIni))]
    private Task RestoreIniAsync() =>
        RewriteIniAsync(
            (folder, ct) => _iniOriginals.RevertAsync(folder, IniRevertScope.Everything, ct),
            nameof(Strings.CharacterDetail_Ini_Everything_Done),
            nothing: null);

    /// <summary>Reads what differs from the author's INIs and whether the mod keeps settings, for the menus.</summary>
    private async Task LoadIniStateAsync(string? modFolder, CancellationToken cancellationToken)
    {
        // Selecting quickly starts several reads; only the last one's answer is used.
        var version = ++_iniVersion;
        ModIniChanges? changes = null;
        var keeps = false;

        if (modFolder is { Length: > 0 })
        {
            try
            {
                changes = await _iniOriginals.ReadAsync(modFolder, cancellationToken).ConfigureAwait(true);
                keeps = (await _savedSettings.ReadAsync(modFolder, cancellationToken).ConfigureAwait(true)).Settings.Count > 0;
            }
            catch (ModOperationException)
            {
                // The mod went while it was read; the rescan that follows reads the next selection.
            }
        }

        if (version != _iniVersion)
        {
            return;
        }

        _iniChanges = changes;
        _keepsSettings = keeps;
        RaiseIniState();
    }

    private void RaiseIniState()
    {
        OnPropertyChanged(nameof(CanUseInGameDefaults));
        OnPropertyChanged(nameof(CanRestoreDefaults));
        OnPropertyChanged(nameof(CanRestoreKeys));
        OnPropertyChanged(nameof(CanRestoreIni));
        UseInGameDefaultsCommand.NotifyCanExecuteChanged();
        RestoreDefaultsCommand.NotifyCanExecuteChanged();
        RestoreKeysCommand.NotifyCanExecuteChanged();
        RestoreIniCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Writes the selected mod's INIs straight away, reads them again, and says so with an Undo.</summary>
    private Task RewriteIniAsync(
        Func<string, CancellationToken, Task<IniRewrite>> rewrite,
        string doneKey,
        Func<string, CancellationToken, Task<string>>? nothing)
    {
        if (SelectedRow is not { } row)
        {
            return Task.CompletedTask;
        }

        var folder = row.Path;
        var name = row.DisplayName;

        return _runner.RunAsync(
            Heading,
            async ct =>
            {
                var done = await rewrite(folder, ct).ConfigureAwait(true);
                await ReloadIniAsync(folder, ct).ConfigureAwait(true);

                if (!done.IsEmpty)
                {
                    AnnounceIni(done, name, doneKey);
                }
                else if (nothing is not null)
                {
                    _notifications.Add(NotificationSeverity.Information, Heading, await nothing(folder, ct).ConfigureAwait(true));
                }
            },
            ActivationToken);
    }

    /// <summary>Why the game's values could not become the defaults: they already are, or the game saved none.</summary>
    private async Task<string> NothingInGameAsync(string folder, CancellationToken cancellationToken)
    {
        var read = await _savedSettings.ReadAsync(folder, cancellationToken).ConfigureAwait(true);

        return read.Settings.Any(setting => setting.InGame is not null)
            ? Text[nameof(Strings.SavedSettings_AlreadyMatch)]
            : string.Join(" ", [Text[nameof(Strings.SavedSettings_NothingSaved)], .. read.Problems]);
    }

    /// <summary>Reads the keys and the menus' state again when the mod is still the one selected.</summary>
    private async Task ReloadIniAsync(string folder, CancellationToken cancellationToken)
    {
        if (SelectedRow is not { } row || !PathComparer.AreEqual(row.Path, folder))
        {
            return;
        }

        await KeySwaps.LoadAsync(folder, sameMod: true, cancellationToken).ConfigureAwait(true);
        await LoadIniStateAsync(folder, cancellationToken).ConfigureAwait(true);
    }

    private void AnnounceIni(IniRewrite done, string name, string doneKey)
    {
        Notification? notice = null;
        var undo = new AsyncRelayCommand(async () =>
        {
            var undone = await _runner.RunAsync(
                Heading,
                async ct =>
                {
                    await _iniOriginals.UndoAsync(done, ct).ConfigureAwait(true);
                    await ReloadIniAsync(done.ModFolder, ct).ConfigureAwait(true);
                },
                CancellationToken.None).ConfigureAwait(true);

            if (undone && notice is not null)
            {
                _notifications.Dismiss(notice);
                _notifications.Add(NotificationSeverity.Information, Heading, Text.Format(nameof(Strings.CharacterDetail_Ini_Undone), name));
            }
        });

        notice = _notifications.Add(
            NotificationSeverity.Information,
            Heading,
            Text.Format(doneKey, name),
            action: undo,
            actionText: Text[nameof(Strings.Notifications_Undo)]);
    }
}
