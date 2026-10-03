using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Core;
using Xxsm.Core.Ini;
using Xxsm.Core.Io;
using Xxsm.Desktop.Services;

namespace Xxsm.Desktop.ViewModels;

/// <summary>"INIs changed in XXSM": every mod whose INIs differ from their authors', and putting the authors' back.</summary>
public sealed partial class ChangedInisViewModel(
    GameContext game,
    IIniOriginalsService originals,
    INotificationService notifications,
    ViewModelWorkRunner work,
    ITextCatalogue text) : ObservableObject
{
    private readonly GameContext _game = game;
    private readonly IIniOriginalsService _originals = originals;
    private readonly INotificationService _notifications = notifications;
    private readonly ViewModelWorkRunner _work = work;
    private readonly ITextCatalogue _text = text;

    /// <summary>Whether the panel is on screen.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>How many mods have an INI changed in XXSM, as last counted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAny), nameof(CountText))]
    private int _count;

    /// <summary>Whether the authors' INIs are being put back now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRevert))]
    private bool _isRunning;

    /// <summary>Each mod with an INI changed in XXSM, ticked to be put back.</summary>
    public ObservableCollection<ChangedIniRowViewModel> Rows { get; } = [];

    /// <summary>Whether any mod has an INI changed in XXSM.</summary>
    public bool HasAny => Count > 0;

    /// <summary>The card's line: how many mods have changed INIs, or that none has.</summary>
    public string CountText => Count == 0
        ? _text[nameof(Strings.ChangedInis_None)]
        : _text.ForCount(Count, nameof(Strings.ChangedInis_Count_One), nameof(Strings.ChangedInis_Count), _text.Mods(Count));

    /// <summary>Whether a mod is ticked and nothing is running.</summary>
    public bool CanRevert => !IsRunning && Rows.Any(row => row.IsChecked);

    /// <summary>Counts the mods with changed INIs again, for the card.</summary>
    public async Task RefreshCountAsync(CancellationToken cancellationToken)
    {
        if (_game.ModsDirectory is not { Length: > 0 } mods)
        {
            Count = 0;
            return;
        }

        var found = await _originals.FindAsync(mods, cancellationToken).ConfigureAwait(true);
        Count = found.Count;
    }

    /// <summary>Opens the panel on every mod with a changed INI, all ticked.</summary>
    public async Task OpenAsync()
    {
        if (_game.ModsDirectory is not { Length: > 0 } mods)
        {
            return;
        }

        IsOpen = true;
        Clear();

        IReadOnlyList<ChangedMod> found = [];
        await _work.RunAsync(
            _text[nameof(Strings.ChangedInis_Heading)],
            async ct => found = await _originals.FindAsync(mods, ct).ConfigureAwait(true),
            CancellationToken.None).ConfigureAwait(true);

        foreach (var changed in found)
        {
            var row = new ChangedIniRowViewModel(changed, mods, _text);
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        }

        Count = found.Count;
        OnPropertyChanged(nameof(CanRevert));
    }

    /// <summary>Closes the panel without changing anything.</summary>
    [RelayCommand]
    public void Close()
    {
        if (IsRunning)
        {
            return;
        }

        IsOpen = false;
        Clear();
    }

    /// <summary>Puts back the ticked mods' INIs as their authors shipped them, then says so with an Undo.</summary>
    [RelayCommand]
    private async Task RevertAsync()
    {
        if (!CanRevert)
        {
            return;
        }

        var ticked = Rows.Where(row => row.IsChecked).ToList();
        var done = new List<IniRewrite>();
        IsRunning = true;

        try
        {
            await _work.RunAsync(
                _text[nameof(Strings.ChangedInis_Heading)],
                async ct =>
                {
                    foreach (var row in ticked)
                    {
                        var rewrite = await _originals.RevertAsync(row.ModFolder, IniRevertScope.Everything, ct).ConfigureAwait(true);

                        if (!rewrite.IsEmpty)
                        {
                            done.Add(rewrite);
                        }
                    }
                },
                CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            IsRunning = false;
        }

        IsOpen = false;
        Clear();
        await RefreshCountAsync(CancellationToken.None).ConfigureAwait(true);

        if (done.Count > 0)
        {
            Announce(done);
        }
    }

    private void Announce(List<IniRewrite> done)
    {
        Notification? notice = null;
        var undo = new AsyncRelayCommand(async () =>
        {
            var undone = await _work.RunAsync(
                _text[nameof(Strings.ChangedInis_Heading)],
                async ct =>
                {
                    var failed = new List<string>();

                    foreach (var rewrite in done)
                    {
                        try
                        {
                            await _originals.UndoAsync(rewrite, ct).ConfigureAwait(true);
                        }
                        catch (ModOperationException ex)
                        {
                            failed.Add($"{PathDisplay.Show(rewrite.ModFolder)}: {ex.Message}");
                        }
                    }

                    if (failed.Count > 0)
                    {
                        throw new ModOperationException(string.Join(Environment.NewLine, failed));
                    }
                },
                CancellationToken.None).ConfigureAwait(true);

            await RefreshCountAsync(CancellationToken.None).ConfigureAwait(true);

            if (undone && notice is not null)
            {
                _notifications.Dismiss(notice);
                _notifications.Add(
                    NotificationSeverity.Information,
                    _text[nameof(Strings.ChangedInis_Heading)],
                    _text.Format(nameof(Strings.ChangedInis_Undone), _text.Mods(done.Count)));
            }
        });

        notice = _notifications.Add(
            NotificationSeverity.Information,
            _text[nameof(Strings.ChangedInis_Heading)],
            _text.Format(nameof(Strings.ChangedInis_Done), _text.Mods(done.Count)),
            action: undo,
            actionText: _text[nameof(Strings.Notifications_Undo)]);
    }

    private void Clear()
    {
        foreach (var row in Rows)
        {
            row.PropertyChanged -= OnRowChanged;
        }

        Rows.Clear();
        OnPropertyChanged(nameof(CanRevert));
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChangedIniRowViewModel.IsChecked))
        {
            OnPropertyChanged(nameof(CanRevert));
        }
    }
}

/// <summary>One mod in "INIs changed in XXSM": what differs from its author's INI, and whether to put it back.</summary>
public sealed partial class ChangedIniRowViewModel : ObservableObject
{
    /// <summary>Creates the row, ticked.</summary>
    public ChangedIniRowViewModel(ChangedMod changed, string modsDirectory, ITextCatalogue text)
    {
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(text);

        ModFolder = changed.Mod.Path;
        Name = changed.Mod.DisplayName;
        Place = PathDisplay.Show(PathComparer.TryGetRelativePath(modsDirectory, changed.Mod.Path) ?? changed.Mod.Path);
        Lines =
        [
            .. changed.Changes.Files.SelectMany(file => file.Changes.Select(change => text.Format(
                nameof(Strings.ChangedInis_Line), file.File, change.Section, change.Name, change.Current, change.Original))),
            .. changed.Changes.Files.Where(file => file.HasOtherChanges)
                .Select(file => text.Format(nameof(Strings.ChangedInis_OtherChanges), file.File)),
        ];
    }

    /// <summary>The mod folder.</summary>
    public string ModFolder { get; }

    /// <summary>The mod's name.</summary>
    public string Name { get; }

    /// <summary>Where it is in the Mods folder.</summary>
    public string Place { get; }

    /// <summary>Each line that differs, beside the author's value, then any INI with other differences.</summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>Whether to put this mod's INIs back.</summary>
    [ObservableProperty]
    private bool _isChecked = true;
}
