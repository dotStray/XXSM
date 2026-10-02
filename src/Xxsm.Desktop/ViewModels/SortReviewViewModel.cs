using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Core.Settings;
using Xxsm.Desktop.Services;
using Xxsm.Packs.Sorting;

namespace Xxsm.Desktop.ViewModels;

/// <summary>Which of the sort review's moves are showing.</summary>
public enum SortReviewFilter
{
    /// <summary>Every move.</summary>
    All = 0,

    /// <summary>Only the mods whose hashes identified their character.</summary>
    Hash,

    /// <summary>Only the mods a file name or the mod's own name placed.</summary>
    Name,

    /// <summary>Only the mods nothing identified, which are going to <c>Others</c>.</summary>
    Others,
}

/// <summary>One row of the sort review: a mod, where it is going, and why.</summary>
public sealed partial class SortRowViewModel(
    SortRunRow row,
    ITextCatalogue text,
    bool canBeRemembered) : ObservableObject
{
    private readonly ITextCatalogue _text = text;

    /// <summary>The plan row. Applying a run hands these back, not the view models.</summary>
    public SortRunRow Row { get; } = row;

    /// <summary>Whether unticking keeps the mod where it is for good: only when it is under a character.</summary>
    public bool CanBeRemembered { get; } = canBeRemembered;

    /// <summary>Whether this row will be applied. Every row starts ticked and can be unticked.</summary>
    [ObservableProperty]
    private bool _isSelected = true;

    /// <summary>The mod's name.</summary>
    public string Name => Row.Mod.DisplayName;

    /// <summary>Where it is now, or that it is loose at the top of the Mods folder.</summary>
    public string FromText => Row.Mod.VariantFolderName is { Length: > 0 } folder
        ? folder
        : _text[nameof(Strings.SortReview_Unfiled)];

    /// <summary>The character folder it would move to.</summary>
    public string ToText => Row.DestinationFolderName;

    /// <summary>The sorter's own account of the decision.</summary>
    public string Reason => Row.Reason;

    /// <summary>Whether the outfit within the family is a guess rather than a hash match.</summary>
    public bool IsUncertain => Row.VariantIsUncertain;

    /// <summary>What kind of guess it is, for the badge: a name picked the outfit, or the default was used.</summary>
    public string CertaintyText => Row.Decision.MemberDecidedByName
        ? _text[nameof(Strings.SortReview_Guess_Name)]
        : Row.Decision.IsDefaultVariantFallback
            ? _text[nameof(Strings.SortReview_Guess_Default)]
            : _text[nameof(Strings.SortReview_Certain)];

    /// <summary>Whether the mod folder would be renamed on the way, and to what.</summary>
    public bool HasHashes => Row.Decision.ExtractedHashes.Count > 0;

    /// <summary>How many hashes were found in the mod, for the row's detail line.</summary>
    public string HashesText =>
        _text.Format(nameof(Strings.SortReview_Hashes), _text.Hashes(Row.Decision.ExtractedHashes.Count));
}

/// <summary>The sort review: what a sort would do, before it does anything; only ticked rows move.</summary>
public sealed partial class SortReviewViewModel(
    GameContext game,
    ISortRunner runner,
    IAppSettingsStore settings,
    INotificationService notifications,
    ViewModelWorkRunner work,
    ITextCatalogue text,
    Func<CancellationToken, Task> rescan) : ObservableObject
{
    private readonly GameContext _game = game;
    private readonly ISortRunner _runner = runner;
    private readonly IAppSettingsStore _settings = settings;
    private readonly INotificationService _notifications = notifications;
    private readonly ViewModelWorkRunner _work = work;
    private readonly ITextCatalogue _text = text;
    private readonly Func<CancellationToken, Task> _rescan = rescan;

    private SortRunPlan? _plan;

    /// <summary>Whether the review is on screen.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>Whether a plan is being worked out.</summary>
    [ObservableProperty]
    private bool _isPlanning;

    /// <summary>The character the review is narrowed to, or null for the whole folder.</summary>
    [ObservableProperty]
    private string? _characterName;

    /// <summary>Whether everything staying put is showing.</summary>
    [ObservableProperty]
    private bool _isUnchangedExpanded;

    /// <summary>The mods that would move, each with a tickbox.</summary>
    public ObservableCollection<SortRowViewModel> Moves { get; } = [];

    /// <summary>Which of the moves are showing.</summary>
    [ObservableProperty]
    private SortReviewFilter _filter;

    /// <summary>The moves the filter lets through; the buttons and applying act on these alone.</summary>
    public ObservableCollection<SortRowViewModel> ShownMoves { get; } = [];

    /// <summary>Whether every move is showing.</summary>
    public bool IsAllFilter => Filter == SortReviewFilter.All;

    /// <summary>Whether only hash matches are showing.</summary>
    public bool IsHashFilter => Filter == SortReviewFilter.Hash;

    /// <summary>Whether only name matches are showing.</summary>
    public bool IsNameFilter => Filter == SortReviewFilter.Name;

    /// <summary>Whether only the mods going to Others are showing.</summary>
    public bool IsOthersFilter => Filter == SortReviewFilter.Others;

    /// <summary>The label on the chip that shows every move.</summary>
    public string FilterAllText => _text.Format(nameof(Strings.SortReview_Filter_All), Moves.Count);

    /// <summary>The label on the chip that shows hash matches.</summary>
    public string FilterHashText => _text.Format(nameof(Strings.SortReview_Filter_Hash), CountOf(SortMatchKind.Hash));

    /// <summary>The label on the chip that shows name matches.</summary>
    public string FilterNameText => _text.Format(nameof(Strings.SortReview_Filter_Name), CountOf(SortMatchKind.Name));

    /// <summary>The label on the chip that shows what is going to Others.</summary>
    public string FilterOthersText =>
        _text.Format(nameof(Strings.SortReview_Filter_Others), CountOf(SortMatchKind.Others));

    /// <summary>Whether any move was found by hash, so its chip can be pressed.</summary>
    public bool HasHashMoves => CountOf(SortMatchKind.Hash) > 0;

    /// <summary>Whether any move was found by name.</summary>
    public bool HasNameMoves => CountOf(SortMatchKind.Name) > 0;

    /// <summary>Whether anything is going to Others.</summary>
    public bool HasOthersMoves => CountOf(SortMatchKind.Others) > 0;

    /// <summary>The mods that would stay where they are.</summary>
    public ObservableCollection<SortRowViewModel> Unchanged { get; } = [];

    /// <summary>The panel's title.</summary>
    public string Heading => CharacterName is { Length: > 0 } character
        ? _text.Format(nameof(Strings.SortReview_Heading_Character), character)
        : _text[nameof(Strings.SortReview_Heading)];

    /// <summary>The count line: how many were looked at, would move, and are already filed.</summary>
    public string SummaryText => _text.Format(
        nameof(Strings.SortReview_Summary),
        _text.Mods(Moves.Count + Unchanged.Count),
        _text.Mods(Moves.Count),
        _text.Mods(Unchanged.Count));

    /// <summary>The header on the collapsed "staying put" section.</summary>
    public string UnchangedText =>
        _text.Format(nameof(Strings.SortReview_Unchanged), _text.Mods(Unchanged.Count));

    /// <summary>How many of the showing rows are ticked.</summary>
    public int SelectedCount => ShownMoves.Count(row => row.IsSelected);

    /// <summary>How many showing rows are unticked and under a character: kept and remembered by the button.</summary>
    public int KeepCount => ShownMoves.Count(row => !row.IsSelected && row.CanBeRemembered);

    /// <summary>How many showing rows are unticked but loose or in <c>Others</c>: offered again next time.</summary>
    public int UntickedUnsortedCount => ShownMoves.Count(row => !row.IsSelected && !row.CanBeRemembered);

    /// <summary>The apply button's label: the moves when there are any, else the keeping.</summary>
    public string ApplyText => SelectedCount == 0 && KeepCount > 0
        ? _text.Format(nameof(Strings.SortReview_Keep), _text.Mods(KeepCount))
        : _text.Format(nameof(Strings.SortReview_Apply), _text.Mods(SelectedCount));

    /// <summary>Whether there is anything to move or to keep.</summary>
    public bool CanApply => (SelectedCount > 0 || KeepCount > 0) && !IsPlanning;

    /// <summary>What unticking rows under a character will do, or null when none are unticked.</summary>
    public string? KeepNoteText => KeepCount > 0
        ? _text.Format(nameof(Strings.SortReview_KeepNote), _text.Mods(KeepCount))
        : null;

    /// <summary>What unticking loose or <c>Others</c> rows will do, or null when none are unticked.</summary>
    public string? UnsortedNoteText => UntickedUnsortedCount > 0
        ? _text.Format(nameof(Strings.SortReview_UnsortedNote), _text.Mods(UntickedUnsortedCount))
        : null;

    /// <summary>Whether any mod would move at all.</summary>
    public bool HasMoves => Moves.Count > 0;

    /// <summary>Whether anything is staying put, so the collapsed section is worth showing.</summary>
    public bool HasUnchanged => Unchanged.Count > 0;

    /// <summary>Whether the plan found nothing to do.</summary>
    public bool IsEmpty => !IsPlanning && Moves.Count == 0;

    /// <summary>Works out what a sort would do, and shows it. Changes nothing.</summary>
    /// <param name="internalName">A character to narrow to, or null for the whole Mods folder.</param>
    /// <param name="displayName">That character's name, for the title.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    public Task OpenAsync(
        string? internalName,
        string? displayName,
        CancellationToken cancellationToken) =>
        OpenCoreAsync(
            displayName,
            (plan, data) => internalName is { Length: > 0 } ? plan.ForCharacter(data, internalName) : plan,
            cancellationToken);

    /// <summary>Shows what a sort would do with the mods no character owns (Unsorted). Changes nothing.</summary>
    public Task OpenUnsortedAsync(CancellationToken cancellationToken) =>
        OpenCoreAsync(
            _text[nameof(Strings.Characters_Unsorted)],
            (plan, data) => plan.ForUnsorted(data),
            cancellationToken);

    /// <summary>Shows what a sort would do with the mods in <c>Others</c>: the Others tile. Changes nothing.</summary>
    public Task OpenOthersAsync(CancellationToken cancellationToken) =>
        OpenCoreAsync(
            _text[nameof(Strings.Characters_Others)],
            (plan, data) => plan.ForOthers(data),
            cancellationToken);

    private async Task OpenCoreAsync(
        string? displayName,
        Func<SortRunPlan, Xxsm.Packs.Merge.GameData, SortRunPlan> narrow,
        CancellationToken cancellationToken)
    {
        if (_game.Data is not { } data || _game.ModsDirectory is not { Length: > 0 } modsDirectory)
        {
            return;
        }

        Clear();
        CharacterName = displayName;
        IsOpen = true;
        IsPlanning = true;
        RaiseCounts();

        // A plan that comes back after the review closed or reopened is not this review's answer.
        var opening = ++_opening;

        var planned = await _work.RunAsync(
            Heading,
            async ct =>
            {
                var saved = await _settings.ReadAsync(ct).ConfigureAwait(true);

                var plan = await _runner
                    .PlanAsync(modsDirectory, data, saved.Sort.ToSortSettings(), ct)
                    .ConfigureAwait(true);

                if (opening != _opening)
                {
                    return;
                }

                // The whole folder is planned, since a destination is right only relative to the Mods root; the rows
                // narrow.
                plan = narrow(plan, data);

                _plan = plan;

                foreach (var row in plan.Rows)
                {
                    var view = new SortRowViewModel(
                        row, _text, UnsortedMods.CharacterFor(row.Mod.VariantFolderName, data) is not null);
                    view.PropertyChanged += OnRowChanged;

                    (row.WillMove ? Moves : Unchanged).Add(view);
                }
            },
            cancellationToken).ConfigureAwait(true);

        if (opening != _opening)
        {
            return;
        }

        IsPlanning = false;
        RefreshShown();

        // A failed or cancelled scan must not leave "nothing to move" on screen.
        if (!planned)
        {
            Close();
            return;
        }

        RaiseCounts();
    }

    /// <summary>Moves the mods whose rows are still ticked.</summary>
    [RelayCommand]
    private Task ApplyAsync()
    {
        if (_plan is not { } plan || !CanApply || _game.Data is not { } data)
        {
            return Task.CompletedTask;
        }

        var rows = ShownMoves.Where(row => row.IsSelected).Select(row => row.Row).ToList();

        // Unticked under a character is remembered, so the next sort does not ask again.
        var unticked = ShownMoves.Where(row => !row.IsSelected).Select(row => row.Row).ToList();

        return _work.RunAsync(
            Heading,
            async ct =>
            {
                if (unticked.Count > 0)
                {
                    ReportKept(await _runner.KeepAsync(unticked, data, ct).ConfigureAwait(true));
                }

                if (rows.Count == 0)
                {
                    Close();
                    await _rescan(ct).ConfigureAwait(true);
                    return;
                }

                var result = await _runner.ApplyAsync(plan.ModsDirectory, rows, ct).ConfigureAwait(true);

                Close();
                await _rescan(ct).ConfigureAwait(true);

                // The undo offer lives on the notice: the review has closed by now.
                Notification? notice = null;

                var undo = result.MovedCount > 0
                    ? new AsyncRelayCommand(async () =>
                    {
                        var ok = await UndoAsync(plan.ModsDirectory, result.RunId).ConfigureAwait(true);

                        if (ok && notice is not null)
                        {
                            // The offer is spent; a failed undo keeps its button.
                            _notifications.Dismiss(notice);
                        }
                    })
                    : null;

                notice = _notifications.Add(
                    result.Failures.Count > 0 ? NotificationSeverity.Warning : NotificationSeverity.Information,
                    _text[nameof(Strings.SortReview_Heading)],
                    result.Failures.Count > 0
                        ? _text.Format(
                            nameof(Strings.SortReview_Done_WithFailures),
                            _text.Mods(result.MovedCount),
                            _text.Mods(result.Failures.Count),
                            result.RunId)
                        : _text.Format(
                            nameof(Strings.SortReview_Done), _text.Mods(result.MovedCount), result.RunId),
                    action: undo,
                    actionText: undo is null ? null : _text[nameof(Strings.Notifications_Undo)]);

                foreach (var failure in result.Failures)
                {
                    _notifications.Add(
                        NotificationSeverity.Error, failure.Row.Mod.DisplayName, failure.Error!);
                }
            },
            CancellationToken.None);
    }

    /// <summary>Puts a whole sort run back, by its id rather than the latest; what cannot come back is named.</summary>
    private Task<bool> UndoAsync(string modsDirectory, string runId) => _work.RunAsync(
        _text[nameof(Strings.Notifications_Undo)],
        async ct =>
        {
            var undone = await _runner.UndoAsync(modsDirectory, runId, ct).ConfigureAwait(true);

            await _rescan(ct).ConfigureAwait(true);

            _notifications.Add(
                undone.Skipped.Count > 0 ? NotificationSeverity.Warning : NotificationSeverity.Information,
                _text[nameof(Strings.Notifications_Undo)],
                undone.Skipped.Count > 0
                    ? _text.ForCount(undone.Skipped.Count, nameof(Strings.SortReview_Undone_WithSkips_One), nameof(Strings.SortReview_Undone_WithSkips),
                        _text.Mods(undone.RestoredCount),
                        _text.Mods(undone.Skipped.Count))
                    : _text.Format(
                        nameof(Strings.SortReview_Undone), _text.Mods(undone.RestoredCount)));

            foreach (var skipped in undone.Skipped)
            {
                _notifications.Add(NotificationSeverity.Warning, skipped.From, skipped.Note!);
            }
        },
        CancellationToken.None);

    /// <summary>Says what keeping the unticked rows did.</summary>
    private void ReportKept(SortKeepResult kept)
    {
        if (kept.Remembered.Count > 0)
        {
            _notifications.Add(
                NotificationSeverity.Information,
                _text[nameof(Strings.SortReview_Heading)],
                _text.Format(nameof(Strings.SortReview_Kept), _text.Mods(kept.Remembered.Count)));
        }

        foreach (var failure in kept.Failures)
        {
            _notifications.Add(NotificationSeverity.Error, failure.Row.Mod.DisplayName, failure.Error);
        }
    }

    /// <summary>Ticks every row.</summary>
    [RelayCommand]
    private void SelectAll() => SetAll(selected: true);

    /// <summary>Unticks every row.</summary>
    [RelayCommand]
    private void SelectNone() => SetAll(selected: false);

    /// <summary>Shows or hides the mods that are staying put.</summary>
    [RelayCommand]
    private void ToggleUnchanged() => IsUnchangedExpanded = !IsUnchangedExpanded;

    /// <summary>Shows only the moves found one way, or all of them. The chips above the list.</summary>
    [RelayCommand]
    private void ShowOnly(SortReviewFilter filter)
    {
        Filter = filter;

        // Pressing the chosen chip unticks it on screen; saying the choice again ticks it back.
        RaiseFilterChoice();
    }

    /// <summary>Closes the review without moving anything.</summary>
    [RelayCommand]
    public void Close()
    {
        _opening++;
        IsOpen = false;
        IsPlanning = false;
        Clear();
        RaiseCounts();
    }

    // Counts openings and closings, so a late plan knows it is no longer wanted.
    private int _opening;

    private void SetAll(bool selected)
    {
        foreach (var row in ShownMoves)
        {
            row.IsSelected = selected;
        }
    }

    private int CountOf(SortMatchKind kind) => Moves.Count(row => row.Row.MatchKind == kind);

    private void RefreshShown()
    {
        ShownMoves.Clear();

        foreach (var row in Moves.Where(row => Filter switch
                 {
                     SortReviewFilter.Hash => row.Row.MatchKind == SortMatchKind.Hash,
                     SortReviewFilter.Name => row.Row.MatchKind == SortMatchKind.Name,
                     SortReviewFilter.Others => row.Row.MatchKind == SortMatchKind.Others,
                     _ => true,
                 }))
        {
            ShownMoves.Add(row);
        }

        RaiseSelection();
    }

    private void RaiseSelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(KeepCount));
        OnPropertyChanged(nameof(UntickedUnsortedCount));
        OnPropertyChanged(nameof(ApplyText));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(KeepNoteText));
        OnPropertyChanged(nameof(UnsortedNoteText));
    }

    private void RaiseFilterChoice()
    {
        OnPropertyChanged(nameof(IsAllFilter));
        OnPropertyChanged(nameof(IsHashFilter));
        OnPropertyChanged(nameof(IsNameFilter));
        OnPropertyChanged(nameof(IsOthersFilter));
    }

    private void Clear()
    {
        foreach (var row in Moves.Concat(Unchanged))
        {
            row.PropertyChanged -= OnRowChanged;
        }

        Moves.Clear();
        ShownMoves.Clear();
        Unchanged.Clear();
        _plan = null;
        CharacterName = null;
        IsUnchangedExpanded = false;
        Filter = SortReviewFilter.All;
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SortRowViewModel.IsSelected))
        {
            RaiseSelection();
        }
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(UnchangedText));
        RaiseSelection();
        OnPropertyChanged(nameof(HasMoves));
        OnPropertyChanged(nameof(HasUnchanged));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(FilterAllText));
        OnPropertyChanged(nameof(FilterHashText));
        OnPropertyChanged(nameof(FilterNameText));
        OnPropertyChanged(nameof(FilterOthersText));
        OnPropertyChanged(nameof(HasHashMoves));
        OnPropertyChanged(nameof(HasNameMoves));
        OnPropertyChanged(nameof(HasOthersMoves));
    }

    partial void OnFilterChanged(SortReviewFilter value)
    {
        RefreshShown();
        RaiseFilterChoice();
    }

    partial void OnIsPlanningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnCharacterNameChanged(string? value) => OnPropertyChanged(nameof(Heading));
}
