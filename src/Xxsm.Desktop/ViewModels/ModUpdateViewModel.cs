using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Core;
using Xxsm.Core.GameBanana;
using Xxsm.Core.Ini;
using Xxsm.Core.Io;
using Xxsm.Desktop.Services;
using Xxsm.Packs.Downloads;
using Xxsm.Packs.GameBanana;

namespace Xxsm.Desktop.ViewModels;

/// <summary>One line of the new version's INI that becomes the user's value.</summary>
/// <param name="Name">The INI, section and line: <c>mod.ini [KeyHat] key</c>.</param>
/// <param name="From">The new version's own value.</param>
/// <param name="To">The user's value, which it becomes.</param>
public sealed record ModUpdateIniLine(string Name, string From, string To);

/// <summary>One file an update changes, as the confirmation lists it.</summary>
/// <param name="change">The change.</param>
/// <param name="kindText">What happens to it, in words.</param>
public sealed class ModUpdateChangeRowViewModel(ModUpdateFileChange change, string kindText)
{
    /// <summary>Its path inside the mod.</summary>
    public string Path { get; } = change.RelativePath;

    /// <summary>Added, replaced or removed, in words.</summary>
    public string KindText { get; } = kindText;

    /// <summary>Whether it is added.</summary>
    public bool IsAdded { get; } = change.Kind == ModUpdateChangeKind.Added;

    /// <summary>Whether it is removed.</summary>
    public bool IsRemoved { get; } = change.Kind == ModUpdateChangeKind.Removed;

    /// <summary>Whether it is an .ini the user may have edited.</summary>
    public bool MayBeEdited { get; } = change.MayBeEdited;
}

/// <summary>"Update this mod?": downloads, lists every file change, and replaces the mod only on confirm.</summary>
/// <remarks>Runs on its own token, never a page's. The old version goes to the trash, with Undo.</remarks>
public sealed partial class ModUpdateViewModel : ObservableObject, IDisposable
{
    private readonly IModUpdater _updater;
    private readonly IDownloadManager _downloads;
    private readonly GameContext _game;
    private readonly INotificationService _notifications;
    private readonly ViewModelWorkRunner _work;
    private readonly IUrlLauncher _urls;
    private readonly IUiDispatcher _ui;
    private readonly ITextCatalogue _text;
    private readonly Func<CancellationToken, Task> _rescan;
    private readonly IIniOriginalsService _iniOriginals;

    private ModUpdateSource? _source;
    private ModUpdatePlan? _plan;
    private DownloadJob? _job;
    private string? _modFolder;
    private CancellationTokenSource? _preparing;
    private Task _settled = Task.CompletedTask;

    /// <summary>Creates the panel.</summary>
    /// <param name="updater">Plans and applies the update.</param>
    /// <param name="downloads">Reports the download's progress.</param>
    /// <param name="game">The selected game, for its Mods folder.</param>
    /// <param name="notifications">Where the result, and its Undo, are said.</param>
    /// <param name="work">Turns an unexpected failure into a notice rather than a crash.</param>
    /// <param name="urls">Opens the mod's page when the site wants a browser.</param>
    /// <param name="ui">Brings the download's progress onto the window's thread.</param>
    /// <param name="text">The interface's wording.</param>
    /// <param name="rescan">Rescans the Mods folder after the mod changed.</param>
    /// <param name="iniOriginals">Says whether the mod has key or default changes to keep.</param>
    public ModUpdateViewModel(
        IModUpdater updater,
        IDownloadManager downloads,
        GameContext game,
        INotificationService notifications,
        ViewModelWorkRunner work,
        IUrlLauncher urls,
        IUiDispatcher ui,
        ITextCatalogue text,
        Func<CancellationToken, Task> rescan,
        IIniOriginalsService iniOriginals)
    {
        ArgumentNullException.ThrowIfNull(updater);
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(urls);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(rescan);
        ArgumentNullException.ThrowIfNull(iniOriginals);

        _updater = updater;
        _downloads = downloads;
        _game = game;
        _notifications = notifications;
        _work = work;
        _urls = urls;
        _ui = ui;
        _text = text;
        _rescan = rescan;
        _iniOriginals = iniOriginals;

        FileChooser = new GameBananaFileChooserViewModel(text);
        FileChooser.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GameBananaFileChooserViewModel.Selected))
            {
                OnPropertyChanged(nameof(CanContinue));
            }
        };
    }

    /// <summary>Whether the panel is on screen.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>Whether the installed version has key or default changes made in XXSM.</summary>
    [ObservableProperty]
    private bool _hasIniChanges;

    /// <summary>Whether those changes are made again in the new version. On unless deselected.</summary>
    [ObservableProperty]
    private bool _keepIniChanges = true;

    /// <summary>The lines not carried because the new version does not have them, in words; null when none.</summary>
    [ObservableProperty]
    private string? _iniMissingText;

    /// <summary>Each line of the new version's INI that becomes the user's value: the author's, then theirs.</summary>
    public ObservableCollection<ModUpdateIniLine> IniReplaced { get; } = [];

    /// <summary>Whether a line of the new version's INI becomes the user's value.</summary>
    public bool HasIniReplaced => IniReplaced.Count > 0;

    /// <summary>Whether the page is being read or the file downloaded and compared.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpdate))]
    [NotifyPropertyChangedFor(nameof(CanChooseAnotherFile))]
    private bool _isPreparing;

    /// <summary>Whether the update is being applied.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpdate))]
    [NotifyPropertyChangedFor(nameof(CanChooseAnotherFile))]
    private bool _isApplying;

    /// <summary>The mod's name, for the title.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TitleText))]
    private string? _modName;

    /// <summary>"Update Frostdew Tail?"</summary>
    public string TitleText => ModName is { Length: > 0 } name
        ? _text.Format(nameof(Strings.ModUpdate_Title), name)
        : _text[nameof(Strings.ModUpdate_Heading)];

    /// <summary>Whether the new version's files are all the same as the installed ones.</summary>
    public bool IsIdentical => _plan is not null && Changes.Count == 0;

    /// <summary>What the panel is doing while it prepares: reading, downloading, comparing.</summary>
    [ObservableProperty]
    private string? _progressText;

    /// <summary>How far the download is, out of 100, or null before anything is known.</summary>
    [ObservableProperty]
    private double? _progressPercent;

    /// <summary>Why the update cannot go ahead, in the site's or the system's words, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string? _problem;

    /// <summary>The page to open when GameBanana wants a browser for the download, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlocked))]
    private string? _blockedPageUrl;

    /// <summary>"1.3.1 → 1.4", or the one version known.</summary>
    [ObservableProperty]
    private string? _versionText;

    /// <summary>When the page last changed, in words, or null.</summary>
    [ObservableProperty]
    private string? _pageDateText;

    /// <summary>Said when the archive held several mods and the likeliest was taken.</summary>
    [ObservableProperty]
    private string? _fileNote;

    /// <summary>Which of the page's files the comparison is of, when it has several.</summary>
    [ObservableProperty]
    private string? _fileText;

    /// <summary>Said when a replaced .ini may hold the user's own edit.</summary>
    [ObservableProperty]
    private string? _editWarning;

    /// <summary>How many files stay as they are.</summary>
    [ObservableProperty]
    private string? _unchangedText;

    /// <summary>The page's files, when it has several, to choose one before anything downloads.</summary>
    public GameBananaFileChooserViewModel FileChooser { get; }

    /// <summary>Whether the panel is asking which of the page's files to update from.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinue))]
    [NotifyPropertyChangedFor(nameof(CanChooseAnotherFile))]
    private bool _isChoosingFile;

    /// <summary>Every file added, replaced or removed.</summary>
    public ObservableCollection<ModUpdateChangeRowViewModel> Changes { get; } = [];

    /// <summary>Whether there is a list of changes to show.</summary>
    public bool HasPlan => _plan is not null;

    /// <summary>Whether the page has been read, so its version and date can be shown.</summary>
    public bool HasSource => _source is not null;

    /// <summary>Whether a file has been chosen and can be downloaded and compared.</summary>
    public bool CanContinue => IsChoosingFile && FileChooser.HasSelection;

    /// <summary>Whether the page has other files to go back to, after a comparison or a failed download.</summary>
    public bool CanChooseAnotherFile =>
        _source?.Page.HasFileChoice == true && !IsChoosingFile && !IsPreparing && !IsApplying;

    /// <summary>Whether there is something wrong to say.</summary>
    public bool HasProblem => Problem is { Length: > 0 };

    /// <summary>Whether the download was answered with a browser challenge.</summary>
    public bool IsBlocked => BlockedPageUrl is { Length: > 0 };

    /// <summary>Whether Update can be pressed.</summary>
    public bool CanUpdate => _plan is not null && !IsPreparing && !IsApplying;

    /// <summary>Completes when the panel has finished whatever it started last.</summary>
    /// <returns>A task that completes when the list, or the reason, is on screen.</returns>
    public Task WhenSettledAsync() => _settled;

    /// <summary>Opens the panel on a mod and starts preparing its update.</summary>
    /// <param name="modFolder">The mod, which must be linked to a GameBanana page.</param>
    /// <returns>A task that completes when the list of changes, or the reason, is on screen.</returns>
    public Task OpenAsync(string modFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modFolder);

        Close();

        _modFolder = modFolder;
        ModName = _game.Inventory?.AllMods.FirstOrDefault(mod => PathComparer.AreEqual(mod.Path, modFolder))?.DisplayName;
        IsOpen = true;

        return _settled = PrepareAsync(fileId: null);
    }

    /// <summary>Downloads the file chosen and lists what it would change.</summary>
    /// <returns>A task that completes when the list of changes, or the reason, is on screen.</returns>
    [RelayCommand]
    public Task ContinueAsync()
    {
        if (!CanContinue || FileChooser.Selected?.Id is not { } fileId)
        {
            return Task.CompletedTask;
        }

        return _settled = PrepareAsync(fileId);
    }

    /// <summary>Puts the comparison away and goes back to the page's files. The download stays on the list.</summary>
    [RelayCommand]
    public void ChooseAnotherFile()
    {
        if (!CanChooseAnotherFile)
        {
            return;
        }

        ClearPlan();
        IsChoosingFile = true;
        RaisePlanChanged();
    }

    /// <summary>Replaces the mod with the new version, then says so with an Undo.</summary>
    [RelayCommand]
    public async Task UpdateAsync()
    {
        if (_plan is not { } plan || _game.ModsDirectory is not { Length: > 0 } mods || !CanUpdate)
        {
            return;
        }

        IsApplying = true;
        ModUpdateResult? result = null;

        try
        {
            await _work.RunAsync(
                _text[nameof(Strings.ModUpdate_Heading)],
                async ct =>
                {
                    try
                    {
                        plan.KeepIniChanges = KeepIniChanges;
                        result = await _updater.ApplyAsync(plan, mods, ct).ConfigureAwait(true);
                    }
                    catch (XxsmException ex)
                    {
                        Problem = ex.Message;
                    }
                },
                CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            IsApplying = false;
        }

        if (result is null)
        {
            return;
        }

        var name = ModName ?? plan.DisplayName;
        Close();

        Notification? notice = null;
        var undo = new AsyncRelayCommand(async () =>
        {
            var undone = await _work.RunAsync(
                _text[nameof(Strings.ModUpdate_Heading)],
                async ct =>
                {
                    await _updater.UndoAsync(result, mods, ct).ConfigureAwait(true);
                    await _rescan(ct).ConfigureAwait(true);
                },
                CancellationToken.None).ConfigureAwait(true);

            if (undone && notice is not null)
            {
                _notifications.Dismiss(notice);
                _notifications.Add(
                    NotificationSeverity.Information,
                    _text[nameof(Strings.ModUpdate_Heading)],
                    _text.Format(nameof(Strings.ModUpdate_Undone), name));
            }
        });

        notice = _notifications.Add(
            NotificationSeverity.Information,
            _text[nameof(Strings.ModUpdate_Heading)],
            (result.Version is { Length: > 0 } version
                ? _text.Format(nameof(Strings.ModUpdate_Done_Version), name, version)
                : _text.Format(nameof(Strings.ModUpdate_Done), name)) + Carried(result.Carried),
            action: undo,
            actionText: _text[nameof(Strings.Notifications_Undo)]);

        await _rescan(CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>Stops whatever is being prepared and takes the panel away. The download stays on the list.</summary>
    [RelayCommand]
    public void Close()
    {
        _preparing?.Cancel();
        _preparing = null;

        StopWatching();
        ClearPlan();
        _source = null;
        _modFolder = null;

        IsOpen = false;
        IsPreparing = false;
        IsChoosingFile = false;
        FileChooser.Clear();
        ModName = null;
        ProgressText = null;
        ProgressPercent = null;
        VersionText = null;
        PageDateText = null;

        RaisePlanChanged();
    }

    /// <summary>Opens the mod's page, for fetching the file in a browser when the site asks for one.</summary>
    [RelayCommand]
    public Task OpenPageAsync() => BlockedPageUrl is { Length: > 0 } address
        ? _urls.OpenAsync(address, CancellationToken.None)
        : Task.CompletedTask;

    /// <inheritdoc />
    public void Dispose() => Close();

    /// <summary>Takes the comparison, and anything said about it, off the screen.</summary>
    private void ClearPlan()
    {
        _plan?.Dispose();
        _plan = null;
        Changes.Clear();
        Problem = null;
        BlockedPageUrl = null;
        FileNote = null;
        FileText = null;
        EditWarning = null;
        UnchangedText = null;
        HasIniChanges = false;
        KeepIniChanges = true;
        IniReplaced.Clear();
        IniMissingText = null;
        OnPropertyChanged(nameof(HasIniReplaced));
    }

    private async Task PrepareAsync(long? fileId)
    {
        if (_modFolder is not { } modFolder)
        {
            return;
        }

        _preparing?.Cancel();
        var preparing = new CancellationTokenSource();
        _preparing = preparing;

        ClearPlan();
        IsChoosingFile = false;
        IsPreparing = true;
        ProgressPercent = null;
        ProgressText = _text[nameof(Strings.ModUpdate_Reading)];
        RaisePlanChanged();

        ModUpdatePlan? plan = null;
        var source = _source;
        var choose = false;

        await _work.RunAsync(
            _text[nameof(Strings.ModUpdate_Heading)],
            async ct =>
            {
                try
                {
                    if (source is null)
                    {
                        source = await _updater.ReadAsync(modFolder, ct).ConfigureAwait(true);

                        // Several files: the user chooses, before one downloads.
                        if (source.Page.HasFileChoice && fileId is null)
                        {
                            choose = true;
                            return;
                        }
                    }

                    plan = await _updater
                        .PlanAsync(source, _game.GameId, fileId, Watch, ct)
                        .ConfigureAwait(true);
                }
                catch (GameBananaDownloadBlockedException ex)
                {
                    Problem = ex.Message;
                    BlockedPageUrl = ex.PageUrl.AbsoluteUri;
                }
                catch (XxsmException ex)
                {
                    Problem = ex.Message;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Closed, or another file chosen: whoever cancelled has the panel now.
                }
            },
            preparing.Token).ConfigureAwait(true);

        StopWatching();

        if (!ReferenceEquals(_preparing, preparing))
        {
            plan?.Dispose();
            return;
        }

        _preparing = null;
        preparing.Dispose();
        IsPreparing = false;
        ProgressText = null;
        ProgressPercent = null;

        if (source is not null && _source is null)
        {
            ShowSource(source);
        }

        if (choose && source is not null)
        {
            FileChooser.Show(source.Page, source.InstalledFile?.IdRow);
            IsChoosingFile = true;
            RaisePlanChanged();
            return;
        }

        if (plan is null)
        {
            RaisePlanChanged();
            return;
        }

        Show(plan);
        await ReadIniChangesAsync(plan).ConfigureAwait(true);
    }

    /// <summary>Offers to keep the key and default changes when the installed version has any.</summary>
    private async Task ReadIniChangesAsync(ModUpdatePlan plan)
    {
        try
        {
            var changes = await _iniOriginals.ReadAsync(plan.ModFolder, CancellationToken.None).ConfigureAwait(true);

            if (!ReferenceEquals(_plan, plan) || !(changes.HasKeyChanges || changes.HasDefaultChanges))
            {
                return;
            }

            var preview = await _iniOriginals.PreviewCarryAsync(plan.ModFolder, plan.Root.Path, CancellationToken.None).ConfigureAwait(true);

            if (!ReferenceEquals(_plan, plan))
            {
                return;
            }

            HasIniChanges = true;
            EditWarning = null;

            foreach (var item in preview.Replaced)
            {
                IniReplaced.Add(new ModUpdateIniLine(
                    $"{item.File} [{item.Change.Section}] {item.Change.Name}", item.NewAuthorValue ?? string.Empty, item.Change.Current));
            }

            IniMissingText = preview.Missing.Count > 0
                ? _text.Format(nameof(Strings.ModUpdate_Carried_Missing), string.Join(", ", preview.Missing.Select(item => $"[{item.Change.Section}] {item.Change.Name} = {item.Change.Current}")))
                : null;
            OnPropertyChanged(nameof(HasIniReplaced));
        }
        catch (ModOperationException)
        {
            HasIniChanges = false;
        }
    }

    /// <summary>What became of the key and default changes, for the notice; empty when there is nothing to say.</summary>
    private string Carried(IniCarryReport? carried)
    {
        if (carried is null || carried.IsEmpty)
        {
            return string.Empty;
        }

        static string Lines(IEnumerable<IniCarried> items) =>
            string.Join(", ", items.Select(item => $"[{item.Change.Section}] {item.Change.Name} = {item.Change.Current}"));

        var text = " " + _text[nameof(Strings.ModUpdate_Carried)];

        if (carried.Clashed.Count > 0)
        {
            text += " " + _text.Format(nameof(Strings.ModUpdate_Carried_Clashed), Lines(carried.Clashed));
        }

        if (carried.Missing.Count > 0)
        {
            text += " " + _text.Format(nameof(Strings.ModUpdate_Carried_Missing), Lines(carried.Missing));
        }

        if (carried.OtherChangesLeft.Count > 0)
        {
            text += " " + _text.Format(nameof(Strings.ModUpdate_Carried_Other), string.Join(", ", carried.OtherChangesLeft));
        }

        return text;
    }

    private void ShowSource(ModUpdateSource source)
    {
        _source = source;

        VersionText = (source.InstalledVersion, source.Page.Version) switch
        {
            ({ Length: > 0 } from, { Length: > 0 } to) => _text.Format(nameof(Strings.ModUpdate_Versions), from, to),
            (_, { Length: > 0 } to) => to,
            _ => null,
        };

        PageDateText = source.Page.DateModified is { } changed
            ? _text.Format(
                nameof(Strings.ModUpdate_PageChanged),
                Resources.TextDates.Date(changed))
            : null;
    }

    private void Show(ModUpdatePlan plan)
    {
        _plan = plan;
        ModName ??= plan.DisplayName;

        FileText = _source?.Page.HasFileChoice == true
            ? _text.Format(
                nameof(Strings.ModUpdate_FromFile),
                plan.File.Description?.Trim() is { Length: > 0 } description ? description : plan.File.File ?? string.Empty)
            : null;

        FileNote = plan.RootWasChosen
            ? _text.Format(nameof(Strings.ModUpdate_RootChosen), plan.Root.Name)
            : null;

        EditWarning = plan.ReplacesAnEditedIni ? _text[nameof(Strings.ModUpdate_EditedIni)] : null;
        UnchangedText = plan.UnchangedCount > 0
            ? _text.Format(nameof(Strings.ModUpdate_Unchanged), _text.Files(plan.UnchangedCount))
            : null;

        foreach (var change in plan.Changes)
        {
            Changes.Add(new ModUpdateChangeRowViewModel(change, _text[change.Kind switch
            {
                ModUpdateChangeKind.Added => nameof(Strings.ModUpdate_Added),
                ModUpdateChangeKind.Removed => nameof(Strings.ModUpdate_Removed),
                _ => nameof(Strings.ModUpdate_Replaced),
            }]));
        }

        RaisePlanChanged();
    }

    private void Watch(DownloadJob job)
    {
        _job = job;
        _downloads.Progress += OnProgress;
        _ui.Post(() => ShowProgress(job));
    }

    private void StopWatching()
    {
        _downloads.Progress -= OnProgress;
        _job = null;
    }

    private void OnProgress(object? sender, DownloadChange change)
    {
        if (ReferenceEquals(change.Job, _job))
        {
            _ui.Post(() => ShowProgress(change.Job));
        }
    }

    private void ShowProgress(DownloadJob job)
    {
        if (!ReferenceEquals(job, _job))
        {
            return;
        }

        ProgressPercent = job.Fraction is { } fraction ? fraction * 100 : null;
        ProgressText = job.TotalBytes is { } total
            ? _text.Format(nameof(Strings.ModUpdate_Downloading), Megabytes(job.Bytes), Megabytes(total))
            : _text[nameof(Strings.ModUpdate_Reading)];
    }

    private static string Megabytes(long bytes) =>
        (bytes / 1_048_576d).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture);

    private void RaisePlanChanged()
    {
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(HasSource));
        OnPropertyChanged(nameof(IsIdentical));
        OnPropertyChanged(nameof(CanUpdate));
        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(CanChooseAnotherFile));
    }
}
