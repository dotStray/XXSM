using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Desktop.Services;
using Xxsm.Packs.Overlays;

namespace Xxsm.Desktop.ViewModels;

/// <summary>One correction an installed pack now makes itself.</summary>
public sealed partial class CaughtUpCorrectionViewModel(RedundantCorrection correction) : ObservableObject
{
    /// <summary>The character.</summary>
    public string InternalName { get; } = correction.InternalName;

    /// <summary>Its name, as it shows now.</summary>
    public string DisplayName { get; } = correction.DisplayName;

    /// <summary>What the correction set, as the pack calls each field.</summary>
    public string FieldsText { get; } = string.Join(", ", correction.Fields);

    /// <summary>Whether to clear it. Every row starts ticked: the pack already says the same.</summary>
    [ObservableProperty]
    private bool _isSelected = true;
}

/// <summary>After an install, offers to clear corrections the pack now makes itself; checked again first.</summary>
public sealed partial class CaughtUpCorrectionsViewModel(
    IOverlayRedundancy redundancy,
    INotificationService notifications,
    ViewModelWorkRunner runner,
    ITextCatalogue text,
    Func<CancellationToken, Task> reload) : ObservableObject
{
    private readonly IOverlayRedundancy _redundancy = redundancy;
    private readonly INotificationService _notifications = notifications;
    private readonly ViewModelWorkRunner _runner = runner;
    private readonly ITextCatalogue _text = text;
    private readonly Func<CancellationToken, Task> _reload = reload;

    private string? _packDirectory;

    /// <summary>Whether the offer is on screen.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>The game the corrections are for.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private string? _gameName;

    /// <summary>The pack version compared with.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private string? _packVersion;

    /// <summary>The corrections the pack now makes.</summary>
    public ObservableCollection<CaughtUpCorrectionViewModel> Corrections { get; } = [];

    /// <summary>"My Game 26.10.1 now does what your corrections to 3 characters did."</summary>
    public string SummaryText => _text.Format(
        nameof(Strings.CaughtUp_Summary),
        GameName ?? string.Empty,
        Xxsm.Packs.Registry.PackVersionText.Display(PackVersion),
        _text.Characters(Corrections.Count));

    /// <summary>How many are ticked.</summary>
    public int SelectedCount => Corrections.Count(c => c.IsSelected);

    /// <summary>Whether anything is ticked.</summary>
    public bool CanClear => SelectedCount > 0;

    /// <summary>"Clear 3 corrections", or that nothing is ticked.</summary>
    public string ClearText => SelectedCount == 0
        ? _text[nameof(Strings.CaughtUp_ClearNone)]
        : _text.Format(nameof(Strings.CaughtUp_Clear), _text.Characters(SelectedCount));

    /// <summary>Compares the user's corrections with a pack just installed, showing the offer if any match.</summary>
    /// <param name="gameId">The game.</param>
    /// <param name="gameName">Its name, for the panel.</param>
    /// <param name="packDirectory">The pack just installed.</param>
    /// <param name="cancellationToken">Cancels the comparison.</param>
    /// <returns>Whether there is something to offer.</returns>
    public async Task<bool> CheckAsync(string gameId, string gameName, string packDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packDirectory);

        var report = await _redundancy.FindAsync(gameId, packDirectory, cancellationToken).ConfigureAwait(true);

        Clear();

        if (report.Redundant.Count == 0)
        {
            return false;
        }

        foreach (var correction in report.Redundant)
        {
            var row = new CaughtUpCorrectionViewModel(correction);
            row.PropertyChanged += OnRowChanged;
            Corrections.Add(row);
        }

        _packDirectory = packDirectory;
        GameId = gameId;
        GameName = gameName;
        PackVersion = report.PackVersion;
        Raise();
        return true;
    }

    /// <summary>The game the offer is about.</summary>
    public string? GameId { get; private set; }

    /// <summary>Puts the offer on screen, once whatever was showing first has gone.</summary>
    public void Show()
    {
        if (Corrections.Count > 0)
        {
            IsOpen = true;
        }
    }

    /// <summary>Clears the ticked corrections and says what happened.</summary>
    [RelayCommand]
    public Task ClearSelectedAsync()
    {
        if (GameId is not { } gameId || _packDirectory is not { } directory || SelectedCount == 0)
        {
            return Task.CompletedTask;
        }

        var names = Corrections.Where(c => c.IsSelected).Select(c => c.InternalName).ToList();
        var title = GameName ?? gameId;

        return _runner.RunAsync(
            _text[nameof(Strings.CaughtUp_Heading)],
            async ct =>
            {
                var result = await _redundancy.ClearAsync(gameId, directory, names, ct).ConfigureAwait(true);

                Dismiss();

                _notifications.Add(
                    NotificationSeverity.Information,
                    title,
                    result.Kept.Count == 0
                        ? _text.Format(nameof(Strings.CaughtUp_Cleared), _text.Characters(result.Cleared.Count))
                        : _text.Format(
                            nameof(Strings.CaughtUp_ClearedSomeKept),
                            _text.Characters(result.Cleared.Count),
                            _text.Characters(result.Kept.Count)));

                await _reload(ct).ConfigureAwait(true);
            },
            CancellationToken.None);
    }

    /// <summary>Closes the offer, keeping every correction.</summary>
    [RelayCommand]
    public void Dismiss()
    {
        IsOpen = false;
        Clear();
        Raise();
    }

    private void Clear()
    {
        foreach (var row in Corrections)
        {
            row.PropertyChanged -= OnRowChanged;
        }

        Corrections.Clear();
        _packDirectory = null;
        GameId = null;
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Raise();

    private void Raise()
    {
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(CanClear));
        OnPropertyChanged(nameof(ClearText));
    }
}
