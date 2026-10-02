using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using Xxsm.Core;
using Xxsm.Core.Diagnostics;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Core.Settings;
using Xxsm.Desktop.Services;
using Xxsm.Packs.Characters;
using Xxsm.Packs.Hashes;
using Xxsm.Packs.Installation;
using Xxsm.Packs.Loading;
using Xxsm.Packs.Merge;
using Xxsm.Packs.Model;
using Xxsm.Packs.Registry;
using Xxsm.Packs.Sorting;
using Xxsm.Packs.Studio;

namespace Xxsm.Desktop.ViewModels.Pages;

/// <summary>A draft on the Studio page's list.</summary>
/// <param name="Summary">What the store knows about it.</param>
/// <param name="SummaryText">"12 characters · version 2026.09.15".</param>
public sealed record StudioDraftChoiceViewModel(StudioDraftSummary Summary, string SummaryText)
{
    /// <summary>The game.</summary>
    public string GameId => Summary.GameId;

    /// <summary>The game's name.</summary>
    public string DisplayName => Summary.DisplayName;

    /// <summary>Why it cannot be read, or null.</summary>
    public string? Error => Summary.Error;

    /// <summary>Whether it can be opened. A broken draft is listed, so its owner can see it, but not opened.</summary>
    public bool CanOpen => Summary.Error is null;

    /// <summary>The draft's own icon, shown before its name.</summary>
    public GameIconViewModel? Icon { get; init; }
}

/// <summary>An installed pack with no draft yet, offered as a starting point.</summary>
public sealed record StudioPackChoiceViewModel(string GameId, string DisplayName, string PackVersion)
{
    /// <summary>The installed pack's icon, shown before its name.</summary>
    public GameIconViewModel? Icon { get; init; }

    /// <summary>The version as the Packs page shows it, <c>26.9.27</c>.</summary>
    public string DisplayVersion => PackVersionText.Display(PackVersion);
}

/// <summary>Pack Studio: the draft list, the new-game wizard, and an open draft's table and Problems panel.</summary>
/// <remarks>Every change is a <see cref="DraftEdits"/> call; the table is rebuilt from the draft after each.</remarks>
public sealed partial class StudioPageViewModel : PageViewModel, IStudioTableEdits
{
    private const int WizardSteps = 3;

    private readonly IStudioDraftStore _store;
    private readonly IPackInstaller _installer;
    private readonly IGamePackLoader _loader;
    private readonly IPackTrial _trial;
    private readonly IHashIgnoreCost _ignoreCosts;
    private readonly IPackExporter _exporter;
    private readonly IPackPublisher _publisher;
    private readonly IFolderLauncher _launcher;
    private readonly CharacterManagerViewModel _manager;
    private readonly IStoragePicker _picker;
    private readonly IClipboardImageReader _clipboard;
    private readonly INotificationService _notifications;
    private readonly ViewModelWorkRunner _runner;
    private readonly TimeProvider _time;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<CancellationToken, Task> _reloadGames;
    private readonly ILogger _logger;
    private readonly IAppSettingsStore _settings;
    private readonly IStudioThumbnailCache _thumbnails;
    private readonly IGameIconProvider _icons;
    private readonly Dictionary<string, StudioPictureViewModel> _pictures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _problemKindFilter = new(StringComparer.Ordinal);

    /// <summary>The character whose problems the panel is narrowed to, from its Problems cell.</summary>
    private string? _problemCharacterFilter;
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);

    private bool _rebuilding;
    private bool _isReplacingAttributeChoices;

    /// <summary>Creates the page.</summary>
    public StudioPageViewModel(
        IStudioDraftStore store,
        IPackInstaller installer,
        IGamePackLoader loader,
        IPackTrial trial,
        IHashIgnoreCost ignoreCosts,
        IPackExporter exporter,
        IPackPublisher publisher,
        IFolderLauncher launcher,
        CharacterManagerViewModel manager,
        StudioImportServices imports,
        IStoragePicker picker,
        IClipboardImageReader clipboard,
        INotificationService notifications,
        ViewModelWorkRunner runner,
        TimeProvider time,
        IUiDispatcher dispatcher,
        ITextCatalogue text,
        IAppSettingsStore settings,
        IStudioThumbnailCache thumbnails,
        IGameIconProvider icons,
        PackInstallFollowUp followUp,
        ColumnWidthMemory columnWidths,
        Func<CancellationToken, Task> reloadGames,
        ILogger logger)
        : base(text)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(trial);
        ArgumentNullException.ThrowIfNull(exporter);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(imports);
        ArgumentNullException.ThrowIfNull(picker);
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(thumbnails);
        ArgumentNullException.ThrowIfNull(icons);
        ArgumentNullException.ThrowIfNull(followUp);
        ArgumentNullException.ThrowIfNull(columnWidths);
        ColumnWidths = columnWidths;
        ArgumentNullException.ThrowIfNull(reloadGames);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _installer = installer;
        _loader = loader;
        _trial = trial;
        _ignoreCosts = ignoreCosts;
        HashList = new HashListEditorViewModel(text);
        _exporter = exporter;
        _publisher = publisher;
        _launcher = launcher;
        _manager = manager;
        _picker = picker;
        _clipboard = clipboard;
        _notifications = notifications;
        _runner = runner;
        _reloadGames = reloadGames;
        _time = time;
        _dispatcher = dispatcher;
        _logger = logger;
        _settings = settings;
        _thumbnails = thumbnails;
        _icons = icons;
        FollowUp = followUp;

        Import = new StudioImportViewModel(
            imports,
            picker,
            text,
            time,
            () => Session,
            work => _runner.RunAsync(Heading, work, ActivationToken),
            RunBusyAsync);

        // A save the user pressed runs to the end even if the page leaves the screen.
        GameSettings = new StudioGameSettingsViewModel(
            store,
            picker,
            text,
            () => Session,
            work => _runner.RunAsync(Heading, work, CancellationToken.None));
    }

    /// <summary>The Game settings panel: the game's details and attributes.</summary>
    public StudioGameSettingsViewModel GameSettings { get; }

    /// <summary>The import panel: every importer's preview, applied to the open draft.</summary>
    public StudioImportViewModel Import { get; }

    /// <summary>The Character Manager, shown over the page while one character of the draft is edited.</summary>
    public CharacterManagerViewModel CharacterManager => _manager;

    /// <summary>Opens one character of the draft in the Character Manager; saving is one Undo step.</summary>
    /// <param name="row">The character.</param>
    [RelayCommand]
    public void EditCharacter(StudioCharacterRowViewModel? row)
    {
        if (Session is not { } session || row is null)
        {
            return;
        }

        try
        {
            _manager.Edit(new DraftCharacterTarget(this, session), row.InternalName);
        }
        catch (ModOperationException ex)
        {
            Refuse(ex.Message);
        }
    }

    /// <summary>Opens the first selected character in the Character Manager.</summary>
    [RelayCommand]
    public void EditSelected() => EditCharacter(SelectedRows.Count > 0 ? SelectedRows[0] : null);

    /// <summary>Opens the import panel for an assets repository.</summary>
    [RelayCommand]
    public Task ImportAssetsAsync() => Import.OpenAsync(StudioImportKind.Assets);

    /// <summary>Opens the import panel for a Mods folder.</summary>
    [RelayCommand]
    public Task ImportModsFolderAsync() => Import.OpenAsync(StudioImportKind.ModsFolder);

    /// <summary>Opens the import panel for a folder of pictures.</summary>
    [RelayCommand]
    public Task ImportPicturesAsync() => Import.OpenAsync(StudioImportKind.Pictures);

    /// <summary>Opens the import panel for a spreadsheet.</summary>
    [RelayCommand]
    public Task ImportSpreadsheetAsync() => Import.OpenAsync(StudioImportKind.Spreadsheet);

    /// <summary>Opens the import panel for the user's own corrections, read at once.</summary>
    [RelayCommand]
    public Task ImportCorrectionsAsync() => Import.OpenAsync(StudioImportKind.Corrections);

    /// <inheritdoc />
    public override string Heading => Text[nameof(Strings.Studio_Heading)];

    // The list of drafts

    /// <summary>Every draft, including any that can no longer be read.</summary>
    public ObservableCollection<StudioDraftChoiceViewModel> Drafts { get; } = [];

    /// <summary>Installed packs that have no draft yet.</summary>
    public ObservableCollection<StudioPackChoiceViewModel> InstalledPacks { get; } = [];

    /// <summary>Whether there are drafts to list.</summary>
    public bool HasDrafts => Drafts.Count > 0;

    /// <summary>Whether there are installed packs to start from.</summary>
    public bool HasInstalledPacks => InstalledPacks.Count > 0;

    /// <summary>Whether the list's empty state should show.</summary>
    public bool IsListEmpty => !IsBusy && Drafts.Count == 0;

    // The open draft

    /// <summary>The open draft, or null while the list is showing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDraftOpen), nameof(IsListShowing), nameof(DraftTitle))]
    private StudioDraftSession? _session;

    /// <summary>Whether a draft is open.</summary>
    public bool IsDraftOpen => Session is not null;

    /// <summary>Whether the list of drafts is showing.</summary>
    public bool IsListShowing => Session is null;

    /// <summary>Whether the open draft has a change not yet on disk.</summary>
    public bool HasUnsavedChanges => Session is { HasUnsavedChanges: true };

    /// <summary>The open draft's game name.</summary>
    public string DraftTitle => Session?.Current.Game.DisplayName ?? string.Empty;

    /// <summary>The open draft's own icon, before its name in the heading; null with no draft open.</summary>
    public GameIconViewModel? DraftIcon => Session is { } session
        ? _icons.Draft(session.Current.GameId, DraftTitle, session.Current.Game.Icon)
        : null;

    /// <summary>The open draft's characters and objects.</summary>
    public ObservableCollection<StudioCharacterRowViewModel> Rows { get; } = [];

    /// <summary>The rows selected in the table.</summary>
    public IReadOnlyList<StudioCharacterRowViewModel> SelectedRows { get; private set; } = [];

    /// <summary>Whether any row is selected.</summary>
    public bool HasSelection => SelectedRows.Count > 0;

    /// <summary>Whether the table shows a tick box on every row and a click on a row picks it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectionBarVisible))]
    private bool _isSelectMode;

    /// <summary>Whether the actions for several characters show: in <em>Select</em>, or with two rows picked.</summary>
    public bool IsSelectionBarVisible => IsSelectMode || SelectedRows.Count > 1;

    /// <summary>Turns <em>Select</em> off again.</summary>
    [RelayCommand]
    public void EndSelectMode() => IsSelectMode = false;

    /// <summary>"3 characters selected".</summary>
    public string SelectionText => Text.Format(nameof(Strings.Studio_Selection), Text.Characters(SelectedRows.Count));

    /// <summary>Characters that are not outfits, which an outfit can be linked to.</summary>
    public IReadOnlyList<string> BaseChoices { get; private set; } = [];

    /// <summary>What <see cref="SetAttribute()"/> can set: the game's attributes, in the game's own order.</summary>
    public IReadOnlyList<StudioAttributeColumn> AttributeChoices { get; private set; } = [];

    /// <summary>Every character, which the Outfit of cell offers as you type.</summary>
    public IReadOnlyList<StudioOutfitChoice> OutfitChoices { get; private set; } = [];

    /// <summary>One table column per attribute; announced only when the columns themselves change.</summary>
    public IReadOnlyList<StudioAttributeColumn> AttributeColumns { get; private set; } = [];

    /// <summary>Whether the game has any attributes to set.</summary>
    public bool HasAttributeChoices => AttributeChoices.Count > 0;

    /// <summary>What the attribute chooser says with nothing chosen: where to add one, for a game with none.</summary>
    public string? AttributePlaceholder => HasAttributeChoices ? null : Text[nameof(Strings.Studio_Bulk_NoAttributes)];

    /// <summary>The name typed for a new character.</summary>
    [ObservableProperty]
    private string _newCharacterName = string.Empty;

    /// <summary>The attribute for <see cref="SetAttribute"/>.</summary>
    [ObservableProperty]
    private StudioAttributeColumn? _bulkAttribute;

    /// <summary>The value typed or picked for <see cref="SetAttribute"/>.</summary>
    [ObservableProperty]
    private string _bulkAttributeValue = string.Empty;

    /// <summary>The values <see cref="BulkAttribute"/> lists, for its drop-down; empty when it is typed.</summary>
    public IReadOnlyList<string> BulkValueChoices => BulkAttribute?.Suggestions ?? [];

    /// <summary>Whether the value is picked from <see cref="BulkValueChoices"/> rather than only typed.</summary>
    public bool HasBulkValueChoices => BulkValueChoices.Count > 0;

    /// <summary>The character <see cref="LinkSelected"/> makes the selection outfits of.</summary>
    [ObservableProperty]
    private string? _bulkBase;

    /// <summary>What goes before each name for <see cref="AddAlias"/>.</summary>
    [ObservableProperty]
    private string _aliasPrefix = string.Empty;

    /// <summary>What goes after each name for <see cref="AddAlias"/>.</summary>
    [ObservableProperty]
    private string _aliasSuffix = string.Empty;

    // The Problems panel

    /// <summary>Every problem with the open draft, errors first.</summary>
    public ObservableCollection<StudioProblemViewModel> Problems { get; } = [];

    /// <summary>The problems the panel lists: all of them, or the kinds picked in <see cref="ProblemKinds"/>.</summary>
    public ObservableCollection<StudioProblemViewModel> VisibleProblems { get; } = [];

    /// <summary>One filter chip per kind of problem the draft has; none picked shows everything.</summary>
    public ObservableCollection<StudioProblemKindViewModel> ProblemKinds { get; } = [];

    /// <summary>Whether the filter is worth showing: with one kind of problem there is nothing to narrow.</summary>
    public bool HasProblemKinds => ProblemKinds.Count > 1;

    /// <summary>Whether some problems are hidden by the filter.</summary>
    public bool IsProblemFilterOn => VisibleProblems.Count < Problems.Count;

    /// <summary>"Showing 5 of 145", and which character when the table's Problems cell narrowed it.</summary>
    public string ProblemFilterText =>
        _problemCharacterFilter is { } character
            ? Text.Format(
                nameof(Strings.Studio_Problems_ShowingAbout),
                VisibleProblems.Count,
                Problems.Count,
                RowFor(character)?.DisplayName ?? character)
            : Text.Format(nameof(Strings.Studio_Problems_Showing), VisibleProblems.Count, Problems.Count);

    /// <summary>Whether the Problems panel is folded to a strip, giving the table the room; remembered.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProblemsOver))]
    [NotifyPropertyChangedFor(nameof(IsProblemsArrowInRow))]
    private bool _isProblemsCollapsed;

    /// <summary>A page at least this wide has the Problems panel beside the table; narrower lays it over.</summary>
    public const double ProblemsBesideWidth = 880;

    private bool _problemsFoldedWhenRoomy;

    /// <summary>Whether the page is narrower than <see cref="ProblemsBesideWidth"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProblemsOver))]
    [NotifyPropertyChangedFor(nameof(IsProblemsArrowInRow))]
    private bool _isPageNarrow;

    /// <summary>The Problems panel is open over the table rather than beside it.</summary>
    public bool IsProblemsOver => IsPageNarrow && !IsProblemsCollapsed;

    /// <summary>Whether the folded panel's arrow keeps its place in the button row, so the row never jumps.</summary>
    public bool IsProblemsArrowInRow => IsPageNarrow || IsProblemsCollapsed;

    /// <summary>The page says how wide it is; where the Problems panel goes follows.</summary>
    public void SetPageWidth(double width) => IsPageNarrow = width < ProblemsBesideWidth;

    /// <summary>Narrowing folds the panel; widening brings back whatever was chosen with the room to have it.</summary>
    partial void OnIsPageNarrowChanged(bool value) => IsProblemsCollapsed = value || _problemsFoldedWhenRoomy;

    /// <summary>The count on the folded panel's arrow: the errors when there are any, else the warnings.</summary>
    public string ProblemBadgeText => ErrorCount > 0
        ? ErrorCount.ToString(System.Globalization.CultureInfo.CurrentCulture)
        : WarningCount > 0 ? WarningCount.ToString(System.Globalization.CultureInfo.CurrentCulture) : string.Empty;

    /// <summary>Whether the folded panel's arrow carries a count.</summary>
    public bool HasProblemBadge => ErrorCount + WarningCount > 0;

    /// <summary>Whether that count is of errors, and so drawn red rather than amber.</summary>
    public bool IsProblemBadgeError => ErrorCount > 0;

    /// <summary>Folds the Problems panel away or opens it, remembered only with the room to have it beside.</summary>
    [RelayCommand]
    public void ToggleProblems()
    {
        IsProblemsCollapsed = !IsProblemsCollapsed;

        if (IsPageNarrow)
        {
            return;
        }

        _problemsFoldedWhenRoomy = IsProblemsCollapsed;
        var collapsed = IsProblemsCollapsed;

        // Runs to the end whether or not the page stays on screen: it is one small write.
        Track(_runner.RunAsync(
            Heading,
            ct => _settings.UpdateAsync(settings => settings with { StudioProblemsCollapsed = collapsed }, ct),
            CancellationToken.None));
    }

    /// <summary>Narrows the Problems panel to one character's problems, as the row's cell counts them.</summary>
    /// <param name="row">The row whose cell was pressed.</param>
    [RelayCommand]
    public void ShowProblemsFor(StudioCharacterRowViewModel? row)
    {
        if (row is null || (row.ErrorCount + row.WarningCount) == 0)
        {
            return;
        }

        if (IsProblemsCollapsed)
        {
            ToggleProblems();
        }

        _problemKindFilter.Clear();

        foreach (var kind in ProblemKinds)
        {
            kind.SetSelected(false);
        }

        _problemCharacterFilter = row.InternalName;
        Select(row.InternalName);
        ApplyProblemFilter();
    }

    /// <summary>Shows every problem again, whatever kinds were picked and whatever cell was pressed.</summary>
    [RelayCommand]
    public void ClearProblemFilter()
    {
        _problemCharacterFilter = null;
        _problemKindFilter.Clear();

        foreach (var kind in ProblemKinds)
        {
            kind.SetSelected(false);
        }

        ApplyProblemFilter();
    }

    private void OnProblemKindToggled(StudioProblemKindViewModel kind)
    {
        // A kind and one character are two questions; picking a kind answers the other.
        _problemCharacterFilter = null;

        if (kind.IsSelected)
        {
            _problemKindFilter.Add(kind.Id);
        }
        else
        {
            _problemKindFilter.Remove(kind.Id);
        }

        ApplyProblemFilter();
    }

    /// <summary>Fills <see cref="VisibleProblems"/> from the picked kinds still present, never going empty.</summary>
    private void ApplyProblemFilter()
    {
        var picked = ProblemKinds.Where(k => k.IsSelected).Select(k => k.Id).ToHashSet(StringComparer.Ordinal);

        // The character's last problem may be the one just fixed: back to everything, not nothing.
        if (_problemCharacterFilter is { } only && !Problems.Any(p => IsAbout(p, only)))
        {
            _problemCharacterFilter = null;
        }

        VisibleProblems.Clear();
        foreach (var problem in Problems)
        {
            if ((picked.Count == 0 || picked.Contains(problem.Kind))
                && (_problemCharacterFilter is not { } character || IsAbout(problem, character)))
            {
                VisibleProblems.Add(problem);
            }
        }

        OnPropertyChanged(nameof(HasProblemKinds));
        OnPropertyChanged(nameof(IsProblemFilterOn));
        OnPropertyChanged(nameof(ProblemFilterText));
    }

    /// <summary>Whether a problem is the character's own, by subject, as the table's cell counts them.</summary>
    private static bool IsAbout(StudioProblemViewModel problem, string internalName) =>
        string.Equals(problem.Subject, internalName, StringComparison.OrdinalIgnoreCase);

    private void FillProblemKinds()
    {
        ProblemKinds.Clear();

        foreach (var group in Problems
                     .GroupBy(p => p.Kind, StringComparer.Ordinal)
                     .OrderBy(g => PackProblemKinds.All.ToList().IndexOf(g.Key)))
        {
            ProblemKinds.Add(new StudioProblemKindViewModel(
                group.Key,
                ProblemKindLabel(group.Key),
                group.Count(),
                _problemKindFilter.Contains(group.Key),
                OnProblemKindToggled));
        }
    }

    private string ProblemKindLabel(string kind) => kind switch
    {
        PackProblemKinds.NoHashes => Text[nameof(Strings.Studio_ProblemKind_NoHashes)],
        PackProblemKinds.SharedHashes => Text[nameof(Strings.Studio_ProblemKind_SharedHashes)],
        PackProblemKinds.Hashes => Text[nameof(Strings.Studio_ProblemKind_Hashes)],
        PackProblemKinds.Pictures => Text[nameof(Strings.Studio_ProblemKind_Pictures)],
        PackProblemKinds.Names => Text[nameof(Strings.Studio_ProblemKind_Names)],
        PackProblemKinds.Outfits => Text[nameof(Strings.Studio_ProblemKind_Outfits)],
        PackProblemKinds.Attributes => Text[nameof(Strings.Studio_ProblemKind_Attributes)],
        PackProblemKinds.Game => Text[nameof(Strings.Studio_ProblemKind_Game)],
        _ => Text[nameof(Strings.Studio_ProblemKind_Other)],
    };

    /// <summary>How many problems block export.</summary>
    public int ErrorCount { get; private set; }

    /// <summary>How many problems do not.</summary>
    public int WarningCount { get; private set; }

    /// <summary>Whether the draft may be exported. Warnings never stop it.</summary>
    public bool CanExport => ErrorCount == 0;

    /// <summary>Whether there is anything in the panel.</summary>
    public bool HasProblems => Problems.Count > 0;

    /// <summary>"2 errors, 14 warnings", or that there are none.</summary>
    public string ProblemsSummary => HasProblems
        ? Text.Format(nameof(Strings.Studio_Problems_Summary), Text.Errors(ErrorCount), Text.Warnings(WarningCount))
        : Text[nameof(Strings.Studio_Problems_None)];

    // The new-game wizard

    /// <summary>Whether the wizard is showing.</summary>
    [ObservableProperty]
    private bool _isNewGameOpen;

    /// <summary>Which of the wizard's steps is showing, from 1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWizardStep1), nameof(IsWizardStep2), nameof(IsWizardStep3))]
    [NotifyPropertyChangedFor(nameof(WizardStepText), nameof(CanWizardNext), nameof(CanWizardBack))]
    private int _wizardStep = 1;

    /// <summary>The game's name. The only thing required.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreateGame), nameof(CanWizardNext))]
    private string _newGameName = string.Empty;

    /// <summary>A short form of the name.</summary>
    [ObservableProperty]
    private string _newGameShortName = string.Empty;

    /// <summary>The folder name XXMI uses for the game.</summary>
    [ObservableProperty]
    private string _newGameImporter = string.Empty;

    /// <summary>A picture for the game's icon, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNewGameIcon))]
    private string? _newGameIcon;

    /// <summary>The game's Mods folder, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNewGameModsFolder))]
    private string? _newGameModsFolder;

    /// <summary>Whether an icon has been chosen.</summary>
    public bool HasNewGameIcon => NewGameIcon is not null;

    /// <summary>Whether a Mods folder has been chosen.</summary>
    public bool HasNewGameModsFolder => NewGameModsFolder is not null;

    /// <summary>Whether the game can be created: it has a name.</summary>
    public bool CanCreateGame => NewGameName.Trim().Length > 0;

    /// <summary>Whether there is a next step, and the name that gates it has been given.</summary>
    public bool CanWizardNext => WizardStep < WizardSteps && CanCreateGame;

    /// <summary>Whether there is a step before this one.</summary>
    public bool CanWizardBack => WizardStep > 1;

    /// <summary>Whether the name step is showing.</summary>
    public bool IsWizardStep1 => WizardStep == 1;

    /// <summary>Whether the short name and importer step is showing.</summary>
    public bool IsWizardStep2 => WizardStep == 2;

    /// <summary>Whether the icon and Mods folder step is showing.</summary>
    public bool IsWizardStep3 => WizardStep == 3;

    /// <summary>"Step 2 of 3".</summary>
    public string WizardStepText => Text.Format(nameof(Strings.Studio_Wizard_Step), WizardStep, WizardSteps);

    // List commands

    /// <summary>Lists the drafts and the installed packs that could become one.</summary>
    [RelayCommand]
    public Task RefreshAsync() => RunBusyAsync(Text[nameof(Strings.Studio_Loading)], LoadListAsync);

    /// <summary>Opens a draft from the list.</summary>
    [RelayCommand]
    public Task OpenDraftAsync(StudioDraftChoiceViewModel? choice) =>
        choice is not { CanOpen: true }
            ? Task.CompletedTask
            : RunBusyAsync(
                Text[nameof(Strings.Studio_Opening)],
                async ct =>
                {
                    if (await SaveOpenDraftAsync().ConfigureAwait(true))
                    {
                        Open(await _store.ReadAsync(choice.GameId, ct).ConfigureAwait(true));
                    }
                });

    /// <summary>Opens an installed pack as a draft.</summary>
    [RelayCommand]
    public Task OpenPackAsync(StudioPackChoiceViewModel? choice) =>
        choice is null ? Task.CompletedTask : OpenInstalledAsync(choice.GameId);

    /// <summary>Opens a game's draft, starting one from its installed pack when there is none yet.</summary>
    public Task OpenInstalledAsync(string gameId) =>
        RunBusyAsync(
            Text[nameof(Strings.Studio_Opening)],
            async ct =>
            {
                if (!await SaveOpenDraftAsync().ConfigureAwait(true))
                {
                    return;
                }

                if (_store.Exists(gameId))
                {
                    Open(await _store.ReadAsync(gameId, ct).ConfigureAwait(true));
                    return;
                }

                var directory = await _installer.FindActivePackDirectoryAsync(gameId, ct).ConfigureAwait(true)
                                ?? throw new ModOperationException($"There is no pack installed for '{gameId}'.");

                var pack = await _loader.LoadAsync(directory, ct).ConfigureAwait(true);

                Open(await _store.CreateFromPackAsync(pack, ct).ConfigureAwait(true));
            });

    // Deleting a draft

    /// <summary>The draft waiting on its "Delete it?" question, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDeleteDraftOpen), nameof(DeleteDraftQuestion))]
    private StudioDraftChoiceViewModel? _draftToDelete;

    /// <summary>Whether the question is showing.</summary>
    public bool IsDeleteDraftOpen => DraftToDelete is not null;

    /// <summary>"Delete the draft for 'My Game'? …".</summary>
    public string DeleteDraftQuestion => DraftToDelete is { } draft
        ? Text.Format(nameof(Strings.Studio_Delete_Question), draft.DisplayName)
        : string.Empty;

    /// <summary>Asks whether to delete a draft. Nothing is deleted until the answer is yes.</summary>
    /// <param name="choice">The draft. One that cannot be read can be deleted too.</param>
    [RelayCommand]
    public void AskToDeleteDraft(StudioDraftChoiceViewModel? choice)
    {
        if (choice is not null)
        {
            DraftToDelete = choice;
        }
    }

    /// <summary>Backs out of deleting a draft.</summary>
    [RelayCommand]
    public void CancelDeleteDraft() => DraftToDelete = null;

    /// <summary>Saves and closes the draft if open, then moves it to the trash, with an Undo on the notice.</summary>
    [RelayCommand]
    public Task ConfirmDeleteDraftAsync()
    {
        if (DraftToDelete is not { } choice)
        {
            return Task.CompletedTask;
        }

        DraftToDelete = null;

        return RunBusyAsync(
            Text[nameof(Strings.Studio_Deleting)],
            async ct =>
            {
                if (Session is { } session
                    && string.Equals(session.Current.GameId, choice.GameId, StringComparison.OrdinalIgnoreCase))
                {
                    if (!await SaveOpenDraftAsync().ConfigureAwait(true))
                    {
                        return;
                    }

                    CloseSession();
                    Rebuild();
                }

                TrashResult trashed;

                try
                {
                    trashed = await _store.DeleteAsync(choice.GameId, ct).ConfigureAwait(true);
                }
                finally
                {
                    await LoadListAsync(ct).ConfigureAwait(true);
                }

                Notification? notice = null;

                var undo = new AsyncRelayCommand(async () =>
                {
                    if (await RestoreDraftAsync(trashed, choice.DisplayName).ConfigureAwait(true) && notice is not null)
                    {
                        _notifications.Dismiss(notice);
                    }
                });

                notice = _notifications.Add(
                    NotificationSeverity.Information,
                    Text[nameof(Strings.Studio_Deleted_Title)],
                    Text.Format(nameof(Strings.Studio_Deleted), choice.DisplayName),
                    action: undo,
                    actionText: Text[nameof(Strings.Notifications_Undo)]);
            });
    }

    /// <summary>Puts a deleted draft back. Not cancelled with the page: the notice outlives it.</summary>
    private Task<bool> RestoreDraftAsync(TrashResult trashed, string displayName) =>
        _runner.RunAsync(
            Text[nameof(Strings.Notifications_Undo)],
            async ct =>
            {
                await _store.RestoreAsync(trashed, ct).ConfigureAwait(true);

                _notifications.Add(
                    NotificationSeverity.Information,
                    Text[nameof(Strings.Notifications_Undo)],
                    Text.Format(nameof(Strings.Studio_Restored), displayName));

                if (Session is null)
                {
                    await LoadListAsync(ct).ConfigureAwait(true);
                }
            },
            CancellationToken.None);

    /// <summary>Saves the open draft, closes it, and shows the list.</summary>
    [RelayCommand]
    public Task BackToDraftsAsync() =>
        RunBusyAsync(
            Text[nameof(Strings.Studio_Status_Saving)],
            async ct =>
            {
                if (Session is { } session)
                {
                    await session.FlushAsync(CancellationToken.None).ConfigureAwait(true);

                    // Closing a draft whose last write failed would drop the change that failed.
                    if (session.HasFailed)
                    {
                        _notifications.Add(NotificationSeverity.Error, DraftTitle, session.StatusText);
                        return;
                    }

                    CloseSession();
                    Rebuild();
                }

                await LoadListAsync(ct).ConfigureAwait(true);
            });

    /// <inheritdoc />
    /// <remarks>An open draft is saved and closed, as <em>Back</em> does.</remarks>
    public override void ReturnToTop()
    {
        if (Session is not null)
        {
            Track(BackToDraftsAsync());
        }
    }

    /// <summary>Writes anything the open draft has not saved yet.</summary>
    /// <returns>A task that completes when the draft is on disk, or the write has failed.</returns>
    public Task FlushAsync(CancellationToken cancellationToken) =>
        Session?.FlushAsync(cancellationToken) ?? Task.CompletedTask;

    // Wizard commands

    /// <summary>Opens the new-game wizard, empty.</summary>
    [RelayCommand]
    public void OpenNewGame()
    {
        NewGameName = string.Empty;
        NewGameShortName = string.Empty;
        NewGameImporter = string.Empty;
        NewGameIcon = null;
        NewGameModsFolder = null;
        WizardStep = 1;
        IsNewGameOpen = true;
    }

    /// <summary>Closes the wizard without creating anything.</summary>
    [RelayCommand]
    public void CancelNewGame() => IsNewGameOpen = false;

    /// <summary>Moves to the wizard's next step.</summary>
    [RelayCommand]
    public void WizardNext()
    {
        if (CanWizardNext)
        {
            WizardStep++;
        }
    }

    /// <summary>Moves to the wizard's previous step.</summary>
    [RelayCommand]
    public void WizardBack()
    {
        if (CanWizardBack)
        {
            WizardStep--;
        }
    }

    /// <summary>Chooses the game's Mods folder.</summary>
    [RelayCommand]
    public Task BrowseNewGameModsAsync() =>
        _runner.RunAsync(
            Heading,
            async ct =>
            {
                var folder = await _picker
                    .PickFolderAsync(Text[nameof(Strings.Studio_Wizard_PickMods)], NewGameModsFolder, ct)
                    .ConfigureAwait(true);

                if (folder is not null)
                {
                    NewGameModsFolder = folder;
                }
            },
            ActivationToken);

    /// <summary>Chooses a picture for the game's icon.</summary>
    [RelayCommand]
    public Task BrowseNewGameIconAsync() =>
        _runner.RunAsync(
            Heading,
            async ct =>
            {
                var file = await _picker
                    .PickFileAsync(
                        Text[nameof(Strings.Studio_Wizard_PickIcon)],
                        [new FileTypeFilter(Text[nameof(Strings.Studio_Wizard_Images)], StudioDraftStore.ImageExtensions)],
                        null,
                        ct)
                    .ConfigureAwait(true);

                if (file is not null)
                {
                    NewGameIcon = file;
                }
            },
            ActivationToken);

    /// <summary>Creates the game's draft and opens it.</summary>
    [RelayCommand]
    public Task CreateGameAsync()
    {
        if (!CanCreateGame)
        {
            return Task.CompletedTask;
        }

        var request = new NewGame
        {
            DisplayName = NewGameName.Trim(),
            ShortName = Blank(NewGameShortName),
            Importer = Blank(NewGameImporter),
            ModsDirectory = NewGameModsFolder,
        };
        var icon = NewGameIcon;

        return RunBusyAsync(
            Text[nameof(Strings.Studio_Creating)],
            async ct =>
            {
                if (!await SaveOpenDraftAsync().ConfigureAwait(true))
                {
                    return;
                }

                var installed = await _installer.ListInstalledAsync(ct).ConfigureAwait(true);
                var drafts = await _store.ListAsync(ct).ConfigureAwait(true);

                var draft = PackDrafts.Create(
                    request,
                    [.. installed.Select(p => p.GameId), .. drafts.Select(d => d.GameId)],
                    _time.GetUtcNow());

                await _store.CreateAsync(draft, ct).ConfigureAwait(true);

                IsNewGameOpen = false;

                if (icon is not null)
                {
                    // The draft exists by now: a picture that cannot be copied costs only the icon.
                    try
                    {
                        var relative = await _store.StoreImageAsync(draft.GameId, "_game", icon, ct).ConfigureAwait(true);
                        draft = DraftEdits.EditGame(draft, new GameEdit { Icon = EditField<string>.To(relative) });
                        await _store.WriteAsync(draft, ct).ConfigureAwait(true);
                    }
                    catch (ModOperationException ex)
                    {
                        _notifications.Add(NotificationSeverity.Warning, request.DisplayName, ex.Message);
                    }
                }

                Open(draft);
            });
    }

    // Table commands

    /// <summary>Steps back one change.</summary>
    [RelayCommand]
    public void Undo() => Session?.Undo();

    /// <summary>Steps forward one undone change.</summary>
    [RelayCommand]
    public void Redo() => Session?.Redo();

    /// <summary>Tells the page which rows the table has selected.</summary>
    public void UpdateSelection(IReadOnlyList<StudioCharacterRowViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        // Rebuilding empties the table, which reports nothing selected.
        if (_rebuilding)
        {
            return;
        }

        _selected.Clear();
        _selected.UnionWith(rows.Select(r => r.InternalName));
        ApplySelection();
    }

    /// <summary>Whether <em>Add character</em> adds just the name (on) or opens the Character Manager (off).</summary>
    [ObservableProperty]
    private bool _isFastAdd = true;

    /// <summary>Remembers the choice; the box is the only thing that changes it.</summary>
    partial void OnIsFastAddChanged(bool value)
    {
        if (_loadingFastAdd)
        {
            return;
        }

        // Runs to the end whether or not the page stays on screen: it is one small write.
        Track(_runner.RunAsync(
            Heading,
            ct => _settings.UpdateAsync(settings => settings with { StudioAddOpensEditor = !value }, ct),
            CancellationToken.None));
    }

    private bool _loadingFastAdd;

    /// <summary>Adds a character with the typed name, or opens the Character Manager on it with Fast add off.</summary>
    [RelayCommand]
    public void AddCharacter()
    {
        var name = NewCharacterName.Trim();

        if (!IsFastAdd)
        {
            if (Session is { } session)
            {
                _manager.NewCharacter(new DraftCharacterTarget(this, session), name);
            }

            return;
        }

        if (name.Length == 0)
        {
            return;
        }

        string? added = null;

        Edit(draft =>
        {
            var result = DraftEdits.AddCharacter(draft, new NewCharacter { DisplayName = name });
            added = result.InternalName;
            return result.Draft;
        });

        if (added is not null)
        {
            NewCharacterName = string.Empty;
            Select(added);
        }
    }

    /// <summary>Sets <see cref="BulkAttribute"/> to <see cref="BulkAttributeValue"/> on the selection.</summary>
    [RelayCommand]
    public void SetAttribute()
    {
        var names = SelectedNames();
        var value = BulkAttributeValue;

        if (names.Count == 0 || BulkAttribute?.Id is not { } attribute)
        {
            return;
        }

        Edit(draft => DraftEdits.SetAttribute(draft, names, attribute, DraftEdits.ParseAttributeValue(draft, attribute, value)));
    }

    /// <summary>Clears <see cref="BulkAttribute"/> on the selection.</summary>
    [RelayCommand]
    public void ClearAttribute()
    {
        var names = SelectedNames();

        if (names.Count == 0 || BulkAttribute?.Id is not { } attribute)
        {
            return;
        }

        Edit(draft => DraftEdits.SetAttribute(draft, names, attribute, null));
    }

    /// <summary>Makes the selection outfits of <see cref="BulkBase"/>.</summary>
    [RelayCommand]
    public void LinkSelected()
    {
        var names = SelectedNames();

        if (names.Count > 0 && !string.IsNullOrWhiteSpace(BulkBase))
        {
            var baseId = BulkBase;
            EditWithNotes(draft => DraftEdits.SetBase(draft, names, baseId));
        }
    }

    /// <summary>Makes the selection characters in their own right — the fix for a wrong guessed link.</summary>
    [RelayCommand]
    public void UnlinkSelected()
    {
        var names = SelectedNames();

        if (names.Count > 0)
        {
            EditWithNotes(draft => DraftEdits.SetBase(draft, names, null));
        }
    }

    /// <summary>Adds an alias to each selected character: prefix, its name, suffix.</summary>
    [RelayCommand]
    public void AddAlias()
    {
        var names = SelectedNames();
        var prefix = AliasPrefix;
        var suffix = AliasSuffix;

        if (names.Count > 0)
        {
            Edit(draft => DraftEdits.AddAlias(draft, names, prefix, suffix));
        }
    }

    /// <summary>Deletes the selection. Undo brings it back.</summary>
    [RelayCommand]
    public void DeleteSelected()
    {
        var names = SelectedNames();

        if (names.Count > 0)
        {
            EditWithNotes(draft => DraftEdits.DeleteCharacters(draft, names));
        }
    }

    // Pictures in the table

    /// <summary>Chooses a picture file for one character, stored under a name of its own.</summary>
    /// <returns>A task that completes when the picture is in the draft, or the picker was closed.</returns>
    [RelayCommand]
    public Task ChoosePictureAsync(StudioCharacterRowViewModel? row)
    {
        if (row is null || Session is not { } session)
        {
            return Task.CompletedTask;
        }

        return _runner.RunAsync(
            Heading,
            async ct =>
            {
                var file = await PickPictureFileAsync(row, ct).ConfigureAwait(true);

                // Closed, or the draft changed underneath the picker: nowhere to write it.
                if (file is null || !ReferenceEquals(Session, session))
                {
                    return;
                }

                await StorePictureAsync(row, PreviewImageSource.FromFile(file), ct).ConfigureAwait(true);
            },
            ActivationToken);
    }

    /// <summary>Asks for a picture file for a character.</summary>
    private Task<string?> PickPictureFileAsync(StudioCharacterRowViewModel row, CancellationToken ct) =>
        _picker.PickFileAsync(
            Text.Format(nameof(Strings.Studio_Picture_Pick), row.DisplayName),
            [new FileTypeFilter(Text[nameof(Strings.Studio_Wizard_Images)], StudioDraftStore.ImageExtensions)],
            null,
            ct);

    /// <summary>The picture for one row: the loaded one if the same file, else a new one; null for none.</summary>
    private StudioPictureViewModel? PictureFor(string gameId, PackVariant variant, HashSet<string> kept)
    {
        if (variant.Image is not { Length: > 0 } image || _store.GetImageVersion(gameId, image) is not { } version)
        {
            return null;
        }

        var key = image + "\n" + version;
        kept.Add(variant.InternalName);

        if (_pictures.TryGetValue(variant.InternalName, out var existing))
        {
            if (string.Equals(existing.Key, key, StringComparison.Ordinal))
            {
                return existing;
            }

            existing.Dispose();
        }

        var picture = new StudioPictureViewModel(key);
        _pictures[variant.InternalName] = picture;
        Track(picture.LoadAsync(ct => _thumbnails.AcquireAsync(gameId, image, version, ct)));
        return picture;
    }

    /// <summary>Lets go of the pictures no row uses any more; all of them when none are kept.</summary>
    private void ReleasePictures(HashSet<string> kept)
    {
        foreach (var name in _pictures.Keys.Where(name => !kept.Contains(name)).ToList())
        {
            _pictures[name].Dispose();
            _pictures.Remove(name);
        }
    }

    // Try it

    /// <summary>Whether the Try it panel is showing.</summary>
    [ObservableProperty]
    private bool _isTryOpen;

    /// <summary>The folder of mods to try the draft against.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTryFolder))]
    private string? _tryModsFolder;

    /// <summary>Whether to list every mod, not only the ones that would move.</summary>
    [ObservableProperty]
    private bool _tryShowEveryMod;

    private SortRunPlan? _tryPlan;

    /// <summary>Whether a folder has been chosen to try against.</summary>
    public bool HasTryFolder => !string.IsNullOrWhiteSpace(TryModsFolder);

    /// <summary>The mods Try it found, and where each would go.</summary>
    public ObservableCollection<StudioTryRowViewModel> TryRows { get; } = [];

    /// <summary>"26 mods looked at; 3 mods would move. Nothing was moved.", or null before a run.</summary>
    public string? TrySummary { get; private set; }

    /// <summary>Whether Try it has run.</summary>
    public bool HasTryResult => TrySummary is not null;

    /// <summary>Opens the Try it panel, starting at the draft's own Mods folder.</summary>
    [RelayCommand]
    public void OpenTry()
    {
        if (Session is not { } session)
        {
            return;
        }

        if (!HasTryFolder)
        {
            TryModsFolder = session.Current.Info.ModsDirectory;
        }

        IsTryOpen = true;
    }

    /// <summary>Closes the Try it panel.</summary>
    [RelayCommand]
    public void CloseTry() => IsTryOpen = false;

    /// <summary>Chooses the folder to try against.</summary>
    [RelayCommand]
    public Task BrowseTryModsAsync() =>
        PickFolderAsync(Text[nameof(Strings.Studio_Wizard_PickMods)], TryModsFolder, folder => TryModsFolder = folder);

    /// <summary>Plans a sort of the chosen folder with the draft installed. Moves nothing.</summary>
    [RelayCommand]
    public Task RunTryAsync()
    {
        if (Session is not { } session || TryModsFolder is not { Length: > 0 } folder)
        {
            return Task.CompletedTask;
        }

        return RunBusyAsync(
            Text[nameof(Strings.Studio_Try_Running)],
            async ct =>
            {
                var draft = session.Current;

                _tryPlan = await _trial.PlanAsync(draft, folder, cancellationToken: ct).ConfigureAwait(true);

                // Kept with the draft, so the next Try it starts there.
                if (draft.Info.ModsDirectory is not { } remembered || !PathComparer.AreEqual(remembered, folder))
                {
                    session.Apply(session.Current with { Info = session.Current.Info with { ModsDirectory = folder } });
                }

                ShowTryRows();
            });
    }

    partial void OnTryShowEveryModChanged(bool value) => ShowTryRows();

    partial void OnBulkAttributeChanged(StudioAttributeColumn? oldValue, StudioAttributeColumn? newValue)
    {
        if (!_isReplacingAttributeChoices)
        {
            OnBulkAttributeChosen(oldValue, newValue);
        }
    }

    /// <summary>Choosing another attribute empties the value; the same one found after an edit keeps it.</summary>
    private void OnBulkAttributeChosen(StudioAttributeColumn? oldValue, StudioAttributeColumn? newValue)
    {
        if (!string.Equals(oldValue?.Id, newValue?.Id, StringComparison.OrdinalIgnoreCase))
        {
            BulkAttributeValue = string.Empty;
        }

        if (!(oldValue?.Suggestions ?? []).SequenceEqual(newValue?.Suggestions ?? [], StringComparer.Ordinal))
        {
            OnPropertyChanged(nameof(BulkValueChoices));
            OnPropertyChanged(nameof(HasBulkValueChoices));
        }
    }

    private void ShowTryRows()
    {
        TryRows.Clear();

        if (_tryPlan is { } plan)
        {
            foreach (var row in TryShowEveryMod ? plan.Rows : plan.Moves)
            {
                TryRows.Add(new StudioTryRowViewModel(row));
            }

            TrySummary = Text.Format(nameof(Strings.Studio_Try_Summary), Text.Mods(plan.Rows.Count), Text.Mods(plan.Moves.Count));
        }
        else
        {
            TrySummary = null;
        }

        OnPropertyChanged(nameof(TrySummary));
        OnPropertyChanged(nameof(HasTryResult));
    }

    // Export

    /// <summary>Whether the Export panel is showing.</summary>
    [ObservableProperty]
    private bool _isExportOpen;

    /// <summary>The folder to write the pack zip to.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExportFolder), nameof(CanRunExport))]
    private string? _exportFolder;

    /// <summary>The version to export as, short or full; stored in full. Proposed when the panel opens.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExportVersionValid), nameof(CanRunExport))]
    private string _exportVersion = string.Empty;

    /// <summary>Whether to replace a pack zip of the same name.</summary>
    [ObservableProperty]
    private bool _exportOverwrite;

    /// <summary>A registry folder to add the pack to, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExportRegistryFolder))]
    private string? _exportRegistryFolder;

    /// <summary>What changed in this version, for the registry.</summary>
    [ObservableProperty]
    private string _exportChangelog = string.Empty;

    /// <summary>A folder to write a contribution folder in, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExportContributionFolder))]
    private string? _exportContributionFolder;

    /// <summary>Whether an output folder has been chosen.</summary>
    public bool HasExportFolder => !string.IsNullOrWhiteSpace(ExportFolder);

    /// <summary>Whether a registry folder has been chosen.</summary>
    public bool HasExportRegistryFolder => !string.IsNullOrWhiteSpace(ExportRegistryFolder);

    /// <summary>Whether a contribution folder has been chosen.</summary>
    public bool HasExportContributionFolder => !string.IsNullOrWhiteSpace(ExportContributionFolder);

    /// <summary>Whether the version typed can be a version.</summary>
    public bool IsExportVersionValid => PackDrafts.IsValidVersion(PackVersionText.ToStored(ExportVersion));

    /// <summary>Whether Export can run: no errors, a folder, and a version.</summary>
    public bool CanRunExport => CanExport && HasExportFolder && IsExportVersionValid;

    /// <summary>What the last export wrote, or null.</summary>
    public StudioExportOutcome? ExportOutcome { get; private set; }

    /// <summary>Whether there is an export to report.</summary>
    public bool HasExportOutcome => ExportOutcome is not null;

    /// <summary>Opens the Export panel with a version proposed.</summary>
    [RelayCommand]
    public void OpenExport()
    {
        if (Session is not { } session)
        {
            return;
        }

        ExportVersion = PackVersionText.Display(PackDrafts.ProposeVersion(session.Current, _time.GetUtcNow()));
        SetExportOutcome(null);
        IsExportOpen = true;
    }

    /// <summary>Closes the Export panel.</summary>
    [RelayCommand]
    public void CloseExport() => IsExportOpen = false;

    /// <summary>Chooses where to write the pack.</summary>
    [RelayCommand]
    public Task BrowseExportFolderAsync() =>
        PickFolderAsync(Text[nameof(Strings.Studio_Export_PickFolder)], ExportFolder, folder => ExportFolder = folder);

    /// <summary>Chooses a registry folder to add the pack to.</summary>
    [RelayCommand]
    public Task BrowseExportRegistryAsync() =>
        PickFolderAsync(Text[nameof(Strings.Studio_Export_PickRegistry)], ExportRegistryFolder, folder => ExportRegistryFolder = folder);

    /// <summary>Chooses where to write a contribution folder.</summary>
    [RelayCommand]
    public Task BrowseExportContributionAsync() =>
        PickFolderAsync(Text[nameof(Strings.Studio_Export_PickContribution)], ExportContributionFolder, folder => ExportContributionFolder = folder);

    /// <summary>Exports the draft, remembers the version, and adds it to a registry or contribution folder.</summary>
    [RelayCommand]
    public Task ExportAsync()
    {
        if (Session is not { } session || !CanRunExport || ExportFolder is not { } folder)
        {
            return Task.CompletedTask;
        }

        var version = PackVersionText.ToStored(ExportVersion);
        var overwrite = ExportOverwrite;
        var registry = HasExportRegistryFolder ? ExportRegistryFolder : null;
        var changelog = Blank(ExportChangelog);
        var contribution = HasExportContributionFolder ? ExportContributionFolder : null;

        return RunBusyAsync(
            Text[nameof(Strings.Studio_Export_Running)],
            async ct =>
            {
                SetExportOutcome(null);

                var result = await _exporter.ExportAsync(session.Current, folder, version, overwrite, ct).ConfigureAwait(true);

                session.Apply(PackDrafts.AfterExport(session.Current, result.PackVersion));
                await session.FlushAsync(ct).ConfigureAwait(true);

                string? index = null;
                if (registry is not null)
                {
                    index = (await _publisher.AddToRegistryAsync(session.Current, result, registry, changelog, ct).ConfigureAwait(true)).IndexPath;
                }

                string? contributionDirectory = null;
                if (contribution is not null)
                {
                    contributionDirectory = (await _publisher.WriteContributionAsync(session.Current, result, contribution, ct).ConfigureAwait(true)).Directory;
                }

                SetExportOutcome(new StudioExportOutcome(
                    result.PackFile,
                    Text.Format(
                        nameof(Strings.Studio_Export_Result),
                        PackVersionText.Display(result.PackVersion),
                        PackChoiceViewModel.FormatSize(result.SizeBytes),
                        Text.Warnings(result.Validation.WarningCount)),
                    result.Sha256,
                    index,
                    contributionDirectory));

                _notifications.Add(
                    NotificationSeverity.Information,
                    DraftTitle,
                    Text.Format(nameof(Strings.Studio_Export_Done), PackVersionText.Display(result.PackVersion), PathDisplay.Show(result.PackFile)));
            });
    }

    /// <summary>Whether installing the draft into this copy of XXSM can run.</summary>
    public bool CanInstallHere => CanExport && Session is not null;

    /// <summary>Installs the draft into this XXSM without exporting it first, at the version Export proposes.</summary>
    [RelayCommand]
    public Task InstallHereAsync()
    {
        if (Session is not { } session || !CanExport)
        {
            return Task.CompletedTask;
        }

        var version = PackDrafts.ProposeVersion(session.Current, _time.GetUtcNow());

        return RunBusyAsync(
            Text[nameof(Strings.Studio_Install_Running)],
            async ct =>
            {
                var result = await _publisher.InstallHereAsync(session.Current, version, ct).ConfigureAwait(true);

                session.Apply(PackDrafts.AfterExport(session.Current, result.PackVersion));
                await session.FlushAsync(ct).ConfigureAwait(true);

                await _reloadGames(ct).ConfigureAwait(true);

                _notifications.Add(
                    NotificationSeverity.Information,
                    DraftTitle,
                    Text.Format(
                        nameof(Strings.Studio_Install_Done),
                        PackVersionText.Display(result.PackVersion),
                        Text.Warnings(result.Validation.WarningCount)));

                await FollowUp.AfterInstallAsync(result.Install, DraftTitle, ct).ConfigureAwait(true);
            });
    }

    /// <summary>The panels that follow <em>Install in XXSM</em>: Skipped updates, then the clean-up offer.</summary>
    public PackInstallFollowUp FollowUp { get; }

    /// <summary>The table's name among the remembered column widths.</summary>
    public const string ColumnWidthsTable = "studio";

    /// <summary>The table's remembered column widths, which the view applies and reports to.</summary>
    public ColumnWidthMemory ColumnWidths { get; }

    /// <summary>Forgets the column widths the user dragged, so each is fitted to what it holds again.</summary>
    [RelayCommand]
    public Task ResetColumnWidthsAsync() => ColumnWidths.Forget(ColumnWidthsTable);

    /// <summary>Shows the folder the pack was written to.</summary>
    [RelayCommand]
    public Task OpenExportFolderAsync() =>
        ExportFolder is not { } folder
            ? Task.CompletedTask
            : _runner.RunAsync(Heading, ct => _launcher.OpenAsync(folder, ct), ActivationToken);

    private void SetExportOutcome(StudioExportOutcome? outcome)
    {
        ExportOutcome = outcome;
        OnPropertyChanged(nameof(ExportOutcome));
        OnPropertyChanged(nameof(HasExportOutcome));
    }

    private void ResetPanels()
    {
        Import.Close();
        GameSettings.Close();
        HashList.Close();
        HashesRow = null;
        IsSharingOpen = false;
        IgnoreCost = null;
        IsDefaultChoiceOpen = false;
        UnclaimedName = null;
        IsTryOpen = false;
        IsExportOpen = false;
        TryModsFolder = null;
        _tryPlan = null;
        ShowTryRows();
        SetExportOutcome(null);
    }

    private Task<bool> PickFolderAsync(string title, string? startAt, Action<string> chosen) =>
        _runner.RunAsync(
            Heading,
            async ct =>
            {
                var folder = await _picker.PickFolderAsync(title, startAt, ct).ConfigureAwait(true);

                if (folder is not null)
                {
                    chosen(folder);
                }
            },
            ActivationToken);

    // Lifecycle

    /// <inheritdoc />
    protected override void OnActivated()
    {
        if (Session is null)
        {
            Track(RefreshAsync());
        }

        Track(_runner.RunAsync(
            Heading,
            async ct =>
            {
                var settings = await _settings.ReadAsync(ct).ConfigureAwait(true);
                _problemsFoldedWhenRoomy = settings.StudioProblemsCollapsed;
                IsProblemsCollapsed = IsPageNarrow || _problemsFoldedWhenRoomy;

                _loadingFastAdd = true;
                try
                {
                    IsFastAdd = !settings.StudioAddOpensEditor;
                }
                finally
                {
                    _loadingFastAdd = false;
                }
            },
            ActivationToken));
    }

    /// <inheritdoc />
    /// <remarks>Writes a pending change at once rather than a second later.</remarks>
    protected override void OnDeactivated()
    {
        if (_manager.IsEditingElsewhere)
        {
            _manager.Close();
        }

        if (Session is { } session)
        {
            Track(session.FlushAsync(CancellationToken.None));
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CloseSession();
        }

        base.Dispose(disposing);
    }

    // Internals

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private Task<bool> RunBusyAsync(string message, Func<CancellationToken, Task> work)
    {
        IsBusy = true;
        BusyMessage = message;
        NotifyList();

        return _runner.RunAsync(
            Heading,
            async ct =>
            {
                try
                {
                    await work(ct).ConfigureAwait(true);
                }
                finally
                {
                    IsBusy = false;
                    BusyMessage = null;
                    NotifyList();
                }
            },
            ActivationToken);
    }

    private async Task LoadListAsync(CancellationToken cancellationToken)
    {
        var drafts = await _store.ListAsync(cancellationToken).ConfigureAwait(true);
        var installed = await _installer.ListInstalledAsync(cancellationToken).ConfigureAwait(true);

        Drafts.Clear();

        foreach (var draft in drafts)
        {
            Drafts.Add(new StudioDraftChoiceViewModel(
                draft,
                Text.Format(nameof(Strings.Studio_Draft_Summary), Text.Characters(draft.VariantCount), PackVersionText.Display(draft.PackVersion)))
            {
                Icon = _icons.Draft(draft.GameId, draft.DisplayName, draft.Icon),
            });
        }

        InstalledPacks.Clear();

        foreach (var pack in installed.Where(p => !drafts.Any(d => string.Equals(d.GameId, p.GameId, StringComparison.OrdinalIgnoreCase))))
        {
            InstalledPacks.Add(new StudioPackChoiceViewModel(pack.GameId, pack.DisplayName ?? pack.GameId, pack.PackVersion)
            {
                Icon = _icons.Installed(pack.GameId, pack.DisplayName ?? pack.GameId),
            });
        }

        NotifyList();
    }

    /// <summary>Writes the open draft before another replaces it; false keeps it open, as the write failed.</summary>
    private async Task<bool> SaveOpenDraftAsync()
    {
        if (Session is not { } session)
        {
            return true;
        }

        await session.FlushAsync(CancellationToken.None).ConfigureAwait(true);

        if (session.HasFailed)
        {
            _notifications.Add(NotificationSeverity.Error, DraftTitle, session.StatusText);
            return false;
        }

        return true;
    }

    private void Open(PackDraft draft)
    {
        CloseSession();
        ResetPanels();

        var session = new StudioDraftSession(draft, _store, _time, _dispatcher, Text, _logger);
        session.DraftChanged += OnDraftChanged;

        _selected.Clear();
        Session = session;
        Rebuild();
    }

    private void CloseSession()
    {
        if (Session is not { } session)
        {
            return;
        }

        // An editor open on this draft would write into a draft no longer on screen.
        if (_manager.IsEditingElsewhere)
        {
            _manager.Close();
        }

        session.DraftChanged -= OnDraftChanged;
        session.Dispose();
        Session = null;
        ReleasePictures([]);
        ResetPanels();
    }

    private void OnDraftChanged(object? sender, EventArgs e) => Rebuild();

    private List<string> SelectedNames() => [.. SelectedRows.Select(r => r.InternalName)];

    private void Select(string internalName)
    {
        _selected.Clear();
        _selected.Add(internalName);
        ApplySelection();
    }

    private bool Edit(Func<PackDraft, PackDraft> change)
    {
        if (Session is not { } session)
        {
            return false;
        }

        try
        {
            session.Apply(change(session.Current));
            return true;
        }
        catch (ModOperationException ex)
        {
            // A refused inline edit: putting the rows back shows what the draft still says.
            Refuse(ex.Message);
            return false;
        }
    }

    private void EditWithNotes(Func<PackDraft, DraftChange> change)
    {
        IReadOnlyList<string> notes = [];

        Edit(draft =>
        {
            var result = change(draft);
            notes = result.Notes;
            return result.Draft;
        });

        foreach (var note in notes)
        {
            _notifications.Add(NotificationSeverity.Information, DraftTitle, note);
        }
    }

    // The table's cells

    /// <inheritdoc />
    public void EditName(StudioCharacterRowViewModel row, string text)
    {
        ArgumentNullException.ThrowIfNull(row);

        EditWithNotes(draft => DraftEdits.EditCharacter(
            draft, row.InternalName, new CharacterEdit { DisplayName = EditField<string>.To(text ?? string.Empty) }));
    }

    /// <inheritdoc />
    public void EditInternalName(StudioCharacterRowViewModel row, string text)
    {
        ArgumentNullException.ThrowIfNull(row);

        var from = row.InternalName;
        var to = (text ?? string.Empty).Trim();

        if (to.Length == 0)
        {
            Refuse(Text[nameof(Strings.Studio_Cell_IdBlank)]);
            return;
        }

        if (!Edit(draft => DraftEdits.RenameCharacter(draft, from, to)))
        {
            return;
        }

        // The selection is kept by id, so a renamed row stays selected.
        if (_selected.Remove(from))
        {
            _selected.Add(to);
            ApplySelection();
        }
    }

    /// <inheritdoc />
    public void EditAliases(StudioCharacterRowViewModel row, string text)
    {
        ArgumentNullException.ThrowIfNull(row);

        IReadOnlyList<string> aliases = (text ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        EditWithNotes(draft => DraftEdits.EditCharacter(
            draft, row.InternalName, new CharacterEdit { Aliases = EditField<IReadOnlyList<string>>.To(aliases) }));
    }

    /// <inheritdoc />
    public void EditOutfitOf(StudioCharacterRowViewModel row, string text)
    {
        ArgumentNullException.ThrowIfNull(row);

        var typed = (text ?? string.Empty).Trim();

        if (string.Equals(typed, row.OutfitOf, StringComparison.Ordinal))
        {
            return;
        }

        string? baseId = null;

        if (typed.Length > 0)
        {
            baseId = FindOutfitChoice(typed);

            if (baseId is null)
            {
                Refuse(Text.Format(nameof(Strings.Studio_Cell_NoSuchCharacter), typed));
                return;
            }
        }

        EditWithNotes(draft => DraftEdits.SetBase(draft, [row.InternalName], baseId));
    }

    /// <inheritdoc />
    public void EditAttribute(StudioCharacterRowViewModel row, string attributeId, string text)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentException.ThrowIfNullOrWhiteSpace(attributeId);

        if (string.Equals((text ?? string.Empty).Trim(), row.AttributeText(attributeId), StringComparison.Ordinal))
        {
            return;
        }

        Edit(draft => DraftEdits.SetAttribute(
            draft, [row.InternalName], attributeId, DraftEdits.ParseAttributeValue(draft, attributeId, text)));
    }

    /// <inheritdoc />
    public void EditHashes(StudioCharacterRowViewModel row, string text) => ReplaceHashes(row, text, nameof(Strings.Studio_Cell_NoHashes));

    // The hashes panel

    /// <summary>One character's hashes as text: the same editor the Character Manager opens.</summary>
    public HashListEditorViewModel HashList { get; }

    /// <summary>The character whose hashes the editor is showing, or null when it is closed.</summary>
    public StudioCharacterRowViewModel? HashesRow { get; private set; }

    /// <summary>Opens the hashes editor on a character, one hash per line.</summary>
    public void OpenHashes(StudioCharacterRowViewModel row) => OpenHashes(row, []);

    /// <summary>Opens the hashes editor on a character, with some of them marked for a look.</summary>
    /// <param name="row">The character.</param>
    /// <param name="marks">The hashes to point out, and why.</param>
    public void OpenHashes(StudioCharacterRowViewModel row, IReadOnlyList<StudioMarkedHashViewModel> marks)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(marks);

        HashesRow = row;
        OnPropertyChanged(nameof(HashesRow));

        HashList.Open(
            Text.Format(nameof(Strings.Studio_Hashes_Title), row.DisplayName),
            row.Hashes,
            text => ReplaceHashes(row, text, nameof(Strings.HashList_NoHashes)),
            [.. marks, .. IgnoredMarks(row)],
            StopIgnoring);
    }

    /// <summary>A mark on each of a character's hashes the pack ignores, with an offer to count it again.</summary>
    internal IReadOnlyList<StudioMarkedHashViewModel> IgnoredMarks(StudioCharacterRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (Session?.Current.Hashes.IgnoredHashes is not { Count: > 0 } ignored)
        {
            return [];
        }

        var set = ignored.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return
        [
            .. row.Hashes
                .Where(set.Contains)
                .Select(hash => new StudioMarkedHashViewModel(hash, Text[nameof(Strings.Studio_Hashes_Mark_Ignored)])
                {
                    IsIgnored = true,
                }),
        ];
    }

    /// <summary>Takes one hash off the pack's list of hashes to ignore, so the sorter scores it again.</summary>
    /// <returns>True when the draft took it.</returns>
    internal bool StopIgnoring(string hash)
    {
        if (!Edit(draft => DraftEdits.UnignoreHashes(draft, [hash]).Draft))
        {
            return false;
        }

        _notifications.Add(
            NotificationSeverity.Information,
            DraftTitle,
            Text.Format(nameof(Strings.Studio_Sharing_StoppedIgnoring), hash));

        return true;
    }

    /// <summary>Makes what was typed the character's whole hash list.</summary>
    /// <param name="row">The character.</param>
    /// <param name="text">What was typed.</param>
    /// <param name="noHashes">What to say when it holds no hash, naming the cell or the box.</param>
    private bool ReplaceHashes(StudioCharacterRowViewModel row, string? text, string noHashes)
    {
        ArgumentNullException.ThrowIfNull(row);

        var parsed = HashPaste.Parse(text);

        // Text that holds no hash is a mistake, not a request to remove them all.
        if (parsed.IsEmpty && !string.IsNullOrWhiteSpace(text))
        {
            Refuse(parsed.Rejected.Count > 0
                ? Text.Format(nameof(Strings.Studio_Cell_NotAHash), parsed.Rejected[0].Text, parsed.Rejected[0].Reason)
                : Text[noHashes]);
            return false;
        }

        if (!Edit(draft => DraftEdits.ReplaceHashes(draft, row.InternalName, parsed.Entries).Draft))
        {
            return false;
        }

        foreach (var rejected in parsed.Rejected)
        {
            _notifications.Add(
                NotificationSeverity.Warning,
                row.DisplayName,
                Text.Format(nameof(Strings.Studio_Cell_NotAHash), rejected.Text, rejected.Reason));
        }

        return true;
    }

    private string? FindOutfitChoice(string typed)
    {
        var choices = OutfitChoices;

        return (choices.FirstOrDefault(c => string.Equals(c.Label, typed, StringComparison.OrdinalIgnoreCase))
                ?? choices.FirstOrDefault(c => string.Equals(c.InternalName, typed, StringComparison.OrdinalIgnoreCase))
                ?? SingleByName(choices, typed))?.InternalName;

        StudioOutfitChoice? SingleByName(IReadOnlyList<StudioOutfitChoice> all, string name)
        {
            if (Session is not { } session)
            {
                return null;
            }

            var named = session.Current.Variants
                .Where(v => string.Equals(v.DisplayName, name, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToList();

            return named.Count == 1
                ? all.FirstOrDefault(c => string.Equals(c.InternalName, named[0].InternalName, StringComparison.OrdinalIgnoreCase))
                : null;
        }
    }

    /// <summary>Says why a cell was not changed, and puts the table back to what the draft says.</summary>
    private void Refuse(string reason)
    {
        _notifications.Add(NotificationSeverity.Error, Text[nameof(Strings.Studio_Edit_Failed)], reason);
        Rebuild();
    }

    private void Rebuild()
    {
        _rebuilding = true;

        try
        {
            Fill();
        }
        finally
        {
            _rebuilding = false;
        }

        ApplySelection();

        OnPropertyChanged(nameof(DraftTitle));
        OnPropertyChanged(nameof(DraftIcon));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanRunExport));
        OnPropertyChanged(nameof(CanInstallHere));
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(ProblemsSummary));
        OnPropertyChanged(nameof(ProblemBadgeText));
        OnPropertyChanged(nameof(HasProblemBadge));
        OnPropertyChanged(nameof(IsProblemBadgeError));
        ApplyProblemFilter();
        OnPropertyChanged(nameof(BaseChoices));

        // The chooser drops its choice for a moment when given a new list; that is not the user choosing.
        var chosen = BulkAttribute;
        _isReplacingAttributeChoices = true;

        try
        {
            OnPropertyChanged(nameof(AttributeChoices));
            OnPropertyChanged(nameof(HasAttributeChoices));
            OnPropertyChanged(nameof(AttributePlaceholder));

            // After the list is announced, on purpose: a chooser drops a selection not yet in its list.
            BulkAttribute =
                AttributeChoices.FirstOrDefault(choice =>
                    string.Equals(choice.Id, chosen?.Id, StringComparison.OrdinalIgnoreCase))
                ?? (AttributeChoices.Count > 0 ? AttributeChoices[0] : null);
        }
        finally
        {
            _isReplacingAttributeChoices = false;
        }

        OnBulkAttributeChosen(chosen, BulkAttribute);
        OnPropertyChanged(nameof(OutfitChoices));
    }

    private void Fill()
    {
        Problems.Clear();

        if (Session is { } session)
        {
            var draft = session.Current;
            var validation = PackValidator.Validate(draft, imageBytes: path => _store.GetImageSize(draft.GameId, path));

            var hashes = (draft.Hashes.Entries ?? [])
                .GroupBy(entry => entry.Variant, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<string>)[.. group.Select(entry => entry.Hash).Distinct(StringComparer.OrdinalIgnoreCase)],
                    StringComparer.OrdinalIgnoreCase);

            OutfitChoices = OutfitChoicesFor(draft);
            // Two ids differing only by capitals are a problem to show, not a reason to fail to open.
            var labels = OutfitChoices
                .GroupBy(c => c.InternalName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Label, StringComparer.OrdinalIgnoreCase);

            var about = validation.Problems
                .Where(problem => problem.Subject is not null)
                .GroupBy(problem => problem.Subject!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (Errors: group.Count(p => p.Severity == DiagnosticSeverity.Error),
                              Warnings: group.Count(p => p.Severity != DiagnosticSeverity.Error)),
                    StringComparer.OrdinalIgnoreCase);

            var rows = new List<StudioCharacterRowViewModel>(draft.Variants.Count);
            var pictured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var ignored = (draft.Hashes.IgnoredHashes ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var variant in PackDrafts.Ordered(draft.Variants))
            {
                about.TryGetValue(variant.InternalName, out var counts);

                var own = hashes.GetValueOrDefault(variant.InternalName) ?? [];
                var ignoredHere = ignored.Count == 0 ? 0 : own.Count(hash => ignored.Contains(hash));

                rows.Add(new StudioCharacterRowViewModel(
                    variant,
                    own,
                    variant.BaseCharacterId is { } baseId ? labels.GetValueOrDefault(baseId) ?? baseId : string.Empty,
                    counts.Errors,
                    counts.Warnings,
                    PictureFor(draft.GameId, variant, pictured),
                    ignored,
                    ignoredHere == 0
                        ? null
                        : Text.Format(nameof(Strings.Studio_Column_HashesIgnored), own.Count, ignoredHere)));
            }

            SyncRows(rows);
            ReleasePictures(pictured);

            var names = new HashSet<string>(Rows.Select(r => r.InternalName), StringComparer.OrdinalIgnoreCase);

            foreach (var problem in validation.Problems)
            {
                Problems.Add(ToProblem(problem, names));
            }

            FillProblemKinds();

            ErrorCount = validation.ErrorCount;
            WarningCount = validation.WarningCount;

            BaseChoices =
            [
                .. draft.Variants
                    .Where(v => v.BaseCharacterId is null)
                    .Select(v => v.InternalName)
                    .Order(StringComparer.OrdinalIgnoreCase),
            ];

            IReadOnlyList<StudioAttributeColumn> attributes =
            [
                .. (draft.Game.Attributes ?? new Dictionary<string, AttributeDefinition>())
                    .Select(pair => new StudioAttributeColumn(
                        pair.Key,
                        string.IsNullOrWhiteSpace(pair.Value.DisplayName) ? pair.Key : pair.Value.DisplayName,
                        pair.Value.IsNumeric ? [] : [.. (pair.Value.Values ?? []).Select(v => v.Id)])),
            ];

            AttributeChoices = attributes;
            SetAttributeColumns(attributes);
        }
        else
        {
            Rows.Clear();
            ReleasePictures([]);
            ProblemKinds.Clear();
            ErrorCount = 0;
            WarningCount = 0;
            BaseChoices = [];
            AttributeChoices = [];
            OutfitChoices = [];
            SetAttributeColumns([]);
        }
    }

    /// <summary>Brings the table's rows in line with <paramref name="wanted"/>, touching only what differs.</summary>
    /// <remarks>Never emptied and refilled: a sorted, scrolled grid would draw blank space.</remarks>
    private void SyncRows(List<StudioCharacterRowViewModel> wanted)
    {
        var keep = wanted.Select(r => r.InternalName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Rows[i].InternalName))
            {
                Rows.RemoveAt(i);
            }
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            var row = wanted[i];
            var at = -1;

            for (var j = i; j < Rows.Count; j++)
            {
                if (string.Equals(Rows[j].InternalName, row.InternalName, StringComparison.OrdinalIgnoreCase))
                {
                    at = j;
                    break;
                }
            }

            if (at < 0)
            {
                Rows.Insert(i, row);
                continue;
            }

            if (at != i)
            {
                Rows.Move(at, i);
            }

            if (!Rows[i].LooksLike(row))
            {
                Rows[i] = row;
            }
        }
    }

    private static List<StudioOutfitChoice> OutfitChoicesFor(PackDraft draft)
    {
        var shared = draft.Variants
            .GroupBy(v => v.DisplayName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return
        [
            .. draft.Variants
                .Select(v => new StudioOutfitChoice(
                    v.InternalName,
                    string.IsNullOrWhiteSpace(v.DisplayName)
                        ? v.InternalName
                        : shared.Contains(v.DisplayName.Trim())
                            ? $"{v.DisplayName.Trim()} ({v.InternalName})"
                            : v.DisplayName.Trim()))
                .OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase),
        ];
    }

    private void SetAttributeColumns(IReadOnlyList<StudioAttributeColumn> columns)
    {
        if (StudioAttributeColumn.SameColumns(AttributeColumns, columns))
        {
            return;
        }

        AttributeColumns = columns;
        OnPropertyChanged(nameof(AttributeColumns));
    }

    private void ApplySelection()
    {
        SelectedRows = [.. Rows.Where(row => _selected.Contains(row.InternalName))];

        foreach (var row in Rows)
        {
            row.IsTicked = _selected.Contains(row.InternalName);
        }

        OnPropertyChanged(nameof(SelectedRows));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(IsSelectionBarVisible));
    }

    private void NotifyList()
    {
        OnPropertyChanged(nameof(HasDrafts));
        OnPropertyChanged(nameof(HasInstalledPacks));
        OnPropertyChanged(nameof(IsListEmpty));
    }

    /// <summary>The open draft as somewhere the Character Manager writes, each save one Undo step.</summary>
    private sealed class DraftCharacterTarget(StudioPageViewModel page, StudioDraftSession session) : ICharacterEditTarget
    {
        public GameData Data { get; } = page._trial.ToGameData(session.Current);

        public IReadOnlySet<string> IgnoredHashes =>
            (session.Current.Hashes.IgnoredHashes ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);

        public bool StopIgnoring(string hash) => page.StopIgnoring(hash);

        public async Task<IReadOnlyList<string>> SaveAsync(CharacterEditorViewModel editor, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(editor);

            if (!ReferenceEquals(page.Session, session))
            {
                throw new ModOperationException(page.Text[nameof(Strings.Studio_Manager_DraftClosed)]);
            }

            if (editor.Existing is not { } existing)
            {
                await CreateAsync(editor, cancellationToken).ConfigureAwait(true);
                return [];
            }

            var id = existing.InternalName;

            if (editor.PendingPortrait is { } picture)
            {
                editor.MarkPortraitStored(
                    await page._store.StoreImageAsync(session.Current.GameId, id, picture, cancellationToken).ConfigureAwait(true));
            }

            var draft = session.Current;
            var notes = new List<string>();
            var edit = editor.ToEdit();

            if (!edit.IsEmpty)
            {
                var edited = DraftEdits.EditCharacter(draft, id, edit);
                draft = edited.Draft;
                notes.AddRange(edited.Notes);
            }

            if (editor.RemovedHashes() is { Count: > 0 } removed)
            {
                draft = DraftEdits.RemoveHashes(draft, id, removed).Draft;
            }

            if (editor.AddedHashes() is { Count: > 0 } added)
            {
                draft = DraftEdits.AddHashes(draft, id, added).Draft;
            }

            session.Apply(draft);
            return notes;
        }

        /// <summary>A new character from the editor as one Undo step: added, then given its stored picture.</summary>
        private async Task CreateAsync(CharacterEditorViewModel editor, CancellationToken cancellationToken)
        {
            var added = DraftEdits.AddCharacter(session.Current, editor.ToNewCharacter() with { Image = null });
            var draft = added.Draft;

            if (editor.PendingPortrait is { } picture)
            {
                editor.MarkPortraitStored(await page._store
                    .StoreImageAsync(session.Current.GameId, added.InternalName, picture, cancellationToken)
                    .ConfigureAwait(true));
            }

            if (editor.ImagePath is { Length: > 0 } image)
            {
                draft = DraftEdits.EditCharacter(draft, added.InternalName, new CharacterEdit { Image = EditField<string>.To(image) }).Draft;
            }

            session.Apply(draft);
            page.NewCharacterName = string.Empty;
            page.Select(added.InternalName);
        }
    }
}
