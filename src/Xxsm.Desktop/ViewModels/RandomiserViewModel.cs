using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Packs.Randomising;

namespace Xxsm.Desktop.ViewModels;

/// <summary>One character in the randomiser, and the mod chosen for it.</summary>
public sealed partial class RandomiserRowViewModel : ObservableObject
{
    private readonly ITextCatalogue _text;
    private readonly Action<RandomiserRowViewModel> _chooseAgain;

    /// <summary>Creates the row.</summary>
    /// <param name="row">The choice.</param>
    /// <param name="text">The interface's wording.</param>
    /// <param name="chooseAgain">Chooses another mod for this character.</param>
    internal RandomiserRowViewModel(RandomiserRow row, ITextCatalogue text, Action<RandomiserRowViewModel> chooseAgain)
    {
        _row = row;
        _text = text;
        _chooseAgain = chooseAgain;
    }

    /// <summary>The choice as it stands.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChosenName), nameof(Detail), nameof(CanChooseAgain))]
    private RandomiserRow _row;

    /// <summary>Whether this character is randomised when the button is pressed. Every row starts ticked.</summary>
    [ObservableProperty]
    private bool _isSelected = true;

    /// <summary>The character's name.</summary>
    public string CharacterName => Row.Character.DisplayName;

    /// <summary>The mod chosen for it.</summary>
    public string ChosenName => Row.Chosen.DisplayName;

    /// <summary>What else happens: how many of its mods it was chosen from, and how many go off.</summary>
    public string Detail
    {
        get
        {
            var from = _text.Format(nameof(Strings.Randomiser_Row_From), _text.Mods(Row.Folder.Mods.Count));

            return Row.SwitchedOff.Count > 0
                ? from + " · " + _text.Format(nameof(Strings.Randomiser_Row_Off), _text.Mods(Row.SwitchedOff.Count))
                : Row.ChangesAnything
                    ? from
                    : from + " · " + _text[nameof(Strings.Randomiser_Row_AlreadyOn)];
        }
    }

    /// <summary>Whether there is another mod to choose.</summary>
    public bool CanChooseAgain => Row.Folder.Mods.Count > 1;

    /// <summary>Chooses another mod for this character.</summary>
    [RelayCommand]
    private void ChooseAgain() => _chooseAgain(this);
}

/// <summary>The randomiser: one mod per character, chosen at random, shown before anything is switched.</summary>
public sealed partial class RandomiserViewModel(
    GameContext game,
    IModRandomiser randomiser,
    SwitchRunNotices notices,
    ViewModelWorkRunner work,
    ITextCatalogue text,
    Func<CancellationToken, Task> rescan) : ObservableObject
{
    private readonly GameContext _game = game;
    private readonly IModRandomiser _randomiser = randomiser;
    private readonly SwitchRunNotices _notices = notices;
    private readonly ViewModelWorkRunner _work = work;
    private readonly ITextCatalogue _text = text;
    private readonly Func<CancellationToken, Task> _rescan = rescan;

    // One per panel: a Random is not safe to share across threads.
    private readonly Random _random = new();

    private IReadOnlyCollection<string>? _onlyFolders;
    private string? _modsDirectory;

    /// <summary>Whether the panel is on screen.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>One row per character with a mod to choose.</summary>
    public ObservableCollection<RandomiserRowViewModel> Rows { get; } = [];

    /// <summary>Whether there is any character to randomise.</summary>
    public bool HasRows => Rows.Count > 0;

    /// <summary>What the button that switches them says.</summary>
    public string ApplyText => _text.Format(nameof(Strings.Randomiser_Apply), _text.Characters(ChosenRows.Count));

    /// <summary>Whether anything ticked would change.</summary>
    public bool CanApply => ChosenRows.Any(row => row.ChangesAnything);

    private List<RandomiserRow> ChosenRows => [.. Rows.Where(row => row.IsSelected).Select(row => row.Row)];

    /// <summary>Opens the panel with a fresh choice.</summary>
    /// <param name="onlyFolders">Character folders to limit it to, or <c>null</c> for every character.</param>
    public void Open(IReadOnlyCollection<string>? onlyFolders = null)
    {
        _onlyFolders = onlyFolders;
        Choose();
        IsOpen = true;
    }

    /// <summary>Closes the panel without switching anything.</summary>
    [RelayCommand]
    public void Close()
    {
        IsOpen = false;
        Clear();
        RaiseCounts();
    }

    /// <summary>Chooses again for every character, keeping which are ticked.</summary>
    [RelayCommand]
    private void ChooseAllAgain()
    {
        var unticked = Rows.Where(row => !row.IsSelected).Select(row => row.Row.Folder.Path).ToHashSet(StringComparer.Ordinal);

        Choose();

        foreach (var row in Rows.Where(row => unticked.Contains(row.Row.Folder.Path)))
        {
            row.IsSelected = false;
        }
    }

    /// <summary>Switches the ticked characters' mods.</summary>
    [RelayCommand]
    private Task ApplyAsync()
    {
        if (!CanApply || _modsDirectory is not { } mods)
        {
            return Task.CompletedTask;
        }

        var rows = ChosenRows;
        Close();

        return _work.RunAsync(
            _text[nameof(Strings.Randomiser_Heading)],
            async ct =>
            {
                var result = await _randomiser.ApplyAsync(mods, rows, ct).ConfigureAwait(true);

                await _rescan(ct).ConfigureAwait(true);
                _notices.Report(mods, result, _text[nameof(Strings.Randomiser_Heading)]);
            },
            CancellationToken.None);
    }

    private void Choose()
    {
        Clear();

        if (_game.Inventory is { } inventory && _game.Data is { } data)
        {
            var plan = _randomiser.Plan(inventory, data, _random, _onlyFolders);
            _modsDirectory = plan.ModsDirectory;

            foreach (var row in plan.Rows)
            {
                var item = new RandomiserRowViewModel(row, _text, ChooseAgain);
                item.PropertyChanged += OnRowChanged;
                Rows.Add(item);
            }
        }

        RaiseCounts();
    }

    private void ChooseAgain(RandomiserRowViewModel row)
    {
        row.Row = _randomiser.Reroll(row.Row, _random);
        RaiseCounts();
    }

    private void Clear()
    {
        foreach (var row in Rows)
        {
            row.PropertyChanged -= OnRowChanged;
        }

        Rows.Clear();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RandomiserRowViewModel.IsSelected))
        {
            RaiseCounts();
        }
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(ApplyText));
        OnPropertyChanged(nameof(CanApply));
        ApplyCommand.NotifyCanExecuteChanged();
    }
}
