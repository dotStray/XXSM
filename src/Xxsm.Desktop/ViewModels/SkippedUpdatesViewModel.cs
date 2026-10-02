using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Packs.Characters;
using Xxsm.Packs.Merge;
using Xxsm.Packs.Registry;

namespace Xxsm.Desktop.ViewModels;

/// <summary>One field a pack update would have changed, and did not.</summary>
public sealed partial class SkippedFieldViewModel : ObservableObject
{
    private readonly ITextCatalogue _text;

    /// <summary>Creates the row.</summary>
    /// <param name="internalName">The variant the field belongs to.</param>
    /// <param name="change">The withheld change.</param>
    /// <param name="text">The interface's wording.</param>
    public SkippedFieldViewModel(string internalName, FieldChange change, ITextCatalogue text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(internalName);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(text);

        InternalName = internalName;
        Change = change;
        _text = text;
    }

    /// <summary>The variant this field belongs to.</summary>
    public string InternalName { get; }

    /// <summary>The withheld change.</summary>
    public FieldChange Change { get; }

    /// <summary>The field's name, as the overlay spells it.</summary>
    public string Field => Change.Field;

    /// <summary>What the previous pack had, or that it had nothing.</summary>
    public string PackOldText => Describe(Change.PackOld);

    /// <summary>What the new pack has.</summary>
    public string PackNewText => Describe(Change.PackNew);

    /// <summary>What the user's own value is.</summary>
    public string UserText => Describe(Change.UserValue);

    /// <summary>Whether this field is being taken from the pack after all.</summary>
    [ObservableProperty]
    private bool _isSelected;

    private string Describe(string? value) =>
        string.IsNullOrWhiteSpace(value) ? _text[nameof(Strings.SkippedUpdates_Nothing)] : value;
}

/// <summary>One variant a pack update was withheld from.</summary>
public sealed class SkippedVariantViewModel
{
    /// <summary>Creates the group.</summary>
    /// <param name="skipped">The withheld changes for one variant.</param>
    /// <param name="text">The interface's wording.</param>
    public SkippedVariantViewModel(SkippedUpdate skipped, ITextCatalogue text)
    {
        ArgumentNullException.ThrowIfNull(skipped);
        ArgumentNullException.ThrowIfNull(text);

        InternalName = skipped.InternalName;
        DisplayName = skipped.DisplayName is { Length: > 0 } name ? name : skipped.InternalName;

        foreach (var change in skipped.Changes)
        {
            Fields.Add(new SkippedFieldViewModel(skipped.InternalName, change, text));
        }
    }

    /// <summary>The variant's id.</summary>
    public string InternalName { get; }

    /// <summary>The variant's name, as the new pack calls it.</summary>
    public string DisplayName { get; }

    /// <summary>The fields that were withheld, one row each.</summary>
    public ObservableCollection<SkippedFieldViewModel> Fields { get; } = [];
}

/// <summary>The <em>Skipped updates</em> review: each withheld change, field by field, until dismissed.</summary>
public sealed partial class SkippedUpdatesViewModel(
    GameContext game,
    ICharacterEditor characters,
    ISkippedUpdatesStore store,
    ViewModelWorkRunner work,
    ITextCatalogue text,
    Func<CancellationToken, Task> reload) : ObservableObject
{
    private readonly GameContext _game = game;
    private readonly ICharacterEditor _characters = characters;
    private readonly ISkippedUpdatesStore _store = store;
    private readonly ViewModelWorkRunner _work = work;
    private readonly ITextCatalogue _text = text;
    private readonly Func<CancellationToken, Task> _reload = reload;

    /// <summary>Whether the review is on screen.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>The pack version the withheld changes came from.</summary>
    [ObservableProperty]
    private string? _packVersion;

    /// <summary>The game the withheld changes belong to.</summary>
    [ObservableProperty]
    private string? _gameId;

    /// <summary>That game's name, for the line that names it. Its id when nothing better is known.</summary>
    [ObservableProperty]
    private string? _gameName;

    /// <summary>One group per variant, each with its withheld fields.</summary>
    public ObservableCollection<SkippedVariantViewModel> Variants { get; } = [];

    /// <summary>The panel's title.</summary>
    public string Heading => _text[nameof(Strings.SkippedUpdates_Heading)];

    /// <summary>What happened, in one line.</summary>
    public string SummaryText => _text.Format(
        nameof(Strings.SkippedUpdates_Summary),
        _text.Characters(Variants.Count),
        PackVersionText.Display(PackVersion));

    /// <summary>Whether there is anything to show.</summary>
    public bool HasVariants => Variants.Count > 0;

    /// <summary>How many fields the user has ticked to take.</summary>
    public int SelectedCount => Variants.SelectMany(variant => variant.Fields).Count(entry => entry.IsSelected);

    /// <summary>The label on the button that takes them.</summary>
    public string TakeText => SelectedCount == 0
        ? _text[nameof(Strings.SkippedUpdates_TakeNone)]
        : _text.Format(nameof(Strings.SkippedUpdates_Take), _text.Fields(SelectedCount));

    /// <summary>Whether these changes belong to the selected game; only then can a field be taken.</summary>
    public bool IsForSelectedGame =>
        GameId is not { Length: > 0 } shown
        || string.Equals(shown, _game.GameId, StringComparison.OrdinalIgnoreCase);

    /// <summary>The line shown when the review belongs to a game that is not selected.</summary>
    public string OtherGameText => _text.Format(
        nameof(Strings.SkippedUpdates_OtherGame),
        GameName is { Length: > 0 } name ? name : GameId ?? string.Empty);

    /// <summary>Whether that line is worth drawing.</summary>
    public bool IsOtherGame => !IsForSelectedGame;

    /// <summary>Whether anything is ticked, for a game that can be edited from here.</summary>
    public bool CanTake => SelectedCount > 0 && IsForSelectedGame;

    /// <summary>Shows the changes a pack update withheld.</summary>
    /// <param name="skipped">The withheld changes, from the update's result or from the record.</param>
    /// <param name="packVersion">The pack version they came from.</param>
    /// <param name="gameId">The game they belong to.</param>
    /// <param name="gameName">That game's name, for the line that names it.</param>
    public void Show(
        IReadOnlyList<SkippedUpdate> skipped, string? packVersion, string? gameId = null, string? gameName = null)
    {
        ArgumentNullException.ThrowIfNull(skipped);

        Clear();

        GameId = gameId;
        GameName = gameName;

        foreach (var variant in skipped)
        {
            var group = new SkippedVariantViewModel(variant, _text);

            foreach (var entry in group.Fields)
            {
                entry.PropertyChanged += OnFieldChanged;
            }

            Variants.Add(group);
        }

        PackVersion = packVersion;
        IsOpen = Variants.Count > 0;
        Raise();
    }

    /// <summary>Takes the pack's version of each ticked field by resetting the override, to track the pack.</summary>
    [RelayCommand]
    private Task TakeSelectedAsync()
    {
        if (_game.Data is not { } data || SelectedCount == 0)
        {
            return Task.CompletedTask;
        }

        var byVariant = Variants
            .Select(variant => (
                variant.InternalName,
                Fields: variant.Fields.Where(entry => entry.IsSelected).Select(entry => entry.Field).ToList()))
            .Where(pair => pair.Fields.Count > 0)
            .ToList();

        return _work.RunAsync(
            Heading,
            async ct =>
            {
                foreach (var (internalName, fields) in byVariant)
                {
                    await _characters.ResetAsync(data, internalName, fields, ct).ConfigureAwait(true);
                }

                await RecordRemainingAsync(ct).ConfigureAwait(true);

                Dismiss();
                await _reload(ct).ConfigureAwait(true);
            },
            CancellationToken.None);
    }

    /// <summary>Ticks every field, so the whole update is taken.</summary>
    [RelayCommand]
    private void TakeAll()
    {
        foreach (var entry in Variants.SelectMany(variant => variant.Fields))
        {
            entry.IsSelected = true;
        }
    }

    /// <summary>Closes the review, keeping every one of the user's own values.</summary>
    [RelayCommand]
    public void Dismiss()
    {
        IsOpen = false;
        Clear();
        Raise();
    }

    /// <summary>Writes back what is still withheld, so reopening shows what is left; nothing left clears it.</summary>
    private Task RecordRemainingAsync(CancellationToken cancellationToken)
    {
        if (GameId is not { Length: > 0 } gameId)
        {
            return Task.CompletedTask;
        }

        var remaining = Variants
            .Select(variant => new SkippedUpdate(
                variant.InternalName,
                variant.DisplayName,
                [.. variant.Fields.Where(entry => !entry.IsSelected).Select(entry => entry.Change)]))
            .Where(update => update.Changes.Count > 0)
            .ToList();

        return _store.SaveAsync(
            new SkippedUpdateRecord
            {
                GameId = gameId,
                PackVersion = PackVersion,
                RecordedAt = DateTimeOffset.UtcNow,
                Updates = remaining,
            },
            cancellationToken);
    }

    private void Clear()
    {
        foreach (var entry in Variants.SelectMany(variant => variant.Fields))
        {
            entry.PropertyChanged -= OnFieldChanged;
        }

        Variants.Clear();
        PackVersion = null;
        GameId = null;
        GameName = null;
    }

    private void OnFieldChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SkippedFieldViewModel.IsSelected))
        {
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(TakeText));
            OnPropertyChanged(nameof(CanTake));
        }
    }

    private void Raise()
    {
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(IsForSelectedGame));
        OnPropertyChanged(nameof(IsOtherGame));
        OnPropertyChanged(nameof(OtherGameText));
        OnPropertyChanged(nameof(HasVariants));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(TakeText));
        OnPropertyChanged(nameof(CanTake));
    }

    partial void OnPackVersionChanged(string? value) => OnPropertyChanged(nameof(SummaryText));

    partial void OnGameNameChanged(string? value) => OnPropertyChanged(nameof(OtherGameText));

    partial void OnGameIdChanged(string? value)
    {
        OnPropertyChanged(nameof(IsForSelectedGame));
        OnPropertyChanged(nameof(IsOtherGame));
        OnPropertyChanged(nameof(OtherGameText));
        OnPropertyChanged(nameof(CanTake));
    }
}
