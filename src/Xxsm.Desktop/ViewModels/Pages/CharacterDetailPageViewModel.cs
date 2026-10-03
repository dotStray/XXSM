using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Core;
using Xxsm.Core.GameBanana;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Core.Settings;
using Xxsm.Desktop.Services;
using Xxsm.Packs.GameBanana;
using Xxsm.Packs.Merge;
using Xxsm.Packs.Pictures;

namespace Xxsm.Desktop.ViewModels.Pages;

/// <summary>How the mod grid's rows are narrowed, beyond the skin filter.</summary>
public enum ModStatusFilter
{
    /// <summary>Every mod. The default.</summary>
    All = 0,

    /// <summary>Only what 3DMigoto will load.</summary>
    Enabled,

    /// <summary>Only what will not.</summary>
    Disabled,
}

/// <summary>What the mod grid's rows are ordered by.</summary>
public enum ModSortMode
{
    /// <summary>The name shown in the grid. The default.</summary>
    Name = 0,

    /// <summary>Whether 3DMigoto will load the mod.</summary>
    Status,

    /// <summary>Who made the mod, when its metadata says.</summary>
    Author,

    /// <summary>When the user got the mod, from its own metadata; mods without one sort last either way.</summary>
    DateAdded,

    /// <summary>When the mod folder's contents last changed, from the filesystem.</summary>
    DateModified,
}

/// <summary>The character detail view: the mod grid, multi-select, <c>SPACE</c> to toggle, pane and menus.</summary>
/// <remarks>A singleton page, shown by the shell outside the rail; <c>Show</c> rebuilds everything it shows.</remarks>
public sealed partial class CharacterDetailPageViewModel : PageViewModel, IRefreshablePage, ISearchablePage
{
    private readonly GameContext _game;
    private readonly IModFileOperations _modFiles;
    private readonly IModConfigStore _modConfigs;
    private readonly IModFiling _filing;
    private readonly IStoragePicker _picker;
    private readonly IFolderLauncher _folderLauncher;
    private readonly IUrlLauncher _urlLauncher;
    private readonly IPortraitCache _portraits;
    private readonly IModPreviewCache _previews;
    private readonly IModPreviewSource _previewSource;
    private readonly IModPreviewEditor _pictures;
    private readonly IClipboardImageReader _clipboard;
    private readonly IPictureDownloader _downloader;
    private readonly IAppSettingsStore _settings;
    private readonly IModUpdateChecker _updates;
    private readonly INotificationService _notifications;
    private readonly Xxsm.Core.Profiles.IProfileService _profiles;
    private readonly ViewModelWorkRunner _runner;
    private readonly ModInstallViewModel _install;
    private readonly SortReviewViewModel _sortReview;
    private readonly CharacterManagerViewModel _manager;
    private readonly GameBananaFillViewModel _fill;
    private readonly ModUpdateViewModel _update;
    private readonly Func<CancellationToken, Task> _rescan;
    private readonly Action _goBack;

    private CharacterTileViewModel? _character;
    private IBitmapLease? _portraitHandle;
    private IBitmapLease? _previewLease;
    private CancellationTokenSource? _previewLoad;
    private string? _previewFor;
    private IReadOnlyList<ModRowViewModel> _selectedRows = [];
    private IReadOnlyList<ModRowViewModel> _movingRows = [];
    private Func<CancellationToken, Task>? _pendingConfirmation;
    private List<IBitmapLease> _skinPortraits = [];
    private string? _reselectPath;

    /// <summary>The mod whose values the edit boxes hold; a rescan that selects it again keeps what was typed.</summary>
    private string? _editing;
    private bool _restoringSelection;
    private int _totalModCount;

    /// <summary>Creates the page.</summary>
    public CharacterDetailPageViewModel(
        GameContext game,
        IModFileOperations modFiles,
        IModConfigStore modConfigs,
        IModFiling filing,
        IStoragePicker picker,
        IFolderLauncher folderLauncher,
        IUrlLauncher urlLauncher,
        IPortraitCache portraits,
        IModPreviewCache previews,
        IModPreviewSource previewSource,
        IModPreviewEditor pictures,
        IClipboardImageReader clipboard,
        IPictureDownloader downloader,
        IAppSettingsStore settings,
        IModUpdateChecker updates,
        INotificationService notifications,
        ViewModelWorkRunner runner,
        ModInstallViewModel install,
        SortReviewViewModel sortReview,
        CharacterManagerViewModel manager,
        GameBananaFillViewModel fill,
        ModUpdateViewModel update,
        ITextCatalogue text,
        ColumnWidthMemory columnWidths,
        Func<CancellationToken, Task> rescan,
        Action goBack,
        Xxsm.Core.Ini.IKeySwapService keySwaps,
        Xxsm.Core.Profiles.IProfileService profiles,
        Xxsm.Core.Ini.ISavedSettingsService savedSettings)
        : base(text)
    {
        ArgumentNullException.ThrowIfNull(columnWidths);
        ArgumentNullException.ThrowIfNull(keySwaps);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(savedSettings);
        _profiles = profiles;
        ColumnWidths = columnWidths;

        KeySwaps = new KeySwapEditorViewModel(keySwaps, text);
        KeySwaps.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(KeySwapEditorViewModel.IsDirty))
            {
                OnPropertyChanged(nameof(IsEditDirty));
                SaveEditsCommand.NotifyCanExecuteChanged();
            }
        };

        SavedSettings = new SavedSettingsViewModel(savedSettings, text);
        SavedSettings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SavedSettingsViewModel.IsDirty))
            {
                OnPropertyChanged(nameof(IsEditDirty));
                SaveEditsCommand.NotifyCanExecuteChanged();
            }
        };

        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(modFiles);
        ArgumentNullException.ThrowIfNull(modConfigs);
        ArgumentNullException.ThrowIfNull(filing);
        ArgumentNullException.ThrowIfNull(picker);
        ArgumentNullException.ThrowIfNull(folderLauncher);
        ArgumentNullException.ThrowIfNull(urlLauncher);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(fill);
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(portraits);
        ArgumentNullException.ThrowIfNull(previews);
        ArgumentNullException.ThrowIfNull(previewSource);
        ArgumentNullException.ThrowIfNull(pictures);
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(sortReview);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(rescan);
        ArgumentNullException.ThrowIfNull(goBack);

        _game = game;
        _modFiles = modFiles;
        _modConfigs = modConfigs;
        _filing = filing;
        _picker = picker;
        _folderLauncher = folderLauncher;
        _urlLauncher = urlLauncher;
        _portraits = portraits;
        _previews = previews;
        _previewSource = previewSource;
        _pictures = pictures;
        _clipboard = clipboard;
        _settings = settings;
        _updates = updates;
        _fill = fill;
        _update = update;
        _downloader = downloader;
        _notifications = notifications;
        _runner = runner;
        _install = install;
        _sortReview = sortReview;
        _manager = manager;
        _rescan = rescan;
        _goBack = goBack;
    }

    /// <inheritdoc />
    public override string Heading =>
        _character?.DisplayName ?? Text[nameof(Strings.CharacterDetail_Heading_Fallback)];

    /// <summary>The stand-in shown until the portrait decodes, and for a character with none.</summary>
    public string Initial => _character?.Initial ?? "?";

    /// <summary>The decoded portrait of the character the page opened on, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPortrait))]
    [NotifyPropertyChangedFor(nameof(HeaderPortrait))]
    [NotifyPropertyChangedFor(nameof(HasHeaderPortrait))]
    private Bitmap? _portrait;

    /// <summary>Whether <see cref="Portrait"/> has a bitmap to show.</summary>
    public bool HasPortrait => Portrait is not null;

    /// <summary>The picture at the top: the selected skin tab's, else the opened character's.</summary>
    public Bitmap? HeaderPortrait => SelectedSkinFilter is { IsAll: false, Portrait: { } skin } ? skin : Portrait;

    /// <summary>Whether <see cref="HeaderPortrait"/> has a bitmap to show.</summary>
    public bool HasHeaderPortrait => HeaderPortrait is not null;

    /// <summary>The mods, after the skin filter, search, status filter and sort.</summary>
    public ObservableCollection<ModRowViewModel> Rows { get; } = [];

    /// <summary>Whether there is a character to show at all.</summary>
    public bool HasCharacter => _character is not null;

    /// <summary>Whether this page shows a place mods sit, Unsorted or Others, rather than a character's.</summary>
    public bool IsPlace => _character?.IsPlace == true;

    /// <summary>Whether the character has no mods filed under it yet.</summary>
    public bool HasNoMods => HasCharacter && _totalModCount == 0;

    /// <summary>Whether a search or filter matched nothing, though mods exist.</summary>
    public bool HasNoMatches => HasCharacter && _totalModCount > 0 && Rows.Count == 0;

    /// <summary>Whether there are rows to show.</summary>
    public bool HasRows => Rows.Count > 0;

    /// <summary>The skin tabs, or empty when the character has no skins.</summary>
    public ObservableCollection<SkinFilterViewModel> SkinFilters { get; } = [];

    /// <summary>The Character Manager, drawn over this view when it is opened from here.</summary>
    public CharacterManagerViewModel CharacterManager => _manager;

    /// <summary>The right-click menu's entries: edit the character, and each of its skins by name.</summary>
    public ObservableCollection<CharacterEditTargetViewModel> EditTargets { get; } = [];

    /// <summary>Whether there is more than one skin tab, including "All".</summary>
    public bool HasSkinFilters => SkinFilters.Count > 1;

    /// <summary>Whether the grid shows every skin of the family at once; each row then names its skin.</summary>
    public bool IsShowingAllSkins => HasSkinFilters && SelectedSkinFilter?.InternalName is null;

    /// <summary>The selected skin tab, or the "All" entry.</summary>
    [ObservableProperty]
    private SkinFilterViewModel? _selectedSkinFilter;

    /// <summary>What the user has typed into the mod search box.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Which mods are shown, beyond the skin filter.</summary>
    [ObservableProperty]
    private ModStatusFilter _statusFilter;

    /// <summary>What the rows are ordered by.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortModeText))]
    private ModSortMode _sortMode;

    /// <summary>Whether the sort runs the other way.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortDirectionText))]
    private bool _sortDescending;

    /// <summary>The current sort's name, shown on the toolbar's sort button.</summary>
    public string SortModeText => Text[SortMode switch
    {
        ModSortMode.Status => nameof(Strings.CharacterDetail_Sort_Status),
        ModSortMode.Author => nameof(Strings.CharacterDetail_Sort_Author),
        ModSortMode.DateAdded => nameof(Strings.CharacterDetail_Sort_DateAdded),
        ModSortMode.DateModified => nameof(Strings.CharacterDetail_Sort_DateModified),
        _ => nameof(Strings.CharacterDetail_Sort_Name),
    }];

    /// <summary>Which way the sort runs, for the direction button's label and tooltip.</summary>
    public string SortDirectionText => Text[SortDescending
        ? nameof(Strings.CharacterDetail_Sort_Descending)
        : nameof(Strings.CharacterDetail_Sort_Ascending)];

    /// <summary>Whether the search box is open; a search in force keeps it open.</summary>
    [ObservableProperty]
    private bool _isSearchOpen;

    /// <summary>Whether the status filter is narrowing the rows, so its icon can say so.</summary>
    public bool IsStatusFiltered => StatusFilter != ModStatusFilter.All;

    /// <summary>The current filter's name, for the filter button's tooltip.</summary>
    public string StatusFilterText => Text[StatusFilter switch
    {
        ModStatusFilter.Enabled => nameof(Strings.CharacterDetail_Filter_Enabled),
        ModStatusFilter.Disabled => nameof(Strings.CharacterDetail_Filter_Disabled),
        _ => nameof(Strings.CharacterDetail_Filter_All),
    }];

    /// <summary>The mod list's remembered column widths, which the view applies and reports to.</summary>
    public ColumnWidthMemory ColumnWidths { get; }

    /// <summary>How many rows are currently selected.</summary>
    public int SelectedCount => _selectedRows.Count;

    /// <summary>"3 selected", for the header. Only meaningful while <see cref="HasSelection"/>.</summary>
    public string SelectionCountText => SelectedCount == 1
        ? Text[nameof(Strings.CharacterDetail_Selection_Count_One)]
        : Text.Format(nameof(Strings.CharacterDetail_Selection_Count_Many), SelectedCount);

    /// <summary>Whether anything is selected. Gates the toolbar's bulk actions.</summary>
    public bool HasSelection => _selectedRows.Count > 0;

    /// <summary>The single selected row's detail, or null when zero or several are selected.</summary>
    public ModRowViewModel? SelectedRow => _selectedRows.Count == 1 ? _selectedRows[0] : null;

    /// <summary>Whether exactly one row is selected, so the detail pane has something to show.</summary>
    public bool HasSelectedRow => SelectedRow is not null;

    /// <summary>Raised when a rescan rebuilt the rows and several should be selected again; the view does it.</summary>
    public event EventHandler<IReadOnlyList<ModRowViewModel>>? SelectionRestoreRequested;

    /// <summary>Whether the grid shows a tick box on every row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectionBarVisible))]
    private bool _isSelectMode;

    /// <summary>The actions that apply to several mods at once, on the bar above the grid.</summary>
    public bool IsSelectionBarVisible => IsSelectMode || SelectedCount > 1;

    /// <summary>The selected mod's own preview image, or null while it loads or when it has none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewImage))]
    private Bitmap? _previewImage;

    /// <summary>Whether <see cref="PreviewImage"/> has a bitmap to show.</summary>
    public bool HasPreviewImage => PreviewImage is not null;

    /// <summary>The grid's current row, two-way, so a rescan can put the selection back.</summary>
    [ObservableProperty]
    private ModRowViewModel? _currentRow;

    /// <summary>The name the user is editing in the detail pane's "Mod name" box.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveEditsCommand))]
    [NotifyPropertyChangedFor(nameof(IsEditDirty))]
    private string _editName = string.Empty;

    /// <summary>The folder name the user is editing.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveEditsCommand))]
    [NotifyPropertyChangedFor(nameof(IsEditDirty))]
    private string _editFolderName = string.Empty;

    /// <summary>The mod's page address, as the user is editing it.</summary>
    [NotifyPropertyChangedFor(nameof(GameBananaLinkText))]
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveEditsCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenModUrlCommand))]
    [NotifyPropertyChangedFor(nameof(IsEditDirty))]
    [NotifyPropertyChangedFor(nameof(CanFetchMod))]
    private string _editModUrl = string.Empty;

    /// <summary>Who made the mod, as the user is editing it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveEditsCommand))]
    [NotifyPropertyChangedFor(nameof(IsEditDirty))]
    private string _editAuthor = string.Empty;

    /// <summary>The mod's version, as the user is editing it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveEditsCommand))]
    [NotifyPropertyChangedFor(nameof(IsEditDirty))]
    private string _editVersion = string.Empty;

    /// <summary>What the mod is, as the user is editing it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveEditsCommand))]
    [NotifyPropertyChangedFor(nameof(IsEditDirty))]
    private string _editDescription = string.Empty;

    /// <summary>The user's own notes on the mod, as they are editing them.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveEditsCommand))]
    [NotifyPropertyChangedFor(nameof(IsEditDirty))]
    private string _editNotes = string.Empty;

    /// <summary>The note under the edit boxes: a clash and a free name, or why a name fails; else null.</summary>
    [ObservableProperty]
    private string? _editNotice;

    /// <summary>Why the folder column shows <c>DISABLED_</c> and the rename box does not, or null.</summary>
    public string? FolderNameNotice =>
        SelectedRow is { IsFolderPrefixed: true } row
            ? Text.Format(nameof(Strings.CharacterDetail_Edit_FolderName_Disabled), row.FolderName)
            : null;

    /// <summary>The selected mod's key bindings, edited in the pane and saved with it.</summary>
    public KeySwapEditorViewModel KeySwaps { get; }

    /// <summary>The selected mod's settings kept between game sessions; new defaults are saved with the pane.</summary>
    public SavedSettingsViewModel SavedSettings { get; }

    /// <summary>Whether any of the edit boxes differs from what is on disk.</summary>
    public bool IsEditDirty =>
        SelectedRow is { } row &&
        (KeySwaps.IsDirty ||
         SavedSettings.IsDirty ||
         !string.Equals(EditName.Trim(), row.DisplayName, StringComparison.Ordinal) ||
         !string.Equals(EditFolderName.Trim(), row.BareFolderName, StringComparison.Ordinal) ||
         !string.Equals(EditModUrl.Trim(), row.ModUrl ?? string.Empty, StringComparison.Ordinal) ||
         !string.Equals(Detail(EditAuthor), row.Author, StringComparison.Ordinal) ||
         !string.Equals(Detail(EditVersion), row.Version, StringComparison.Ordinal) ||
         !string.Equals(Detail(EditDescription), row.Description, StringComparison.Ordinal) ||
         !string.Equals(Detail(EditNotes), row.Notes, StringComparison.Ordinal));

    /// <summary>A details box as it is stored: trimmed, and an empty box is no value at all.</summary>
    private static string? Detail(string text) => text.Trim() is { Length: > 0 } value ? value : null;

    /// <summary>A destructive action awaiting confirmation in place, or null.</summary>
    [ObservableProperty]
    private string? _pendingConfirmationMessage;

    /// <summary>The "Move to…" picker: the one character picker, shared with the import.</summary>
    public CharacterPickerViewModel MovePicker { get; } = new();

    /// <summary>Shows a character: called by the shell each time the grid opens one.</summary>
    /// <param name="character">The tile that was opened.</param>
    public void Show(CharacterTileViewModel character) => Show(character, selectPath: null, skin: null);

    /// <summary>Shows a character, on one skin's tab, with one mod selected, in one rebuild of the rows.</summary>
    /// <param name="character">The tile to show.</param>
    /// <param name="selectPath">The mod to select, or null for none.</param>
    /// <param name="skin">The skin whose tab to open on, or null for the character's own.</param>
    private void Show(CharacterTileViewModel character, string? selectPath, string? skin)
    {
        ArgumentNullException.ThrowIfNull(character);

        _portraitHandle?.Dispose();
        _portraitHandle = null;
        Portrait = null;

        _character = character;
        _selectedRows = [];
        SearchText = string.Empty;
        StatusFilter = ModStatusFilter.All;
        SortMode = ModSortMode.Name;
        MovePicker.Cancel();
        PendingConfirmationMessage = null;
        _pendingConfirmation = null;

        ReleaseSkinPortraits();
        SkinFilters.Clear();

        if (character.Family.Count > 1)
        {
            SkinFilters.Add(new SkinFilterViewModel(null, Text[nameof(Strings.CharacterDetail_Skins_All)]));

            foreach (var member in character.Family)
            {
                SkinFilters.Add(new SkinFilterViewModel(member.InternalName, member.DisplayName));
            }
        }

        // The setter does nothing when the value is unchanged, so RebuildRows below runs regardless.
        SelectedSkinFilter = SkinFilters.FirstOrDefault(filter => string.Equals(
                                 filter.InternalName,
                                 skin ?? character.Variant?.InternalName,
                                 StringComparison.OrdinalIgnoreCase))
                             ?? SkinFilters.FirstOrDefault();

        EditTargets.Clear();

        IReadOnlyList<MergedVariant> editable = character.Family.Count > 0
            ? character.Family
            : character.Variant is { } only ? [only] : [];

        foreach (var member in editable)
        {
            EditTargets.Add(new CharacterEditTargetViewModel(
                Text.Format(nameof(Strings.CharacterDetail_EditCharacter), member.DisplayName),
                new RelayCommand(() => _manager.Edit(member))));
        }

        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(Initial));
        OnPropertyChanged(nameof(HasCharacter));
        OnPropertyChanged(nameof(IsPlace));
        OnPropertyChanged(nameof(HasSkinFilters));
        OnPropertyChanged(nameof(IsShowingAllSkins));
        RaiseSelectionChanged();
        _reselectPath = selectPath;
        RebuildRows();

        if (_game.Data is { } data && character.Variant is { } variant)
        {
            Track(LoadPortraitAsync(data, variant));
            Track(LoadSkinPortraitsAsync(data, character));
        }

        // The rows came from the last scan: show at once, then rescan to correct them.
        Track(_rescan(ActivationToken));
    }

    /// <summary>Decodes a thumbnail for each skin tab, holding the leases until the page moves on.</summary>
    private async Task LoadSkinPortraitsAsync(GameData data, CharacterTileViewModel character)
    {
        var tabs = SkinFilters
            .Where(filter => !filter.IsAll)
            .ToDictionary(filter => filter.InternalName!, StringComparer.OrdinalIgnoreCase);

        foreach (var member in character.Family)
        {
            if (!tabs.TryGetValue(member.InternalName, out var tab))
            {
                continue;
            }

            IBitmapLease? handle;

            try
            {
                handle = await _portraits.AcquireAsync(data, member, ActivationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Show() may have moved on to a different character while this was decoding.
            if (!ReferenceEquals(_character, character))
            {
                handle?.Dispose();
                return;
            }

            if (handle is null)
            {
                continue;
            }

            _skinPortraits.Add(handle);
            tab.Portrait = handle.Bitmap;

            if (ReferenceEquals(tab, SelectedSkinFilter))
            {
                RaiseHeaderPortraitChanged();
            }
        }
    }

    private void RaiseHeaderPortraitChanged()
    {
        OnPropertyChanged(nameof(HeaderPortrait));
        OnPropertyChanged(nameof(HasHeaderPortrait));
    }

    private void ReleaseSkinPortraits()
    {
        foreach (var handle in _skinPortraits)
        {
            handle.Dispose();
        }

        _skinPortraits = [];
    }

    /// <summary>Returns to the character grid; with the Character Manager open, closes that first.</summary>
    [RelayCommand]
    private void Back()
    {
        if (_manager.IsOpen)
        {
            _manager.Close();
            return;
        }

        _goBack();
    }

    /// <summary>Orders the rows by something else.</summary>
    [RelayCommand]
    private void SetSort(ModSortMode mode) => SortMode = mode;

    /// <summary>Shows all mods, or only the enabled or disabled ones.</summary>
    [RelayCommand]
    private void SetStatusFilter(ModStatusFilter filter) => StatusFilter = filter;

    /// <summary>Runs the current sort the other way.</summary>
    [RelayCommand]
    private void ToggleSortDirection() => SortDescending = !SortDescending;

    /// <summary>Opens the search box, leaving what was typed in it.</summary>
    public void OpenSearch() => IsSearchOpen = true;

    /// <summary>Opens the search box, or closes it and clears what was typed, so nothing is hidden unseen.</summary>
    [RelayCommand]
    private void ToggleSearch()
    {
        if (IsSearchOpen)
        {
            SearchText = string.Empty;
            IsSearchOpen = false;
            return;
        }

        IsSearchOpen = true;
    }

    /// <summary>Rescans the Mods folder, so the grid catches up with whatever happened outside XXSM.</summary>
    [RelayCommand]
    private async Task RefreshAsync() =>
        await _runner.RunAsync(Heading, _rescan, ActivationToken).ConfigureAwait(true);

    /// <summary>Toggles the selected mods: enables all if any is disabled, else disables all. <c>SPACE</c>.</summary>
    [RelayCommand]
    private Task ToggleSelectedAsync() => ToggleAsync(_selectedRows);

    /// <summary>Turns the tick boxes on, or off again when they are already showing.</summary>
    [RelayCommand]
    private void ToggleSelectMode() => IsSelectMode = !IsSelectMode;

    /// <summary>Leaves <em>Select</em> mode. The bar's <em>Done</em>.</summary>
    [RelayCommand]
    private void EndSelectMode() => IsSelectMode = false;

    /// <summary>Enables every selected mod, whatever state each was in.</summary>
    [RelayCommand]
    private Task EnableSelectedAsync() => SetSelectedEnabledAsync(enable: true);

    /// <summary>Disables every selected mod, whatever state each was in.</summary>
    [RelayCommand]
    private Task DisableSelectedAsync() => SetSelectedEnabledAsync(enable: false);

    /// <summary>Puts every selected mod into one state, including those already in it.</summary>
    private Task SetSelectedEnabledAsync(bool enable)
    {
        var rows = _selectedRows;

        if (rows.Count == 0)
        {
            return Task.CompletedTask;
        }

        return _runner.RunAsync(
            Heading,
            async ct =>
            {
                foreach (var row in rows.Where(row => row.IsEnabled != enable))
                {
                    await _modFiles.SetEnabledAsync(row.Path, enable, ct).ConfigureAwait(true);
                }

                await _rescan(ct).ConfigureAwait(true);
            },
            ActivationToken);
    }

    /// <summary>Toggles one row from its context menu.</summary>
    [RelayCommand]
    private Task ToggleRowAsync(ModRowViewModel? row) => ToggleAsync(TargetRows(row));

    /// <summary>Toggles exactly one mod, from the switch in its own row, never the rest of the selection.</summary>
    [RelayCommand]
    private Task ToggleOneAsync(ModRowViewModel? row) =>
        ToggleAsync(row is null ? [] : [row]);

    /// <summary>Opens a mod's own folder in the file manager.</summary>
    /// <param name="row">The row to open, or null for the selection.</param>
    [RelayCommand]
    private Task OpenModFolderAsync(ModRowViewModel? row)
    {
        if ((row ?? SelectedRow) is not { } target)
        {
            return Task.CompletedTask;
        }

        return _runner.RunAsync(
            target.DisplayName,
            ct => _folderLauncher.OpenAsync(target.Path, ct),
            ActivationToken);
    }

    /// <summary>Opens the selected mod's own page in the browser.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenModUrl))]
    private async Task OpenModUrlAsync() =>
        await _runner.RunAsync(
            Heading,
            ct => _urlLauncher.OpenAsync(EditModUrl.Trim(), ct),
            ActivationToken).ConfigureAwait(true);

    private bool CanOpenModUrl() => EditModUrl.Trim().Length > 0;

    /// <summary>Puts the edit boxes back to what is on disk.</summary>
    [RelayCommand]
    private void RevertEdits() => ResetEdits();

    /// <summary>Applies the pane's edits: the keys and defaults, the folder rename, then the label, address and details.</summary>
    /// <remarks>Both names are checked for a clash before either is applied, so a save never half-happens.</remarks>
    [RelayCommand(CanExecute = nameof(CanSaveEdits))]
    private Task SaveEditsAsync()
    {
        if (SelectedRow is not { } row)
        {
            return Task.CompletedTask;
        }

        var wantedName = EditName.Trim();
        var wantedFolder = EditFolderName.Trim();
        var wantedUrl = EditModUrl.Trim();
        var wantedDetails = new ModDetails(
            Detail(EditAuthor), Detail(EditVersion), Detail(EditDescription), Detail(EditNotes));

        EditNotice = null;

        if (wantedName.Length == 0 || wantedFolder.Length == 0)
        {
            EditNotice = Text[nameof(Strings.CharacterDetail_Edit_NameRequired)];
            return Task.CompletedTask;
        }

        return _runner.RunAsync(
            Heading,
            ct => SaveEditsAsync(row, wantedName, wantedFolder, wantedUrl, wantedDetails, ct),
            ActivationToken);
    }

    private bool CanSaveEdits() => HasSelectedRow && IsEditDirty;

    private async Task SaveEditsAsync(
        ModRowViewModel row,
        string wantedName,
        string wantedFolder,
        string wantedUrl,
        ModDetails wantedDetails,
        CancellationToken cancellationToken)
    {
        var renaming = !string.Equals(wantedFolder, row.BareFolderName, StringComparison.Ordinal);
        var relabelling = !string.Equals(wantedName, row.DisplayName, StringComparison.Ordinal);

        if (renaming)
        {
            var proposal = await _modFiles
                .ProposeFolderNameAsync(row.Path, wantedFolder, cancellationToken)
                .ConfigureAwait(true);

            if (!proposal.IsFree)
            {
                Offer(
                    nameof(Strings.CharacterDetail_Edit_FolderTaken),
                    proposal,
                    name => EditFolderName = name);

                return;
            }
        }

        if (relabelling)
        {
            var proposal = ModNames.ProposeAmong(
                wantedName,
                Rows.Where(other => !PathComparer.AreEqual(other.Path, row.Path))
                    .Select(other => other.DisplayName));

            if (!proposal.IsFree)
            {
                Offer(
                    nameof(Strings.CharacterDetail_Edit_NameTaken),
                    proposal,
                    name => EditName = name);

                return;
            }
        }

        // The keys and the defaults first, while the folder is where they were read from.
        await KeySwaps.SaveAsync(cancellationToken).ConfigureAwait(true);
        await SavedSettings.SaveAsync(cancellationToken).ConfigureAwait(true);

        var path = row.Path;

        if (renaming)
        {
            var renamed = await _modFiles
                .RenameAsync(path, wantedFolder, cancellationToken).ConfigureAwait(true);

            path = renamed.ToPath;
        }

        // A label equal to the folder is dropped: that is what no custom name looks like.
        var label = string.Equals(wantedName, wantedFolder, StringComparison.Ordinal)
            ? null
            : wantedName;

        var url = wantedUrl.Length == 0 ? null : wantedUrl;

        var redescribing = wantedDetails != new ModDetails(row.Author, row.Version, row.Description, row.Notes);

        if (relabelling ||
            redescribing ||
            !string.Equals(url, row.ModUrl, StringComparison.Ordinal))
        {
            await _modConfigs
                .UpdateAsync(
                    path,
                    config => config.WithModUrl(url) with
                    {
                        CustomName = label,
                        Author = wantedDetails.Author,
                        Version = wantedDetails.Version,
                        Description = wantedDetails.Description,
                        Notes = wantedDetails.Notes,
                    },
                    cancellationToken)
                .ConfigureAwait(true);
        }

        _reselectPath = path;

        await _rescan(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Shows the clash, writes the free name into the box being typed in, and renames nothing.</summary>
    private void Offer(string noticeKey, ModNameProposal proposal, Action<string> prefill)
    {
        EditNotice = Text.Format(noticeKey, proposal.CollidesWith ?? proposal.Wanted, proposal.Name);
        prefill(proposal.Name);
    }

    /// <summary>Brings the edit boxes back into line with the selected mod.</summary>
    private void ResetEdits()
    {
        EditNotice = null;
        EditName = SelectedRow?.DisplayName ?? string.Empty;
        EditFolderName = SelectedRow?.BareFolderName ?? string.Empty;
        EditModUrl = SelectedRow?.ModUrl ?? string.Empty;
        EditAuthor = SelectedRow?.Author ?? string.Empty;
        EditVersion = SelectedRow?.Version ?? string.Empty;
        EditDescription = SelectedRow?.Description ?? string.Empty;
        EditNotes = SelectedRow?.Notes ?? string.Empty;
        KeySwaps.Revert();
        SavedSettings.Revert();
    }

    /// <summary>The four details the pane edits beside the name and address, as stored.</summary>
    private sealed record ModDetails(string? Author, string? Version, string? Description, string? Notes);

    private Task ToggleAsync(IReadOnlyList<ModRowViewModel> rows)
    {
        if (rows.Count == 0)
        {
            return Task.CompletedTask;
        }

        var enable = rows.Any(row => !row.IsEnabled);

        return _runner.RunAsync(
            Heading,
            async ct =>
            {
                foreach (var row in rows)
                {
                    await _modFiles.SetEnabledAsync(row.Path, enable, ct).ConfigureAwait(true);
                }

                await _rescan(ct).ConfigureAwait(true);
            },
            ActivationToken);
    }

    /// <summary>Asks to delete the selected mods, confirmed in place.</summary>
    [RelayCommand]
    private void DeleteSelected() => AskToDelete(_selectedRows);

    /// <summary>Asks to delete one row from its context menu.</summary>
    [RelayCommand]
    private void DeleteRow(ModRowViewModel? row) => AskToDelete(TargetRows(row));

    private void AskToDelete(IReadOnlyList<ModRowViewModel> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        PendingConfirmationMessage = rows.Count == 1
            ? Text.Format(nameof(Strings.CharacterDetail_Delete_ConfirmOne), rows[0].DisplayName)
            : Text.Format(nameof(Strings.CharacterDetail_Delete_ConfirmMany), rows.Count);

        _pendingConfirmation = ct => DeleteAsync(rows, ct);
    }

    /// <summary>Moves mods to the trash and says so with Undo; one that cannot go does not stop the rest.</summary>
    private async Task DeleteAsync(IReadOnlyList<ModRowViewModel> rows, CancellationToken cancellationToken)
    {
        if (_game.ModsDirectory is not { Length: > 0 } modsDirectory)
        {
            return;
        }

        var trashed = new List<TrashResult>();

        foreach (var row in rows)
        {
            try
            {
                var result = await _modFiles.DeleteAsync(row.Path, modsDirectory, cancellationToken).ConfigureAwait(true);

                if (result.Trash is { } record)
                {
                    trashed.Add(record);
                }
            }
            catch (ModOperationException exception)
            {
                _notifications.Add(NotificationSeverity.Error, row.DisplayName, exception.Message);
            }
        }

        await _rescan(cancellationToken).ConfigureAwait(true);

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
            Text[nameof(Strings.CharacterDetail_Deleted_Title)],
            Text.Format(nameof(Strings.CharacterDetail_Deleted), Text.Mods(trashed.Count)),
            action: undo,
            actionText: Text[nameof(Strings.Notifications_Undo)]);
    }

    /// <summary>Puts deleted mods back; what could not come back stays for a second try.</summary>
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
                    await _modFiles.RestoreAsync(record, ct).ConfigureAwait(true);
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
                    ? Text.ForCount(skipped.Count, nameof(Strings.CharacterDetail_Restored_WithSkips_One), nameof(Strings.CharacterDetail_Restored_WithSkips), Text.Mods(restored), Text.Mods(skipped.Count))
                    : Text.Format(nameof(Strings.CharacterDetail_Restored), Text.Mods(restored)));

            foreach (var (path, reason) in skipped)
            {
                _notifications.Add(NotificationSeverity.Warning, path, reason);
            }
        },
        CancellationToken.None);

    // A mod's picture

    /// <summary>Chooses a picture for the selected mod from the file picker. The pen over the thumbnail.</summary>
    [RelayCommand]
    private Task ChooseModImageAsync() => SelectedRow is not { } row
        ? Task.CompletedTask
        : _runner.RunAsync(
            Heading,
            async ct =>
            {
                var picked = await _picker
                    .PickFileAsync(
                        Text[nameof(Strings.ModImage_PickerTitle)],
                        [new FileTypeFilter(Text[nameof(Strings.ModImage_FileType)], [.. IModPreviewEditor.SupportedExtensions])],
                        cancellationToken: ct)
                    .ConfigureAwait(true);

                if (picked is { Length: > 0 })
                {
                    await SetModImageAsync(row, PreviewImageSource.FromFile(picked), ct).ConfigureAwait(true);
                }
            },
            ActivationToken);

    /// <summary>Uses the picture on the clipboard for the selected mod: Ctrl+V, or the right-click menu.</summary>
    [RelayCommand]
    private Task PasteModImageAsync() => SelectedRow is not { } row
        ? Task.CompletedTask
        : _runner.RunAsync(
            Heading,
            async ct =>
            {
                if (await _clipboard.ReadAsync(ct).ConfigureAwait(true) is not { } image)
                {
                    _notifications.Add(NotificationSeverity.Warning, Heading, Text[nameof(Strings.ModImage_NothingToPaste)]);
                    return;
                }

                await SetModImageAsync(row, image, ct).ConfigureAwait(true);
            },
            ActivationToken);

    /// <summary>Uses an image file dropped onto the selected mod's picture.</summary>
    public Task DropModImageAsync(string path) =>
        string.IsNullOrWhiteSpace(path) ? Task.CompletedTask : DropModImageAsync(PictureDrop.FromFile(path));

    /// <summary>Uses a picture dropped onto the selected mod's picture, fetching a web one first.</summary>
    /// <param name="drop">What the drop carried.</param>
    /// <returns>A task that completes when the picture is set and shown.</returns>
    public Task DropModImageAsync(PictureDrop drop) =>
        SelectedRow is not { } row ? Task.CompletedTask : DropModImageAsync(row, drop);

    /// <summary>Uses a picture dropped onto a mod's row as its picture, then selects the row.</summary>
    /// <param name="row">The mod it was dropped on.</param>
    /// <param name="drop">What the drop carried.</param>
    /// <returns>A task that completes when the picture is set and shown.</returns>
    public Task DropModImageAsync(ModRowViewModel row, PictureDrop drop)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(drop);

        return _runner.RunAsync(
            Heading,
            async ct => await SetModImageAsync(row, await drop.ToSourceAsync(_downloader, Text, ct).ConfigureAwait(true), ct)
                .ConfigureAwait(true),
            ActivationToken);
    }

    /// <summary>Removes the selected mod's picture; only XXSM's own stored copy goes to the trash.</summary>
    [RelayCommand(CanExecute = nameof(HasPreviewImage))]
    private Task ClearModImageAsync() => SelectedRow is not { } row
        ? Task.CompletedTask
        : _runner.RunAsync(
            Heading,
            async ct =>
            {
                if (!await _pictures.ClearAsync(row.Path, ct).ConfigureAwait(true))
                {
                    return;
                }

                // Forget what is on screen, or the pane keeps showing the old bitmap for the same mod.
                _reselectPath = row.Path;
                _previewFor = null;

                await _rescan(ct).ConfigureAwait(true);
            },
            ActivationToken);

    private async Task SetModImageAsync(ModRowViewModel row, PreviewImageSource image, CancellationToken cancellationToken)
    {
        if (image.FilePath is { } file && !IModPreviewEditor.IsSupportedImage(file))
        {
            _notifications.Add(NotificationSeverity.Warning, Heading, Text.Format(nameof(Strings.ModImage_Unsupported), PathDisplay.Show(file)));
            return;
        }

        await _pictures.SetAsync(row.Path, image, cancellationToken).ConfigureAwait(true);

        // Forget what is on screen, or the pane never looks again for the same mod.
        _reselectPath = row.Path;
        _previewFor = null;

        await _rescan(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Runs whatever <see cref="PendingConfirmationMessage"/> describes.</summary>
    [RelayCommand]
    private Task ConfirmPendingAsync()
    {
        var action = _pendingConfirmation;
        PendingConfirmationMessage = null;
        _pendingConfirmation = null;

        return action is null
            ? Task.CompletedTask
            : _runner.RunAsync(Heading, action, ActivationToken);
    }

    /// <summary>Backs out of whatever <see cref="PendingConfirmationMessage"/> describes.</summary>
    [RelayCommand]
    private void CancelPending()
    {
        PendingConfirmationMessage = null;
        _pendingConfirmation = null;
    }

    /// <summary>Opens the "Move to…" picker for the current selection.</summary>
    [RelayCommand]
    private void OpenMoveTargetPicker() => OpenMoveTargetPicker(_selectedRows);

    /// <summary>Opens the "Move to…" picker for one row from its context menu.</summary>
    [RelayCommand]
    private void OpenMoveTargetPickerForRow(ModRowViewModel? row) => OpenMoveTargetPicker(TargetRows(row));

    private void OpenMoveTargetPicker(IReadOnlyList<ModRowViewModel> rows)
    {
        if (rows.Count == 0 || _game.Data is not { } data)
        {
            return;
        }

        _movingRows = rows;

        // Only the character these mods are all under is left out, not its family.
        bool AlreadyUnder(MergedVariant variant) =>
            rows.All(row => PathComparer.AreNamesEqual(row.Mod.VariantFolderName, variant.ModFilesName));

        var byName = FolderLookup(_game.Inventory);

        // Others first, unless every one of these is in Others already.
        var others = rows.All(row => PathComparer.AreNamesEqual(row.Mod.VariantFolderName, ModsFolderLayout.UnsortedFolderName))
            ? []
            : new[]
            {
                MoveTargetViewModel.Others(
                    Text[nameof(Strings.Characters_Others)],
                    byName.GetValueOrDefault(ModsFolderLayout.UnsortedFolderName)?.Path),
            };

        MovePicker.Open(
            Text[nameof(Strings.CharacterDetail_MoveTo)],
            [
                .. others,
                .. data.VisibleVariants
                    .Where(variant => !AlreadyUnder(variant))
                    .Select(variant => new MoveTargetViewModel(
                        variant, byName.GetValueOrDefault(variant.ModFilesName)?.Path))
                    .OrderBy(target => target.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            ],
            ConfirmMoveAsync);
    }

    /// <summary>Moves the mods the picker was opened for to the chosen character.</summary>
    private Task ConfirmMoveAsync(MoveTargetViewModel target)
    {
        if (_movingRows.Count == 0 || _game.ModsDirectory is not { Length: > 0 } modsDirectory)
        {
            return Task.CompletedTask;
        }

        var rows = _movingRows;
        var destination = target.ExistingFolderPath ?? PathComparer.Join(modsDirectory, target.ModFilesName);

        return _runner.RunAsync(
            Heading,
            async ct =>
            {
                var moved = new List<ModFilingResult>();

                foreach (var row in rows)
                {
                    // One mod that cannot move does not stop the rest.
                    try
                    {
                        // Remembered as well as moved, or the next auto-sort would move it back.
                        var filed = await _filing
                            .MoveAsync(row.Path, destination, target.InternalName, ct)
                            .ConfigureAwait(true);

                        moved.Add(filed);

                        if (filed.RememberError is { } error)
                        {
                            _notifications.Add(
                                NotificationSeverity.Warning,
                                Heading,
                                Text.Format(nameof(Strings.CharacterDetail_MoveTo_NotRemembered), row.DisplayName, error));
                        }
                    }
                    catch (ModOperationException exception)
                    {
                        _notifications.Add(NotificationSeverity.Error, row.DisplayName, exception.Message);
                    }
                }

                await _rescan(ct).ConfigureAwait(true);

                if (moved.Count > 0)
                {
                    OfferMoveBack(moved, target.DisplayName);
                }
            },
            ActivationToken);
    }

    /// <summary>Says what <em>Move to…</em> did, with an offer to put it back.</summary>
    private void OfferMoveBack(List<ModFilingResult> moved, string characterName)
    {
        Notification? notice = null;

        var undo = new AsyncRelayCommand(async () =>
        {
            await MoveBackAsync(moved).ConfigureAwait(true);

            if (moved.Count == 0 && notice is not null)
            {
                _notifications.Dismiss(notice);
            }
        });

        notice = _notifications.Add(
            NotificationSeverity.Information,
            Text[nameof(Strings.CharacterDetail_MoveTo)],
            Text.Format(nameof(Strings.CharacterDetail_Moved), Text.Mods(moved.Count), characterName),
            action: undo,
            actionText: Text[nameof(Strings.Notifications_Undo)]);
    }

    /// <summary>Puts mods moved by hand back, newest first; what could not go back stays for a second try.</summary>
    private Task<bool> MoveBackAsync(List<ModFilingResult> pending) => _runner.RunAsync(
        Text[nameof(Strings.Notifications_Undo)],
        async ct =>
        {
            var restored = 0;
            var skipped = new List<(string Name, string Reason)>();

            foreach (var filed in Enumerable.Reverse(pending).ToList())
            {
                try
                {
                    await _filing
                        .UndoMoveAsync(filed.Move.ToPath, filed.Move.FromPath, filed.PreviousFiling, ct)
                        .ConfigureAwait(true);

                    pending.Remove(filed);
                    restored++;
                }
                catch (ModOperationException exception)
                {
                    skipped.Add((filed.Move.ToName, exception.Message));
                }
            }

            await _rescan(ct).ConfigureAwait(true);

            _notifications.Add(
                skipped.Count > 0 ? NotificationSeverity.Warning : NotificationSeverity.Information,
                Text[nameof(Strings.Notifications_Undo)],
                skipped.Count > 0
                    ? Text.ForCount(skipped.Count, nameof(Strings.CharacterDetail_MovedBack_WithSkips_One), nameof(Strings.CharacterDetail_MovedBack_WithSkips), Text.Mods(restored), Text.Mods(skipped.Count))
                    : Text.ForCount(restored, nameof(Strings.CharacterDetail_MovedBack_One), nameof(Strings.CharacterDetail_MovedBack), Text.Mods(restored)));

            foreach (var (name, reason) in skipped)
            {
                _notifications.Add(NotificationSeverity.Warning, name, reason);
            }
        },
        CancellationToken.None);

    /// <summary>Forgets that these mods were filed by hand, and shows where auto-sort would put them.</summary>
    /// <param name="row">The row that was right-clicked.</param>
    /// <returns>A task that completes when the review is on screen.</returns>
    [RelayCommand]
    private Task LetAutoSortDecideForRowAsync(ModRowViewModel? row)
    {
        var rows = TargetRows(row).Where(target => target.IsFiledByYou).ToList();

        if (rows.Count == 0 || _character is not { } character)
        {
            return Task.CompletedTask;
        }

        return _runner.RunAsync(
            Heading,
            async ct =>
            {
                foreach (var target in rows)
                {
                    await _filing.LetAutoSortDecideAsync(target.Path, ct).ConfigureAwait(true);
                }

                await _rescan(ct).ConfigureAwait(true);
                await OpenSortReviewAsync(character, ct).ConfigureAwait(true);
            },
            ActivationToken);
    }

    private Task OpenSortReviewAsync(CharacterTileViewModel character, CancellationToken cancellationToken) =>
        character switch
        {
            { IsUnsorted: true } => _sortReview.OpenUnsortedAsync(cancellationToken),
            { IsOthers: true } => _sortReview.OpenOthersAsync(cancellationToken),
            _ => _sortReview.OpenAsync(character.InternalName, character.DisplayName, cancellationToken),
        };

    /// <summary>The install confirm step, shown over the mod grid.</summary>
    public ModInstallViewModel Install => _install;

    /// <summary>The sort preview, narrowed to this character.</summary>
    public SortReviewViewModel SortReview => _sortReview;

    /// <summary>Whether something is being dragged across the mod grid.</summary>
    [ObservableProperty]
    private bool _isDragOver;

    /// <summary>Whether anything is covering the page, so the scrim behind it should be drawn.</summary>
    public bool IsAnyPanelOpen =>
        _install.IsOpen
        || _sortReview.IsOpen
        || MovePicker.IsOpen
        || PendingConfirmationMessage is not null
        || _manager.IsOpen
        || _fill.IsOpen
        || _update.IsOpen;

    /// <summary>Reads a folder or archive dropped here, and shows what it would install into this character.</summary>
    /// <param name="source">The folder or archive that was dropped.</param>
    /// <returns>A task that completes when the confirm step is on screen.</returns>
    public Task DropAsync(string source)
    {
        IsDragOver = false;

        return string.IsNullOrWhiteSpace(source) || _character is null
            ? Task.CompletedTask
            : _install.OpenAsync(source, InstallTarget.Id, InstallTarget.Name, ActivationToken);
    }

    /// <summary>The character an install here goes into; none for Unsorted and Others.</summary>
    private (string? Id, string? Name) InstallTarget =>
        _character is null || _character.IsPlace ? (null, null) : (_character.InternalName, _character.DisplayName);

    /// <summary>Installs a mod folder, or a folder of mods, that the user picks under this character.</summary>
    [RelayCommand]
    private Task AddModFromFolderAsync() =>
        _character is null || _game.ModsDirectory is not { Length: > 0 }
            ? Task.CompletedTask
            : _runner.RunAsync(
                Heading, ct => _install.PickFolderAsync(InstallTarget.Id, InstallTarget.Name, ct), ActivationToken);

    /// <summary>Installs a mod archive the user picks under this character.</summary>
    [RelayCommand]
    private Task AddModFromArchiveAsync() =>
        _character is null || _game.ModsDirectory is not { Length: > 0 }
            ? Task.CompletedTask
            : _runner.RunAsync(
                Heading, ct => _install.PickArchiveAsync(InstallTarget.Id, InstallTarget.Name, ct), ActivationToken);

    // GameBanana

    /// <summary>The panel that shows a page's fields beside the mod's own.</summary>
    public GameBananaFillViewModel GameBananaFill => _fill;

    /// <summary>"Update this mod?"</summary>
    public ModUpdateViewModel ModUpdate => _update;

    /// <summary>Downloads the selected mod's newer version and shows what it changes, to confirm.</summary>
    [RelayCommand]
    private Task UpdateModAsync() =>
        SelectedRow is { } row ? _update.OpenAsync(row.Path) : Task.CompletedTask;

    /// <summary>Whether <em>Add mod</em> should offer <em>From GameBanana…</em>.</summary>
    [ObservableProperty]
    private bool _canAddFromGameBanana;

    /// <summary>Whether a paste on this page should start a look-up.</summary>
    private bool _pasteStartsLookup;

    /// <summary>Whether looking a mod up from its address box is offered at all.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFetchMod))]
    private bool _canFetchModDetails;

    /// <summary>Whether the update mark is shown; off with GameBanana, but not when it is unreachable.</summary>
    [ObservableProperty]
    private bool _showsUpdates;

    /// <summary>Whether the selected mod is linked to GameBanana, in words, and what saving does; or null.</summary>
    public string? GameBananaLinkText
    {
        get
        {
            if (SelectedRow is not { } row)
            {
                return null;
            }

            var typed = EditModUrl.Trim();

            if (!string.Equals(typed, row.ModUrl ?? string.Empty, StringComparison.Ordinal))
            {
                var typedId = GameBananaUrl.FindModId(typed);

                if (typedId is not null && typedId != row.GameBananaModId)
                {
                    return Text[nameof(Strings.CharacterDetail_Link_WillLink)];
                }

                if (typedId is null && row.IsLinkedToGameBanana)
                {
                    return Text[nameof(Strings.CharacterDetail_Link_WillUnlink)];
                }
            }

            return !row.IsLinkedToGameBanana
                ? Text[nameof(Strings.CharacterDetail_Link_None)]
                : row.LastCheckedText is { } checkedOn
                    ? Text.Format(nameof(Strings.CharacterDetail_Link_Checked), checkedOn)
                    : Text[nameof(Strings.CharacterDetail_Link_Linked)];
        }
    }

    /// <summary>Whether a row's menu offers <em>Pretend this mod has an update</em>: developer tools only.</summary>
    [ObservableProperty]
    private bool _canPretendUpdate;

    /// <summary>Marks a mod as having an update without asking GameBanana, to see the mark.</summary>
    /// <param name="row">The row right-clicked.</param>
    /// <returns>A task that completes when the mark is on screen, or the reason it is not is.</returns>
    [RelayCommand]
    private async Task PretendUpdateForRowAsync(ModRowViewModel? row)
    {
        if (row is null || !CanPretendUpdate)
        {
            return;
        }

        await _runner.RunAsync(
            Heading,
            async ct =>
            {
                var marked = await _updates
                    .PretendAsync(row.Path, Text[nameof(Strings.CharacterDetail_PretendUpdate_Version)], ct)
                    .ConfigureAwait(true);

                if (!marked)
                {
                    _notifications.Add(
                        NotificationSeverity.Information,
                        Text[nameof(Strings.CharacterDetail_PretendUpdate)],
                        Text.Format(nameof(Strings.CharacterDetail_PretendUpdate_NotLinked), row.DisplayName));

                    return;
                }

                _reselectPath = row.Path;
                await _rescan(ct).ConfigureAwait(true);
            },
            ActivationToken).ConfigureAwait(true);
    }

    /// <summary>Whether the selected mod's address is one that can be read.</summary>
    public bool CanFetchMod =>
        CanFetchModDetails && GameBananaUrl.FindModId(EditModUrl) is not null;

    /// <summary>Opens the install panel asking for a GameBanana address, rather than reading the clipboard.</summary>
    [RelayCommand]
    private void AddModFromGameBanana()
    {
        if (_character is null)
        {
            return;
        }

        if (_character.IsPlace)
        {
            _install.AskForAddress(null, null);
        }
        else
        {
            _install.AskForAddress(_character.InternalName, _character.DisplayName);
        }
    }

    /// <summary>Reads the mod's page and does with it whatever the paste setting says.</summary>
    /// <returns>A task that completes when the panel is up, or the writing is done.</returns>
    [RelayCommand]
    private async Task FetchModDetailsAsync()
    {
        if (SelectedRow is not { } row || GameBananaUrl.FindModId(EditModUrl) is not { } modId)
        {
            return;
        }

        await _runner.RunAsync(
            Text[nameof(Strings.GameBanana_Fill_Heading)],
            ct => _fill.LinkAsync(row.Path, modId, ct),
            ActivationToken).ConfigureAwait(true);
    }

    /// <summary>A paste on the page with nothing focused: a GameBanana address opens the prompt for here.</summary>
    /// <returns>A task that completes when the address has been read, or refused.</returns>
    public Task PasteAddressAsync()
    {
        if (!_pasteStartsLookup || !CanAddFromGameBanana)
        {
            return Task.CompletedTask;
        }

        var target = _character is null or { IsPlace: true } ? null : _character;

        return _install.PasteAddressAsync(target?.InternalName, target?.DisplayName, ActivationToken);
    }

    /// <summary>Agrees that the selected mod's installed version is current, clearing the mark.</summary>
    [RelayCommand]
    private async Task DismissUpdateAsync()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        await _runner.RunAsync(
            Heading,
            async ct =>
            {
                await _updates.AcceptAsync(row.Path, ct).ConfigureAwait(true);
                await _rescan(ct).ConfigureAwait(true);
            },
            ActivationToken).ConfigureAwait(true);
    }

    /// <summary>Reads what XXSM is allowed to do with GameBanana, for the buttons that depend on it.</summary>
    private async Task ReadGameBananaSettingsAsync()
    {
        var settings = await _settings.ReadAsync(ActivationToken).ConfigureAwait(true);
        var gameBanana = settings.GameBanana;

        CanAddFromGameBanana = gameBanana.Enabled && gameBanana.AddFromUrlOrDefault;
        _pasteStartsLookup = gameBanana.Enabled && gameBanana.PasteStartsLookupOrDefault;
        CanFetchModDetails = gameBanana.Enabled;
        CanPretendUpdate = gameBanana.Enabled && gameBanana.DeveloperTools;
        ShowsUpdates = gameBanana.Enabled;
    }

    /// <summary>Shows what auto-sort would do for this character, in both directions.</summary>
    [RelayCommand]
    private Task SortThisCharacterAsync() =>
        _character is null
            ? Task.CompletedTask
            : OpenSortReviewAsync(_character, ActivationToken);

    /// <summary>Tells the view model which rows the grid's selection holds; it cannot be bound.</summary>
    /// <param name="rows">Every currently selected row.</param>
    public void UpdateSelection(IReadOnlyList<ModRowViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        _selectedRows = rows;

        foreach (var row in Rows)
        {
            row.IsTicked = rows.Contains(row);
        }

        RaiseSelectionChanged();
    }

    /// <inheritdoc />
    protected override void OnActivated()
    {
        _game.PropertyChanged += OnGameChanged;

        Track(LoadProfilesAsync(ActivationToken));

        // Shared singletons: heard while on screen and let go of again, or they keep the page alive.
        _install.PropertyChanged += OnPanelChanged;
        _sortReview.PropertyChanged += OnPanelChanged;
        _manager.PropertyChanged += OnPanelChanged;
        _fill.PropertyChanged += OnPanelChanged;
        _update.PropertyChanged += OnPanelChanged;
        MovePicker.PropertyChanged += OnPanelChanged;

        Track(ReadGameBananaSettingsAsync());
    }

    private void OnPanelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ModInstallViewModel.IsOpen) or nameof(SortReviewViewModel.IsOpen)
            or nameof(GameBananaFillViewModel.IsOpen))
        {
            OnPropertyChanged(nameof(IsAnyPanelOpen));
        }
    }

    /// <inheritdoc />
    protected override void OnDeactivated()
    {
        _game.PropertyChanged -= OnGameChanged;
        _install.PropertyChanged -= OnPanelChanged;
        _sortReview.PropertyChanged -= OnPanelChanged;
        _manager.PropertyChanged -= OnPanelChanged;
        _fill.PropertyChanged -= OnPanelChanged;
        _update.PropertyChanged -= OnPanelChanged;
        MovePicker.PropertyChanged -= OnPanelChanged;
        _fill.Close();
        _update.Close();
        _manager.Close();
        ReleaseSkinPortraits();
        _portraitHandle?.Dispose();
        _portraitHandle = null;
        Portrait = null;

        _previewLoad?.Cancel();
        _previewLoad?.Dispose();
        _previewLoad = null;
        PreviewImage = null;
        _previewLease?.Dispose();
        _previewLease = null;
        _previewFor = null;
    }

    partial void OnSearchTextChanged(string value) => RebuildRows();

    partial void OnStatusFilterChanged(ModStatusFilter value)
    {
        OnPropertyChanged(nameof(IsStatusFiltered));
        OnPropertyChanged(nameof(StatusFilterText));
        RebuildRows();
    }

    partial void OnSortDescendingChanged(bool value) => RebuildRows();

    partial void OnSortModeChanged(ModSortMode value) => RebuildRows();

    partial void OnSelectedSkinFilterChanged(SkinFilterViewModel? value)
    {
        OnPropertyChanged(nameof(IsShowingAllSkins));
        RaiseHeaderPortraitChanged();
        RebuildRows();
    }

    /// <summary>Keeps the selection in step when the view model moved the grid's current row, after a rescan.</summary>
    /// <remarks>Gated on purpose: when the grid drives, answering again would collapse a multi-row selection.</remarks>
    partial void OnCurrentRowChanged(ModRowViewModel? value)
    {
        if (_restoringSelection)
        {
            UpdateSelection(value is null ? [] : [value]);
        }
    }

    partial void OnPreviewImageChanged(Bitmap? value) => ClearModImageCommand.NotifyCanExecuteChanged();

    partial void OnPendingConfirmationMessageChanged(string? value) =>
        OnPropertyChanged(nameof(IsAnyPanelOpen));

    private void OnGameChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameContext.Inventory))
        {
            RebuildRows();
            Track(LoadProfilesAsync(ActivationToken));
        }
        else if (e.PropertyName == nameof(GameContext.Data))
        {
            RefreshCharacter();
        }
    }

    /// <summary>Reads the character again after the data reloaded, keeping the tab; if gone, to the grid.</summary>
    private void RefreshCharacter()
    {
        if (_character is not { } shown || _game.Data is not { } data)
        {
            return;
        }

        if (shown.IsPlace)
        {
            RebuildRows();
            return;
        }

        var wasShowingAll = IsShowingAllSkins;
        var skin = SelectedSkinFilter?.InternalName;

        if (FindAgain(shown, data) is not { } match)
        {
            _goBack();
            return;
        }

        Show(match);
        ReturnToSkin(wasShowingAll, skin);
    }

    /// <summary>Shows the character this page was left on again, read afresh, as it was left.</summary>
    /// <returns>False when there is nothing to come back to: no character was open, or it is gone.</returns>
    public bool Resume()
    {
        if (_character is not { } shown || _game.Data is not { } data)
        {
            return false;
        }

        if ((shown.IsPlace ? shown : FindAgain(shown, data)) is not { } match)
        {
            return false;
        }

        var wasShowingAll = IsShowingAllSkins;
        var skin = SelectedSkinFilter?.InternalName;
        var search = SearchText;
        var status = StatusFilter;
        var sort = SortMode;

        Show(match);
        ReturnToSkin(wasShowingAll, skin);

        SearchText = search;
        StatusFilter = status;
        SortMode = sort;

        return true;
    }

    /// <summary>Shows whichever page holds a mod, with that mod selected and its skin's tab chosen.</summary>
    /// <param name="modFolder">The mod's own folder, as the last scan found it.</param>
    /// <returns>False when the last scan has no such mod, and nothing changed.</returns>
    public bool ShowMod(string modFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modFolder);

        if (_game.Data is not { } data
            || _game.Inventory is not { } inventory
            || inventory.AllMods.FirstOrDefault(mod => PathComparer.AreEqual(mod.Path, modFolder)) is not { } mod
            || TileHolding(mod, inventory, data) is not { } tile)
        {
            return false;
        }

        // A skin's mod is under that skin's tab, not the base character's.
        var member = tile.Family.FirstOrDefault(variant =>
            string.Equals(variant.ModFilesName, mod.VariantFolderName, StringComparison.OrdinalIgnoreCase));

        Show(tile, mod.Path, member?.InternalName);

        return true;
    }

    /// <summary>The tile a mod is shown under: Unsorted, Others, or its character's as the grid builds it.</summary>
    private CharacterTileViewModel? TileHolding(InstalledMod mod, ModsInventory inventory, GameData data)
    {
        var unsorted = Xxsm.Packs.Sorting.UnsortedMods.Find(inventory, data);

        if (unsorted.Any(loose => PathComparer.AreEqual(loose.Path, mod.Path)))
        {
            return CharacterTileViewModel.Unsorted(unsorted.Count, unsorted.Count(loose => loose.IsEnabled), Text);
        }

        if (Xxsm.Packs.Sorting.UnsortedMods.OthersFolder(inventory, data) is { } others
            && others.Mods.Any(other => PathComparer.AreEqual(other.Path, mod.Path)))
        {
            return CharacterTileViewModel.Others(others.Mods.Count, others.EnabledCount, others.Path, Text);
        }

        var tiles = CharactersPageViewModel.Build(
            data, inventory, _game.SkinDisplayMode, Text, new CharacterGridQuery { IncludeHidden = true });

        var match = tiles.FirstOrDefault(tile => tile.Family.Any(member =>
            string.Equals(member.ModFilesName, mod.VariantFolderName, StringComparison.OrdinalIgnoreCase)));

        foreach (var tile in tiles.Where(tile => !ReferenceEquals(tile, match)))
        {
            tile.Dispose();
        }

        return match;
    }

    /// <summary>The character's tile as the grid would build it now, or null when it is gone.</summary>
    private CharacterTileViewModel? FindAgain(CharacterTileViewModel shown, GameData data)
    {
        var tiles = CharactersPageViewModel.Build(
            data, _game.Inventory, _game.SkinDisplayMode, Text, new CharacterGridQuery { IncludeHidden = true });

        var match = tiles.FirstOrDefault(tile =>
                        string.Equals(tile.InternalName, shown.InternalName, StringComparison.OrdinalIgnoreCase))
                    ?? tiles.FirstOrDefault(tile => tile.Family.Any(member =>
                        string.Equals(member.InternalName, shown.Variant?.InternalName, StringComparison.OrdinalIgnoreCase)));

        foreach (var tile in tiles.Where(tile => !ReferenceEquals(tile, match)))
        {
            tile.Dispose();
        }

        return match;
    }

    /// <summary>Puts back the skin tab the user was on, which <c>Show</c> does not know.</summary>
    private void ReturnToSkin(bool wasShowingAll, string? skin) =>
        SelectedSkinFilter = wasShowingAll
            ? SkinFilters.FirstOrDefault()
            : SkinFilters.FirstOrDefault(filter =>
                  string.Equals(filter.InternalName, skin, StringComparison.OrdinalIgnoreCase))
              ?? SelectedSkinFilter;

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionCountText));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsSelectionBarVisible));
        OnPropertyChanged(nameof(SelectedRow));
        OnPropertyChanged(nameof(HasSelectedRow));
        OnPropertyChanged(nameof(FolderNameNotice));
        OnPropertyChanged(nameof(GameBananaLinkText));
        RaiseProfilesChanged();

        // Exact comparison on purpose: two mods can differ only by case on ext4.
        var same = SelectedRow?.Path is { } path && string.Equals(path, _editing, StringComparison.Ordinal);
        _editing = SelectedRow?.Path;

        if (!same || !IsEditDirty)
        {
            ResetEdits();
        }

        OnPropertyChanged(nameof(IsEditDirty));
        SaveEditsCommand.NotifyCanExecuteChanged();

        UpdatePreviewImage();
        Track(KeySwaps.LoadAsync(SelectedRow?.Path, ActivationToken));
        Track(SavedSettings.LoadAsync(SelectedRow?.Path, ActivationToken));
    }

    /// <summary>Brings <see cref="PreviewImage"/> in line with the selection, keyed on path, not row.</summary>
    private void UpdatePreviewImage()
    {
        var wanted = SelectedRow?.Path;

        if (PathComparer.AreEqual(wanted, _previewFor))
        {
            return;
        }

        _previewLoad?.Cancel();
        _previewLoad?.Dispose();
        _previewLoad = null;

        // Not disposed here on purpose: the cache owns the bitmap; releasing only allows eviction.
        PreviewImage = null;
        _previewLease?.Dispose();
        _previewLease = null;
        _previewFor = wanted;

        if (SelectedRow is not { } row)
        {
            return;
        }

        var load = CancellationTokenSource.CreateLinkedTokenSource(ActivationToken);
        _previewLoad = load;

        Track(LoadPreviewImageAsync(row, load.Token));
    }

    private async Task LoadPreviewImageAsync(ModRowViewModel row, CancellationToken cancellationToken)
    {
        IBitmapLease? lease;

        try
        {
            lease = await _previews.AcquireAsync(row.Mod, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // The selection may have moved on while this was decoding.
        if (cancellationToken.IsCancellationRequested || !PathComparer.AreEqual(_previewFor, row.Path))
        {
            lease?.Dispose();
            return;
        }

        _previewLease = lease;
        PreviewImage = lease?.Bitmap;
    }

    private IReadOnlyList<ModRowViewModel> TargetRows(ModRowViewModel? clicked) =>
        HasSelection ? _selectedRows : clicked is not null ? [clicked] : [];

    private void RebuildRows()
    {
        // Remembered before the rows are thrown away, so the rescan reselects them.
        var reselect = _reselectPath;
        _reselectPath = null;

        // A new path for a row an edit moved; else a key per row, which survives an enable's rename.
        var keep = reselect is null
            ? (IReadOnlyList<string>)[.. _selectedRows.Select(row => row.SelectionKey)]
            : [];

        Rows.Clear();
        _totalModCount = 0;

        if (_character is { IsPlace: true } place && _game.Inventory is { } placeInventory && _game.Data is { } data)
        {
            var mods = place.IsOthers
                ? Xxsm.Packs.Sorting.UnsortedMods.OthersFolder(placeInventory, data)?.Mods ?? []
                : Xxsm.Packs.Sorting.UnsortedMods.Find(placeInventory, data);

            _totalModCount = mods.Count;

            foreach (var mod in Order(mods.Where(Matches)))
            {
                var where = place.IsOthers
                    ? null
                    : mod.VariantFolderName ?? Text[nameof(Strings.CharacterDetail_Unsorted_Loose)];

                Rows.Add(new ModRowViewModel(mod, Text, where));
            }
        }
        else if (_character is not null && _game.Inventory is { } inventory)
        {
            var byName = FolderLookup(inventory);

            var family = SelectedSkinFilter?.InternalName is { } chosen
                ? _character.Family.Where(member => string.Equals(
                    member.InternalName, chosen, StringComparison.OrdinalIgnoreCase))
                : _character.Family;

            var skinOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var folders = new List<VariantFolder>();

            foreach (var member in family)
            {
                if (byName.GetValueOrDefault(member.ModFilesName) is not { } folder ||
                    skinOf.ContainsKey(folder.Name))
                {
                    continue;
                }

                skinOf[folder.Name] = member.DisplayName;
                folders.Add(folder);
            }

            var mods = folders.SelectMany(folder => folder.Mods).ToList();

            _totalModCount = mods.Count;

            var filtered = mods.Where(Matches);

            var ordered = Order(filtered);

            var labelRows = IsShowingAllSkins;

            foreach (var mod in ordered)
            {
                var skin = labelRows && mod.VariantFolderName is { } folderName
                    ? skinOf.GetValueOrDefault(folderName)
                    : null;

                Rows.Add(new ModRowViewModel(mod, Text, skin));
            }
        }

        OnPropertyChanged(nameof(HasNoMods));
        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(HasRows));

        RestoreSelection(reselect, keep);
    }

    /// <summary>Puts the selection back after a rescan, as far as the mods are still there.</summary>
    /// <param name="path">The one row an edit has just moved, by its new path, or null.</param>
    /// <param name="keys">A selection key per picked row, used when <paramref name="path"/> is null.</param>
    private void RestoreSelection(string? path, IReadOnlyList<string> keys)
    {
        var restored = path is { } one
            ? Rows.Where(row => PathComparer.AreEqual(row.Path, one)).ToList()
            : keys.Count == 0
                ? []
                : Rows.Where(row => keys.Any(key => PathComparer.AreEqual(row.SelectionKey, key))).ToList();

        _restoringSelection = true;

        try
        {
            CurrentRow = restored.Count > 0 ? restored[0] : null;
        }
        finally
        {
            _restoringSelection = false;
        }

        UpdateSelection(restored);

        if (restored.Count > 1)
        {
            SelectionRestoreRequested?.Invoke(this, restored);
        }
    }

    /// <summary>Puts the rows in the toolbar's order, by name within it; unknown values sort last either way.</summary>
    private IEnumerable<InstalledMod> Order(IEnumerable<InstalledMod> mods)
    {
        var byName = mods.OrderBy(mod => mod.DisplayName, StringComparer.CurrentCultureIgnoreCase);

        return SortMode switch
        {
            ModSortMode.Status => By(byName, mod => mod.IsEnabled),
            ModSortMode.Author => KnownFirst(
                mod => mod.Config?.Author is { Length: > 0 },
                mod => mod.Config?.Author ?? string.Empty,
                StringComparer.CurrentCultureIgnoreCase),
            ModSortMode.DateAdded => KnownFirst(
                mod => mod.DateAdded is not null, mod => mod.DateAdded ?? default, null),
            ModSortMode.DateModified => KnownFirst(
                mod => mod.LastWriteTimeUtc is not null,
                mod => mod.LastWriteTimeUtc ?? default,
                null),
            _ => SortDescending ? byName.Reverse() : byName,
        };

        // The direction applies to the chosen field; name stays ascending underneath.
        IEnumerable<InstalledMod> By<TKey>(
            IEnumerable<InstalledMod> source,
            Func<InstalledMod, TKey> key,
            IComparer<TKey>? comparer = null)
        {
            var ordered = SortDescending
                ? source.OrderByDescending(key, comparer)
                : source.OrderBy(key, comparer);

            return ordered.ThenBy(mod => mod.DisplayName, StringComparer.CurrentCultureIgnoreCase);
        }

        IEnumerable<InstalledMod> KnownFirst<TKey>(
            Func<InstalledMod, bool> isKnown,
            Func<InstalledMod, TKey> key,
            IComparer<TKey>? comparer)
        {
            var known = byName.Where(isKnown).ToList();
            var unknown = byName.Where(mod => !isKnown(mod));

            return By(known, key, comparer).Concat(unknown);
        }
    }

    private bool Matches(InstalledMod mod)
    {
        if (SearchText is { Length: > 0 } search
            && mod.DisplayName.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) < 0)
        {
            return false;
        }

        return StatusFilter switch
        {
            ModStatusFilter.Enabled => mod.IsEnabled,
            ModStatusFilter.Disabled => !mod.IsEnabled,
            _ => true,
        };
    }

    private static Dictionary<string, VariantFolder> FolderLookup(ModsInventory? inventory)
    {
        var folders = new Dictionary<string, VariantFolder>(StringComparer.OrdinalIgnoreCase);

        if (inventory is null)
        {
            return folders;
        }

        foreach (var folder in inventory.VariantFolders)
        {
            folders[folder.Name] = folder;
        }

        return folders;
    }

    private async Task LoadPortraitAsync(GameData data, MergedVariant variant)
    {
        IBitmapLease? handle;

        try
        {
            handle = await _portraits.AcquireAsync(data, variant, ActivationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Show() may have moved on to a different character while this was decoding.
        if (!ReferenceEquals(_character?.Variant, variant))
        {
            handle?.Dispose();
            return;
        }

        _portraitHandle = handle;
        Portrait = handle?.Bitmap;
    }
}

/// <summary>One tab of the skin selector, or the "All" entry, with a portrait or an initial.</summary>
public sealed partial class SkinFilterViewModel : ObservableObject
{
    /// <summary>Creates a tab.</summary>
    /// <param name="internalName">The skin's id, or null for the "All" entry.</param>
    /// <param name="displayName">The tab's label.</param>
    public SkinFilterViewModel(string? internalName, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        InternalName = internalName;
        DisplayName = displayName;
        Initial = displayName[..1].ToUpper(System.Globalization.CultureInfo.CurrentCulture);
    }

    /// <summary>The skin's id, or null for "All".</summary>
    public string? InternalName { get; }

    /// <summary>The tab's label.</summary>
    public string DisplayName { get; }

    /// <summary>The stand-in shown until the portrait decodes, and for a skin with none.</summary>
    public string Initial { get; }

    /// <summary>Whether this is the "All" entry rather than a real skin.</summary>
    public bool IsAll => InternalName is null;

    /// <summary>The decoded portrait, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPortrait))]
    private Bitmap? _portrait;

    /// <summary>Whether <see cref="Portrait"/> has a bitmap to show.</summary>
    public bool HasPortrait => Portrait is not null;
}
