using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Desktop.Services;
using Xxsm.Packs.Importing;

namespace Xxsm.Desktop.ViewModels;

/// <summary>A character tag no character answers to, and what the user said it means.</summary>
public sealed partial class ImportTagViewModel : ObservableObject
{
    private readonly ModImportViewModel _owner;

    /// <summary>Creates the row.</summary>
    /// <param name="tag">The tag as written.</param>
    /// <param name="countText">How many mods carry it.</param>
    /// <param name="chosenName">The character chosen for it, or <c>null</c>.</param>
    /// <param name="owner">The panel, which does what the buttons ask.</param>
    internal ImportTagViewModel(string tag, string countText, string? chosenName, ModImportViewModel owner)
    {
        Tag = tag;
        CountText = countText;
        ChosenName = chosenName;
        _owner = owner;
    }

    /// <summary>The tag as written.</summary>
    public string Tag { get; }

    /// <summary>How many mods carry it.</summary>
    public string CountText { get; }

    /// <summary>The character chosen for it, or <c>null</c> while it names none.</summary>
    public string? ChosenName { get; }

    /// <summary>Whether a character has been chosen for it.</summary>
    public bool IsChosen => ChosenName is not null;

    /// <summary>Opens the character picker for this tag.</summary>
    [RelayCommand]
    private void Choose() => _owner.ChooseFor(this);

    /// <summary>Opens the Character Manager on a new character named after this tag.</summary>
    [RelayCommand]
    private void New() => _owner.CreateFor(this);

    /// <summary>Forgets the character chosen for this tag.</summary>
    [RelayCommand]
    private Task ForgetAsync() => _owner.ForgetAsync(this);
}

/// <summary>A mod whose tag and hashes name different characters.</summary>
public sealed partial class ImportDisagreementViewModel : ObservableObject
{
    /// <summary>Creates the row.</summary>
    internal ImportDisagreementViewModel(ModImportRow row, ITextCatalogue text)
    {
        Row = row;
        ModName = row.Details.Name is { Length: > 0 } name ? name : row.ModName;
        Detail = text.Format(
            nameof(Strings.Import_Disagree_Row), row.Character!.DisplayName, row.HashCharacter!.DisplayName);
    }

    /// <summary>The import row.</summary>
    public ModImportRow Row { get; }

    /// <summary>The mod.</summary>
    public string ModName { get; }

    /// <summary>"Tagged Klee; its hashes say Ganyu".</summary>
    public string Detail { get; }

    /// <summary>Whether the tag decides. Ticked by default: the tag wins.</summary>
    [ObservableProperty]
    private bool _followTag = true;
}

/// <summary>"Bring in details from XX-Mod-Manager": what another manager knew, and the answers needed first.</summary>
/// <remarks>Writes each mod's <c>.xxsm/mod.json</c> and moves nothing; the sort review files them after.</remarks>
public sealed partial class ModImportViewModel : ObservableObject
{
    private readonly GameContext _game;
    private readonly IModImporter _importer;
    private readonly SortReviewViewModel _sortReview;
    private readonly CharacterManagerViewModel _manager;
    private readonly INotificationService _notifications;
    private readonly ViewModelWorkRunner _work;
    private readonly ITextCatalogue _text;
    private readonly Func<CancellationToken, Task> _rescan;
    private readonly Dictionary<string, string> _choices = new(StringComparer.OrdinalIgnoreCase);

    // The game the choices were made in; another game starts without them.
    private string? _choicesGameId;

    private ModImportPlan? _plan;
    private int _planVersion;

    private HashSet<string> _unticked = new(StringComparer.Ordinal);

    /// <summary>Creates the panel.</summary>
    /// <param name="game">The shell's shared view of the selected game.</param>
    /// <param name="importer">Plans and writes the import.</param>
    /// <param name="sortReview">Opened afterwards, to file the mods by their new characters.</param>
    /// <param name="manager">Creates a character for a tag the pack does not know.</param>
    /// <param name="notifications">Where the result is reported.</param>
    /// <param name="work">Turns a failure into a notice rather than a crash.</param>
    /// <param name="text">The interface's wording.</param>
    /// <param name="rescan">Rescans the Mods folder after writing, so every page shows it.</param>
    public ModImportViewModel(
        GameContext game,
        IModImporter importer,
        SortReviewViewModel sortReview,
        CharacterManagerViewModel manager,
        INotificationService notifications,
        ViewModelWorkRunner work,
        ITextCatalogue text,
        Func<CancellationToken, Task> rescan)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(importer);
        ArgumentNullException.ThrowIfNull(sortReview);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(rescan);

        _game = game;
        _importer = importer;
        _sortReview = sortReview;
        _manager = manager;
        _notifications = notifications;
        _work = work;
        _text = text;
        _rescan = rescan;

        Picker.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CharacterPickerViewModel.IsOpen))
            {
                OnPropertyChanged(nameof(IsPanelVisible));
            }
        };
    }

    /// <summary>Whether the panel is open, whether or not it is showing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPanelVisible))]
    private bool _isOpen;

    /// <summary>Whether the panel is drawn: it steps aside while another panel answers one of its tags.</summary>
    public bool IsPanelVisible => IsOpen && !Picker.IsOpen && !_manager.IsOpen;

    /// <summary>How many mods have something new to bring in, as the Mods page's card last counted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAvailable))]
    private int _availableCount;

    /// <summary>What the Mods page's card says.</summary>
    [ObservableProperty]
    private string? _availableText;

    /// <summary>Whether there is anything to bring in, so the card is shown.</summary>
    public bool IsAvailable => AvailableCount > 0;

    /// <summary>"Bring in details from XX-Mod-Manager".</summary>
    [ObservableProperty]
    private string _heading = string.Empty;

    /// <summary>One line per kind of detail: "Links: 139 mods".</summary>
    public ObservableCollection<string> Fills { get; } = [];

    /// <summary>Tags that name no character, and those the user has answered.</summary>
    public ObservableCollection<ImportTagViewModel> Tags { get; } = [];

    /// <summary>Mods whose tag and hashes disagree.</summary>
    public ObservableCollection<ImportDisagreementViewModel> Disagreements { get; } = [];

    /// <summary>Mods that cannot be brought in, each as a sentence.</summary>
    public ObservableCollection<string> Problems { get; } = [];

    /// <summary>The one character picker, for a tag's character.</summary>
    public CharacterPickerViewModel Picker { get; } = new();

    /// <summary>Whether the plan is being worked out again; nothing can be applied meanwhile.</summary>
    [ObservableProperty]
    private bool _isPlanning;

    /// <summary>Whether there are tags to answer.</summary>
    public bool HasTags => Tags.Count > 0;

    /// <summary>Whether there are disagreements to look at.</summary>
    public bool HasDisagreements => Disagreements.Count > 0;

    /// <summary>Whether any mod cannot be brought in.</summary>
    public bool HasProblems => Problems.Count > 0;

    /// <summary>Whether there is anything to write.</summary>
    public bool CanApply => _plan is { } plan && plan.Changes.Count > 0;

    /// <summary>What the button that writes it says; without a count while there is no plan.</summary>
    public string ApplyText => _plan is { } plan
        ? _text.Format(nameof(Strings.Import_Apply), _text.Mods(plan.Changes.Count))
        : _text[nameof(Strings.Import_Heading_Plain)];

    /// <summary>Said instead of the lists when everything is already in.</summary>
    public bool ShowNothing => _plan is { } plan && plan.Changes.Count == 0;

    /// <summary>Whether the counts of what comes in are shown: there is a plan, and it brings something.</summary>
    public bool ShowFills => _plan is { } plan && plan.Changes.Count > 0;

    /// <summary>Counts what could be brought in, without reading any hashes, for the Mods page's card.</summary>
    public async Task RefreshAvailabilityAsync(CancellationToken cancellationToken)
    {
        if (_game.ModsDirectory is not { Length: > 0 } mods || _game.Data is not { } data)
        {
            AvailableCount = 0;
            return;
        }

        ForgetOtherGames(data);

        var plan = await _importer.PlanAsync(mods, data, _choices, compareHashes: false, cancellationToken)
            .ConfigureAwait(true);

        AvailableCount = plan.Changes.Count;
        AvailableText = _text.ForCount(plan.Changes.Count, nameof(Strings.Mods_Import_Body_One), nameof(Strings.Mods_Import_Body), _text.Mods(plan.Changes.Count), Sources(plan));
    }

    /// <summary>Opens the panel and works out what the import would bring in, hashes compared.</summary>
    public Task OpenAsync()
    {
        IsOpen = true;
        _game.PropertyChanged -= OnGameChanged;
        _game.PropertyChanged += OnGameChanged;
        _manager.PropertyChanged -= OnManagerChanged;
        _manager.PropertyChanged += OnManagerChanged;

        return PlanAsync();
    }

    /// <summary>Closes the panel without writing anything.</summary>
    [RelayCommand]
    public void Close()
    {
        _game.PropertyChanged -= OnGameChanged;
        _manager.PropertyChanged -= OnManagerChanged;
        Picker.Cancel();
        IsOpen = false;
    }

    private void OnManagerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CharacterManagerViewModel.IsOpen))
        {
            OnPropertyChanged(nameof(IsPanelVisible));
        }
    }

    /// <summary>Writes what the plan brings in, then opens the sort review when a character came in.</summary>
    [RelayCommand]
    private Task ApplyAsync()
    {
        if (!CanApply || _plan is not { } plan)
        {
            return Task.CompletedTask;
        }

        var hashesDecide = Disagreements.Where(row => !row.FollowTag).Select(row => row.Row.ModFolder)
            .ToHashSet(StringComparer.Ordinal);
        var rows = plan.Rows
            .Select(row => hashesDecide.Contains(row.ModFolder) ? row with { FollowTag = false } : row)
            .ToList();

        Close();

        return _work.RunAsync(
            _text[nameof(Strings.Import_Heading_Plain)],
            async ct =>
            {
                var result = await _importer.ApplyAsync(rows, ct).ConfigureAwait(true);

                await _rescan(ct).ConfigureAwait(true);

                _notifications.Add(
                    result.Failures.Count > 0 ? NotificationSeverity.Warning : NotificationSeverity.Information,
                    _text[nameof(Strings.Import_Heading_Plain)],
                    _text.Format(nameof(Strings.Import_Done), _text.Mods(result.Written.Count)) +
                    (result.Failures.Count > 0
                        ? " " + _text.ForCount(result.Failures.Count, nameof(Strings.Import_Failures_One), nameof(Strings.Import_Failures), _text.Mods(result.Failures.Count))
                        : string.Empty));

                // Exact on purpose: two rows may be folders differing only by capitals.
                foreach (var (folder, error) in result.Failures)
                {
                    _notifications.Add(
                        NotificationSeverity.Error,
                        rows.First(row => row.ModFolder == folder).ModName,
                        error);
                }

                await RefreshAvailabilityAsync(ct).ConfigureAwait(true);

                if (rows.Any(row => row.FollowTag && row.Fills.Contains("character") && result.Written.Contains(row.ModFolder)))
                {
                    await _sortReview.OpenAsync(null, null, ct).ConfigureAwait(true);
                }
            },
            CancellationToken.None);
    }

    /// <summary>Opens the picker for a tag.</summary>
    internal void ChooseFor(ImportTagViewModel tag)
    {
        if (_game.Data is not { } data)
        {
            return;
        }

        Picker.Open(
            _text.Format(nameof(Strings.Import_Picker_Heading), tag.Tag),
            [
                MoveTargetViewModel.Others(_text[nameof(Strings.Characters_Others)]),
                .. data.VisibleVariants
                    .Select(variant => new MoveTargetViewModel(variant, null))
                    .OrderBy(target => target.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            ],
            async target =>
            {
                _choices[tag.Tag] = target.InternalName;
                await PlanAsync().ConfigureAwait(true);
            });
    }

    /// <summary>Opens the Character Manager on a new character named after a tag.</summary>
    internal void CreateFor(ImportTagViewModel tag) => _manager.NewCharacter(tag.Tag);

    /// <summary>Forgets a tag's chosen character.</summary>
    internal Task ForgetAsync(ImportTagViewModel tag)
    {
        _choices.Remove(tag.Tag);
        return PlanAsync();
    }

    private async Task PlanAsync()
    {
        if (_game.ModsDirectory is not { Length: > 0 } mods || _game.Data is not { } data)
        {
            return;
        }

        ForgetOtherGames(data);

        // Only the last plan asked for is shown; the button waits for it.
        var version = ++_planVersion;
        var choices = new Dictionary<string, string>(_choices, StringComparer.OrdinalIgnoreCase);

        if (_plan is not null)
        {
            _unticked = Disagreements.Where(row => !row.FollowTag).Select(row => row.Row.ModFolder)
                .ToHashSet(StringComparer.Ordinal);
        }

        _plan = null;
        Fills.Clear();
        Tags.Clear();
        Disagreements.Clear();
        Problems.Clear();
        IsPlanning = true;
        RaiseLists();

        ModImportPlan? plan = null;

        await _work.RunAsync(
            _text[nameof(Strings.Import_Heading_Plain)],
            async ct => plan = await _importer.PlanAsync(mods, data, choices, compareHashes: true, ct).ConfigureAwait(true),
            CancellationToken.None).ConfigureAwait(true);

        if (version != _planVersion)
        {
            return;
        }

        IsPlanning = false;

        if (plan is not null)
        {
            Show(plan, data);
        }
    }

    private void Show(ModImportPlan plan, Xxsm.Packs.Merge.GameData data)
    {
        var unticked = _unticked;

        _plan = plan;
        Heading = _text.Format(nameof(Strings.Import_Heading), Sources(plan));

        Fills.Clear();

        foreach (var (key, label) in FillLabels)
        {
            var count = plan.Changes.Count(row => row.Fills.Contains(key));

            if (count > 0)
            {
                Fills.Add(_text.Format(nameof(Strings.Import_Fill_Line), _text[label], _text.Mods(count)));
            }
        }

        Tags.Clear();

        foreach (var tag in plan.UnmatchedTags)
        {
            Tags.Add(new ImportTagViewModel(tag.Tag, _text.Mods(tag.ModCount), null, this));
        }

        foreach (var (tag, internalName) in _choices.OrderBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            var count = plan.Rows.Count(row => string.Equals(row.Details.Character, tag, StringComparison.OrdinalIgnoreCase));

            var chosenName = data.Find(internalName)?.DisplayName
                             ?? (PathComparer.AreNamesEqual(internalName, ModsFolderLayout.UnsortedFolderName)
                                 ? _text[nameof(Strings.Characters_Others)]
                                 : null);

            if (count > 0 && chosenName is not null)
            {
                Tags.Add(new ImportTagViewModel(tag, _text.Mods(count), chosenName, this));
            }
        }

        Disagreements.Clear();

        foreach (var row in plan.Disagreements)
        {
            Disagreements.Add(new ImportDisagreementViewModel(row, _text) { FollowTag = !unticked.Contains(row.ModFolder) });
        }

        Problems.Clear();

        foreach (var row in plan.Rows.Where(row => row.Problem is not null))
        {
            Problems.Add(row.ModName + ": " + row.Problem);
        }

        RaiseLists();
    }

    private void RaiseLists()
    {
        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(HasDisagreements));
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(ApplyText));
        OnPropertyChanged(nameof(ShowNothing));
        OnPropertyChanged(nameof(ShowFills));
    }

    private string Sources(ModImportPlan plan) =>
        (plan.CountFrom(ModImportSource.Jasm) > 0, plan.CountFrom(ModImportSource.XxModManager) > 0) switch
        {
            (true, true) => _text[nameof(Strings.Import_Source_Both)],
            (true, false) => _text[nameof(Strings.Import_Source_Jasm)],
            _ => _text[nameof(Strings.Import_Source_XxModManager)],
        };

    /// <summary>Drops the tag choices and unticked rows when the game is not the one they were made in.</summary>
    private void ForgetOtherGames(Xxsm.Packs.Merge.GameData data)
    {
        if (string.Equals(_choicesGameId, data.GameId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _choices.Clear();
        _unticked.Clear();
        _plan = null;
        _choicesGameId = data.GameId;
    }

    private void OnGameChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A character made for a tag reloads the game; the tag may now name it by itself.
        if (e.PropertyName == nameof(GameContext.Data) && IsOpen)
        {
            Planning = PlanAsync();
        }
    }

    /// <summary>The plan started by a reload of the game, for a test to wait on.</summary>
    internal Task Planning { get; private set; } = Task.CompletedTask;

    private static readonly (string Key, string Label)[] FillLabels =
    [
        ("character", nameof(Strings.Import_Fill_Character)),
        ("link", nameof(Strings.Import_Fill_Link)),
        ("name", nameof(Strings.Import_Fill_Name)),
        ("description", nameof(Strings.Import_Fill_Description)),
        ("notes", nameof(Strings.Import_Fill_Notes)),
        ("author", nameof(Strings.Import_Fill_Author)),
        ("version", nameof(Strings.Import_Fill_Version)),
        ("picture", nameof(Strings.Import_Fill_Picture)),
        ("tags", nameof(Strings.Import_Fill_Tags)),
        ("date", nameof(Strings.Import_Fill_Date)),
    ];
}
