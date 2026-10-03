using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Core;
using Xxsm.Core.Ini;
using Xxsm.Core.Io;
using Xxsm.Desktop.Services;

namespace Xxsm.Desktop.ViewModels.Pages;

/// <summary>One line of a mod's INI that Save will change.</summary>
/// <param name="Name">Which line: <c>$hat</c>, or <c>[KeyHat] key</c>; with its INI when the mod has several.</param>
/// <param name="From">Its value now.</param>
/// <param name="To">The value Save writes.</param>
public sealed record PendingIniLine(string Name, string From, string To);

/// <summary>
/// The selected mod's INIs from Save's and Revert's menus: current toggles as defaults, and the author's back. Each
/// lists what would change; the pane's Save writes it, with an Undo, and Revert drops it.
/// </summary>
public sealed partial class CharacterDetailPageViewModel
{
    private ModIniChanges? _iniChanges;
    private bool _keepsSettings;
    private int _iniVersion;
    private PendingIni? _pendingIni;

    /// <summary>Whether the selected mod keeps settings between game sessions, so the game's can become its defaults.</summary>
    public bool CanUseInGameDefaults => HasSelectedRow && _keepsSettings;

    /// <summary>Whether a default of the selected mod differs from its author's.</summary>
    public bool CanRestoreDefaults => HasSelectedRow && _iniChanges?.HasDefaultChanges == true;

    /// <summary>Whether a key of the selected mod differs from its author's.</summary>
    public bool CanRestoreKeys => HasSelectedRow && _iniChanges?.HasKeyChanges == true;

    /// <summary>Whether any INI of the selected mod differs from its author's.</summary>
    public bool CanRestoreIni => HasSelectedRow && _iniChanges?.HasChanges == true;

    /// <summary>The lines Save will change in the mod's INI, from one of the two menus; empty when none is waiting.</summary>
    public ObservableCollection<PendingIniLine> PendingIniLines { get; } = [];

    /// <summary>Over <see cref="PendingIniLines"/>: what Save will do with them; null when nothing is waiting.</summary>
    public string? PendingIniText => _pendingIni is { } pending ? Text[pending.HeadingKey] : null;

    /// <summary>Under the lines: INIs with other differences that a whole-file revert puts back too, or that the file
    /// changed since the list was made; null when there is nothing to add.</summary>
    public string? PendingIniNote => _pendingIni is { } pending
        ? (pending.ChangedSince, pending.Note) switch
        {
            (true, { } note) => Text[nameof(Strings.CharacterDetail_IniPending_Changed)] + " " + note,
            (true, null) => Text[nameof(Strings.CharacterDetail_IniPending_Changed)],
            (false, var note) => note,
        }
        : null;

    /// <summary>Whether a menu's change is waiting for Save.</summary>
    public bool HasPendingIni => _pendingIni is not null;

    /// <summary>Lists the defaults the values the game saved would replace; Save writes them.</summary>
    [RelayCommand(CanExecute = nameof(CanUseInGameDefaults))]
    private Task UseInGameDefaultsAsync() => StageIniAsync(IniOperation.InGame);

    /// <summary>Lists the defaults the mod came with that would come back; Save writes them.</summary>
    [RelayCommand(CanExecute = nameof(CanRestoreDefaults))]
    private Task RestoreDefaultsAsync() => StageIniAsync(IniOperation.Defaults);

    /// <summary>Lists the keys the mod came with that would come back; Save writes them.</summary>
    [RelayCommand(CanExecute = nameof(CanRestoreKeys))]
    private Task RestoreKeysAsync() => StageIniAsync(IniOperation.Keys);

    /// <summary>Lists what putting back the author's whole INI would change; Save writes it.</summary>
    [RelayCommand(CanExecute = nameof(CanRestoreIni))]
    private Task RestoreIniAsync() => StageIniAsync(IniOperation.Everything);

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

    /// <summary>Works out what an operation would change and shows it over Save; nothing is written.</summary>
    private Task StageIniAsync(IniOperation operation)
    {
        if (SelectedRow is not { } row)
        {
            return Task.CompletedTask;
        }

        var folder = row.Path;

        return _runner.RunAsync(
            Heading,
            async ct =>
            {
                var (lines, note) = await PreviewIniAsync(folder, operation, ct).ConfigureAwait(true);

                if (SelectedRow is not { } still || !PathComparer.AreEqual(still.Path, folder))
                {
                    return;
                }

                if (lines.Count == 0 && note is null)
                {
                    ShowPendingIni(null, []);

                    if (operation == IniOperation.InGame)
                    {
                        _notifications.Add(NotificationSeverity.Information, Heading, await NothingInGameAsync(folder, ct).ConfigureAwait(true));
                    }

                    return;
                }

                // The keys typed into the pane would be overwritten by the author's: they go, as Revert would.
                if (operation is IniOperation.Keys or IniOperation.Everything)
                {
                    KeySwaps.Revert();
                }

                ShowPendingIni(new PendingIni(operation, lines, note, ChangedSince: false), lines);
            },
            ActivationToken);
    }

    /// <summary>Each line an operation would change, and anything else it would do, read from disk now.</summary>
    private async Task<(List<PendingIniLine> Lines, string? Note)> PreviewIniAsync(
        string folder, IniOperation operation, CancellationToken cancellationToken)
    {
        if (operation == IniOperation.InGame)
        {
            var read = await _savedSettings.ReadAsync(folder, cancellationToken).ConfigureAwait(true);
            var changing = read.Settings.Where(setting => setting.DiffersFromGame).ToList();
            var several = changing.Select(setting => setting.File).Distinct(StringComparer.Ordinal).Count() > 1;

            return ([.. changing.Select(setting => new PendingIniLine(
                several ? $"{setting.File} {setting.Name}" : setting.Name, setting.EffectiveDefault, setting.InGame!))], null);
        }

        var changes = await _iniOriginals.ReadAsync(folder, cancellationToken).ConfigureAwait(true);
        var severalFiles = changes.Files.Count > 1;
        var lines = new List<PendingIniLine>();

        foreach (var file in changes.Files)
        {
            foreach (var change in file.Changes.Where(change => operation switch
                     {
                         IniOperation.Defaults => change.Kind == IniChangeKind.Default,
                         IniOperation.Keys => change.Kind == IniChangeKind.Key,
                         _ => true,
                     }))
            {
                var name = change.Kind == IniChangeKind.Default ? change.Name : $"[{change.Section}] {change.Name}";
                lines.Add(new PendingIniLine(severalFiles ? $"{file.File} {name}" : name, change.Current, change.Original));
            }
        }

        var other = operation == IniOperation.Everything
            ? changes.Files.Where(file => file.HasOtherChanges).Select(file => file.File).ToList()
            : [];

        return (lines, other.Count > 0
            ? Text.Format(nameof(Strings.CharacterDetail_IniPending_Other), string.Join(", ", other))
            : null);
    }

    private void ShowPendingIni(PendingIni? pending, IReadOnlyList<PendingIniLine> lines)
    {
        _pendingIni = pending;
        PendingIniLines.Clear();

        foreach (var line in lines)
        {
            PendingIniLines.Add(line);
        }

        OnPropertyChanged(nameof(PendingIniText));
        OnPropertyChanged(nameof(PendingIniNote));
        OnPropertyChanged(nameof(HasPendingIni));
        OnPropertyChanged(nameof(IsEditDirty));
        SaveEditsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Drops the waiting change; the mod's INI is left as it is.</summary>
    private void DropPendingIni() => ShowPendingIni(null, []);

    /// <summary>Writes the waiting change, if it is still what was shown, and says so with an Undo.</summary>
    /// <param name="folder">The mod's folder now; switching it on or off renames it while the list waits.</param>
    /// <param name="name">The mod's name, for the notice.</param>
    /// <param name="cancellationToken">Cancels before anything is written.</param>
    /// <returns>False when the INI changed since the list was made: the new list is shown and nothing is written.</returns>
    private async Task<bool> SavePendingIniAsync(string folder, string name, CancellationToken cancellationToken)
    {
        if (_pendingIni is not { } pending)
        {
            return true;
        }

        var (lines, note) = await PreviewIniAsync(folder, pending.Operation, cancellationToken).ConfigureAwait(true);

        if (!lines.SequenceEqual(pending.Lines) || note != pending.Note)
        {
            ShowPendingIni(lines.Count == 0 && note is null ? null : new PendingIni(pending.Operation, lines, note, ChangedSince: true), lines);
            return false;
        }

        var done = pending.Operation switch
        {
            IniOperation.InGame => await _iniOriginals.UseInGameDefaultsAsync(folder, cancellationToken).ConfigureAwait(true),
            IniOperation.Defaults => await _iniOriginals.RevertAsync(folder, IniRevertScope.Defaults, cancellationToken).ConfigureAwait(true),
            IniOperation.Keys => await _iniOriginals.RevertAsync(folder, IniRevertScope.Keys, cancellationToken).ConfigureAwait(true),
            _ => await _iniOriginals.RevertAsync(folder, IniRevertScope.Everything, cancellationToken).ConfigureAwait(true),
        };

        DropPendingIni();

        if (!done.IsEmpty)
        {
            AnnounceIni(done, name, pending.DoneKey);
        }

        return true;
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

    /// <summary>What one of the menus' items does to the mod's INI.</summary>
    private enum IniOperation
    {
        InGame,
        Defaults,
        Keys,
        Everything,
    }

    /// <summary>A menu item's change, listed and waiting for Save.</summary>
    private sealed record PendingIni(IniOperation Operation, IReadOnlyList<PendingIniLine> Lines, string? Note, bool ChangedSince)
    {
        public string HeadingKey => Operation switch
        {
            IniOperation.InGame => nameof(Strings.CharacterDetail_IniPending_InGame),
            IniOperation.Defaults => nameof(Strings.CharacterDetail_IniPending_Defaults),
            IniOperation.Keys => nameof(Strings.CharacterDetail_IniPending_Keys),
            _ => nameof(Strings.CharacterDetail_IniPending_Ini),
        };

        public string DoneKey => Operation switch
        {
            IniOperation.InGame => nameof(Strings.CharacterDetail_Ini_InGame_Done),
            IniOperation.Defaults => nameof(Strings.CharacterDetail_Ini_Defaults_Done),
            IniOperation.Keys => nameof(Strings.CharacterDetail_Ini_Keys_Done),
            _ => nameof(Strings.CharacterDetail_Ini_Everything_Done),
        };
    }
}
