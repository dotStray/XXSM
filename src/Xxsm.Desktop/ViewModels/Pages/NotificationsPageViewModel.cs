using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Core;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Desktop.Services;

namespace Xxsm.Desktop.ViewModels.Pages;

/// <summary>The notices page: what the last scan found in the Mods folder, and what XXSM did this session.</summary>
/// <remarks>Findings come back every scan until fixed, so they have no Dismiss; opening the page reads
/// notices.</remarks>
public sealed partial class NotificationsPageViewModel : PageViewModel, IRefreshablePage
{
    private readonly INotificationService _notifications;
    private readonly GameContext _game;
    private readonly IModFileOperations _files;
    private readonly IModsFolderLeftovers _leftovers;
    private readonly IFolderLauncher _launcher;
    private readonly ViewModelWorkRunner _runner;
    private readonly Func<CancellationToken, Task> _rescan;
    private readonly Action _showMods;

    /// <summary>Creates the page.</summary>
    /// <param name="notifications">The session's notices.</param>
    /// <param name="game">The selected game, and what the last scan of its Mods folder found.</param>
    /// <param name="files">Deletes and renames folders.</param>
    /// <param name="leftovers">Tidies what an interrupted update or move left behind.</param>
    /// <param name="launcher">Opens a folder in the file manager.</param>
    /// <param name="runner">Turns a failure into a notice rather than a crash.</param>
    /// <param name="text">The interface's wording.</param>
    /// <param name="rescan">Rescans the Mods folder after something in it changed.</param>
    /// <param name="showMods">Sends the user to the Mods page.</param>
    public NotificationsPageViewModel(
        INotificationService notifications,
        GameContext game,
        IModFileOperations files,
        IModsFolderLeftovers leftovers,
        IFolderLauncher launcher,
        ViewModelWorkRunner runner,
        ITextCatalogue text,
        Func<CancellationToken, Task> rescan,
        Action showMods)
        : base(text)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(leftovers);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(rescan);
        ArgumentNullException.ThrowIfNull(showMods);

        _notifications = notifications;
        _game = game;
        _files = files;
        _leftovers = leftovers;
        _launcher = launcher;
        _runner = runner;
        _rescan = rescan;
        _showMods = showMods;

        EmptyFolders = new EmptyFoldersReviewViewModel(text, DeleteFoldersAsync);
        Rename = new FolderRenameViewModel(files, notifications, runner, text, rescan);
        EmptyFolders.PropertyChanged += OnPanelChanged;
        Rename.PropertyChanged += OnPanelChanged;
    }

    /// <inheritdoc />
    public override string Heading => Text[nameof(Strings.Notifications_Heading)];

    /// <summary>Everything raised this session, newest first.</summary>
    public ReadOnlyObservableCollection<Notification> Notifications => _notifications.Notifications;

    /// <summary>Whether there is any activity to show.</summary>
    public bool HasNotifications => Notifications.Count > 0;

    /// <summary>What the last scan found in the Mods folder, each with what it offers to do.</summary>
    public ObservableCollection<ModsFolderFindingViewModel> Findings { get; } = [];

    /// <summary>Whether the scan found anything.</summary>
    public bool HasFindings => Findings.Count > 0;

    /// <summary>The window that deletes several character folders with no mods.</summary>
    public EmptyFoldersReviewViewModel EmptyFolders { get; }

    /// <summary>The window that renames a folder that differs from another only by capitals.</summary>
    public FolderRenameViewModel Rename { get; }

    /// <summary>Whether either window is open, so the page behind it is dimmed.</summary>
    public bool IsAnyPanelOpen => EmptyFolders.IsOpen || Rename.IsOpen;

    /// <summary>Rescans the Mods folder, so what was fixed outside XXSM goes and anything new appears.</summary>
    [RelayCommand]
    private async Task RefreshAsync() =>
        await _runner.RunAsync(Heading, _rescan, ActivationToken).ConfigureAwait(true);

    /// <summary>Removes one notice.</summary>
    [RelayCommand]
    public void Dismiss(Notification? notification)
    {
        if (notification is not null)
        {
            _notifications.Dismiss(notification);
            OnPropertyChanged(nameof(HasNotifications));
        }
    }

    /// <summary>Removes every notice. What the scan found stays: it is not activity.</summary>
    [RelayCommand]
    public void ClearAll()
    {
        _notifications.Clear();
        OnPropertyChanged(nameof(HasNotifications));
    }

    /// <inheritdoc />
    protected override void OnActivated()
    {
        _notifications.MarkAllRead();
        ((INotifyCollectionChanged)_notifications.Notifications).CollectionChanged += OnNotificationsChanged;
        _game.PropertyChanged += OnGameChanged;

        RebuildFindings();
        OnPropertyChanged(nameof(HasNotifications));
    }

    /// <inheritdoc />
    protected override void OnDeactivated()
    {
        ((INotifyCollectionChanged)_notifications.Notifications).CollectionChanged -= OnNotificationsChanged;
        _game.PropertyChanged -= OnGameChanged;

        EmptyFolders.Close();
        Rename.Close();
    }

    private void RebuildFindings()
    {
        Findings.Clear();

        if (_game.Inventory is { } inventory)
        {
            foreach (var diagnostic in inventory.Diagnostics)
            {
                switch (diagnostic.Code)
                {
                    // Gathered below, so ten of them are one finding.
                    case ModDiagnosticCodes.EmptyVariantFolder or ModDiagnosticCodes.UnfiledMod
                        or ModDiagnosticCodes.LeftoverSpareCopy or ModDiagnosticCodes.LeftoverOnlyCopy:
                        break;

                    case ModDiagnosticCodes.TrashInsideMods when diagnostic.Paths is [var trash, ..]:
                        Findings.Add(Finding(
                            diagnostic.Code,
                            Text[nameof(Strings.Findings_TrashInside_Title)],
                            diagnostic.Message,
                            Text[nameof(Strings.Findings_MoveTrashOut)],
                            () => MoveTrashOutAsync(trash)));
                        break;

                    case ModDiagnosticCodes.CaseCollision:
                        var paths = diagnostic.Paths;
                        Findings.Add(Finding(
                            diagnostic.Code,
                            Text[nameof(Strings.Findings_CaseCollision_Title)],
                            diagnostic.Message,
                            Text[nameof(Strings.Findings_Rename)],
                            () => OpenRename(paths)));
                        break;

                    default:
                        Findings.Add(OpenFolderFinding(diagnostic));
                        break;
                }
            }

            AddEmptyFolders(inventory);
            AddLeftovers(inventory);
            AddUnfiledMods(inventory);
        }

        OnPropertyChanged(nameof(HasFindings));
    }

    private void AddEmptyFolders(ModsInventory inventory)
    {
        var empty = inventory.VariantFolders.Where(folder => folder.Mods.Count == 0).ToList();

        if (empty is [var only])
        {
            Findings.Add(Finding(
                ModDiagnosticCodes.EmptyVariantFolder,
                Text[nameof(Strings.Findings_Empty_Title)],
                only.OtherEntryCount == 0
                    ? Text.Format(nameof(Strings.Findings_EmptyOne), only.Name)
                    : Text.Format(nameof(Strings.Findings_EmptyOne_WithItems), only.Name, Text.Items(only.OtherEntryCount)),
                Text[nameof(Strings.Findings_Delete)],
                () => DeleteFoldersAsync([only]),
                destructive: true));
        }
        else if (empty.Count > 1)
        {
            Findings.Add(Finding(
                ModDiagnosticCodes.EmptyVariantFolder,
                Text[nameof(Strings.Findings_EmptyMany_Title)],
                Text.Format(nameof(Strings.Findings_EmptyMany), Text.Folders(empty.Count)),
                Text[nameof(Strings.Findings_Review)],
                () => EmptyFolders.Open(empty)));
        }
    }

    /// <summary>What interrupted updates and moves left: spares to trash, mods to put back; never at start.</summary>
    private void AddLeftovers(ModsInventory inventory)
    {
        var spare = inventory.Diagnostics.Where(d => d.Code == ModDiagnosticCodes.LeftoverSpareCopy && d.Paths.Count > 0).ToList();
        var only = inventory.Diagnostics.Where(d => d.Code == ModDiagnosticCodes.LeftoverOnlyCopy && d.Paths.Count > 0).ToList();

        if (spare.Count > 0)
        {
            var paths = spare.Select(d => d.Paths[0]).ToList();
            Findings.Add(Finding(
                ModDiagnosticCodes.LeftoverSpareCopy,
                Text[spare.Count == 1 ? nameof(Strings.Findings_LeftoverSpare_Title) : nameof(Strings.Findings_LeftoverSpareMany_Title)],
                spare is [var one] ? one.Message : Text.Format(nameof(Strings.Findings_LeftoverSpareMany), Text.Folders(spare.Count)),
                Text[nameof(Strings.Findings_MoveToTrash)],
                () => TrashLeftoversAsync(paths),
                destructive: true));
        }

        if (only.Count > 0)
        {
            var paths = only.Select(d => d.Paths[0]).ToList();
            Findings.Add(Finding(
                ModDiagnosticCodes.LeftoverOnlyCopy,
                Text[only.Count == 1 ? nameof(Strings.Findings_LeftoverOnly_Title) : nameof(Strings.Findings_LeftoverOnlyMany_Title)],
                only is [var one] ? one.Message : Text.Format(nameof(Strings.Findings_LeftoverOnlyMany), Text.Mods(only.Count)),
                Text[nameof(Strings.Findings_PutBack)],
                () => PutLeftoversBackAsync(paths)));
        }
    }

    private void AddUnfiledMods(ModsInventory inventory)
    {
        if (inventory.UnfiledMods.Count == 0)
        {
            return;
        }

        Findings.Add(Finding(
            ModDiagnosticCodes.UnfiledMod,
            Text[nameof(Strings.Findings_Unfiled_Title)],
            inventory.UnfiledMods is [var only]
                ? Text.Format(nameof(Strings.Findings_UnfiledOne), only.DisplayName)
                : Text.Format(nameof(Strings.Findings_UnfiledMany), Text.Mods(inventory.UnfiledMods.Count)),
            Text[nameof(Strings.Findings_GoToMods)],
            _showMods));
    }

    private ModsFolderFindingViewModel OpenFolderFinding(Xxsm.Core.Diagnostics.Diagnostic diagnostic)
    {
        var title = diagnostic.Code switch
        {
            ModDiagnosticCodes.UnreadableDirectory => Text[nameof(Strings.Findings_UnreadableFolder_Title)],
            ModDiagnosticCodes.UnreadableModConfig => Text[nameof(Strings.Findings_UnreadableDetails_Title)],
            ModDiagnosticCodes.LeftoverUnrecognised => Text[nameof(Strings.Findings_LeftoverUnknown_Title)],
            _ => Text[nameof(Strings.Findings_Other_Title)],
        };

        if (diagnostic.Paths is not [var path, ..])
        {
            return new ModsFolderFindingViewModel { Code = diagnostic.Code, Title = title, Message = diagnostic.Message };
        }

        return new ModsFolderFindingViewModel
        {
            Code = diagnostic.Code,
            Title = title,
            Message = diagnostic.Message,
            ActionText = Text[nameof(Strings.Findings_OpenFolder)],
            ActionCommand = new AsyncRelayCommand(() =>
                _runner.RunAsync(Heading, ct => _launcher.OpenAsync(path, ct), ActivationToken)),
        };
    }

    private static ModsFolderFindingViewModel Finding(
        string code, string title, string message, string actionText, Func<Task> action, bool destructive = false) => new()
        {
            Code = code,
            Title = title,
            Message = message,
            ActionText = actionText,
            ActionCommand = new AsyncRelayCommand(action),
            IsDestructive = destructive,
        };

    private static ModsFolderFindingViewModel Finding(
        string code, string title, string message, string actionText, Action action) =>
        Finding(code, title, message, actionText, () =>
        {
            action();
            return Task.CompletedTask;
        });

    private void OpenRename(IReadOnlyList<string> paths) =>
        Rename.Open([.. paths.Select(path => new RenameChoiceViewModel(path, NameOf(path)))]);

    /// <summary>A folder's name exactly as on disk: the last segment of its scanned path.</summary>
    /// <remarks>Not looked up by path on purpose: path lookups ignore case, and here case matters.</remarks>
    private static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];

    /// <summary>Moves empty character folders to the trash; one that cannot go does not stop the rest.</summary>
    private Task<bool> DeleteFoldersAsync(IReadOnlyList<VariantFolder> folders) => _runner.RunAsync(
        Heading,
        async ct =>
        {
            if (_game.ModsDirectory is not { Length: > 0 } modsDirectory)
            {
                return;
            }

            var trashed = new List<TrashResult>();

            foreach (var folder in folders)
            {
                try
                {
                    var result = await _files
                        .DeleteEmptyCharacterFolderAsync(folder.Path, modsDirectory, ct)
                        .ConfigureAwait(true);

                    if (result.Trash is { } record)
                    {
                        trashed.Add(record);
                    }
                }
                catch (ModOperationException exception)
                {
                    _notifications.Add(NotificationSeverity.Error, folder.Name, exception.Message);
                }
            }

            await _rescan(ct).ConfigureAwait(true);

            if (trashed.Count == 0)
            {
                return;
            }

            Notification? notice = null;

            var undo = new AsyncRelayCommand(async () =>
            {
                await RestoreAsync(trashed).ConfigureAwait(true);

                if (trashed.Count == 0 && notice is not null)
                {
                    _notifications.Dismiss(notice);
                }
            });

            notice = _notifications.Add(
                NotificationSeverity.Information,
                Text[nameof(Strings.Findings_Deleted_Title)],
                Text.Format(nameof(Strings.Findings_Deleted), Text.Folders(trashed.Count)),
                action: undo,
                actionText: Text[nameof(Strings.Notifications_Undo)]);
        },
        CancellationToken.None);

    /// <summary>Moves a trash folder an older XXSM left inside Mods to beside it; nothing is emptied.</summary>
    private Task<bool> MoveTrashOutAsync(string trash) => _runner.RunAsync(
        Heading,
        async ct =>
        {
            if (_game.ModsDirectory is not { Length: > 0 } modsDirectory)
            {
                return;
            }

            var result = await _files.MoveTrashOutAsync(trash, modsDirectory, ct).ConfigureAwait(true);

            await _rescan(ct).ConfigureAwait(true);

            _notifications.Add(
                result.Problems.Count > 0 ? NotificationSeverity.Warning : NotificationSeverity.Information,
                Text[nameof(Strings.Findings_TrashMovedOut_Title)],
                result.Problems.Count > 0
                    ? Text.ForCount(result.Problems.Count, nameof(Strings.Findings_TrashMovedOut_WithProblems_One), nameof(Strings.Findings_TrashMovedOut_WithProblems),
                        Text.Items(result.Moved),
                        result.Destination,
                        Text.Items(result.Problems.Count))
                    : Text.Format(nameof(Strings.Findings_TrashMovedOut), Text.Items(result.Moved), PathDisplay.Show(result.Destination)));

            foreach (var problem in result.Problems)
            {
                _notifications.Add(NotificationSeverity.Warning, result.Source, problem);
            }
        },
        CancellationToken.None);

    /// <summary>Moves spare leftovers to the trash with Undo; one that became a mod's only copy stays.</summary>
    private Task<bool> TrashLeftoversAsync(IReadOnlyList<string> paths) => _runner.RunAsync(
        Heading,
        async ct =>
        {
            if (_game.ModsDirectory is not { Length: > 0 } modsDirectory)
            {
                return;
            }

            var result = await _leftovers.TrashSpareCopiesAsync(paths, modsDirectory, ct).ConfigureAwait(true);

            await _rescan(ct).ConfigureAwait(true);
            ReportProblems(result);

            if (result.Trashed.Count == 0)
            {
                return;
            }

            var trashed = result.Trashed.ToList();
            Notification? notice = null;

            var undo = new AsyncRelayCommand(async () =>
            {
                await RestoreAsync(trashed).ConfigureAwait(true);

                if (trashed.Count == 0 && notice is not null)
                {
                    _notifications.Dismiss(notice);
                }
            });

            notice = _notifications.Add(
                NotificationSeverity.Information,
                Text[nameof(Strings.Findings_LeftoversTrashed_Title)],
                Text.Format(nameof(Strings.Findings_Deleted), Text.Folders(trashed.Count)),
                action: undo,
                actionText: Text[nameof(Strings.Notifications_Undo)]);
        },
        CancellationToken.None);

    /// <summary>Gives each mod left under a working name its own name again.</summary>
    private Task<bool> PutLeftoversBackAsync(IReadOnlyList<string> paths) => _runner.RunAsync(
        Heading,
        async ct =>
        {
            if (_game.ModsDirectory is not { Length: > 0 } modsDirectory)
            {
                return;
            }

            var result = await _leftovers.PutBackAsync(paths, modsDirectory, ct).ConfigureAwait(true);

            await _rescan(ct).ConfigureAwait(true);
            ReportProblems(result);

            if (result.PutBack.Count > 0)
            {
                _notifications.Add(
                    NotificationSeverity.Information,
                    Text[nameof(Strings.Findings_PutBack_Title)],
                    Text.ForCount(result.PutBack.Count, nameof(Strings.Findings_PutBackDone_One), nameof(Strings.Findings_PutBackDone), Text.Mods(result.PutBack.Count)));
            }
        },
        CancellationToken.None);

    /// <summary>Each leftover that was left alone is its own notice, in Core's words.</summary>
    private void ReportProblems(LeftoverTidyResult result)
    {
        foreach (var problem in result.Problems)
        {
            _notifications.Add(NotificationSeverity.Warning, problem.Path, problem.Reason);
        }
    }

    /// <summary>Puts deleted folders back; what could not come back stays for a second try.</summary>
    private Task<bool> RestoreAsync(List<TrashResult> pending) => _runner.RunAsync(
        Text[nameof(Strings.Notifications_Undo)],
        async ct =>
        {
            var restored = 0;
            var skipped = new List<(string Path, string Reason)>();

            foreach (var record in pending.ToList())
            {
                try
                {
                    await _files.RestoreAsync(record, ct).ConfigureAwait(true);
                    pending.Remove(record);
                    restored++;
                }
                catch (ModOperationException exception)
                {
                    skipped.Add((record.OriginalPath, exception.Message));
                }
            }

            await _rescan(ct).ConfigureAwait(true);

            _notifications.Add(
                skipped.Count > 0 ? NotificationSeverity.Warning : NotificationSeverity.Information,
                Text[nameof(Strings.Notifications_Undo)],
                skipped.Count > 0
                    ? Text.ForCount(skipped.Count, nameof(Strings.Findings_Restored_WithSkips_One), nameof(Strings.Findings_Restored_WithSkips), Text.Folders(restored), Text.Folders(skipped.Count))
                    : Text.Format(nameof(Strings.Findings_Restored), Text.Folders(restored)));

            foreach (var (path, reason) in skipped)
            {
                _notifications.Add(NotificationSeverity.Warning, path, reason);
            }
        },
        CancellationToken.None);

    private void OnGameChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GameContext.Inventory) or nameof(GameContext.ModsDirectory))
        {
            RebuildFindings();
        }
    }

    private void OnNotificationsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _notifications.MarkAllRead();
        OnPropertyChanged(nameof(HasNotifications));
    }

    private void OnPanelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EmptyFoldersReviewViewModel.IsOpen) or nameof(FolderRenameViewModel.IsOpen))
        {
            OnPropertyChanged(nameof(IsAnyPanelOpen));
        }
    }
}
