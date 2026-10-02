using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Desktop.Services;

namespace Xxsm.Desktop.ViewModels;

/// <summary>Export: a copy of the mods in a folder of the user's choosing; Stop removes a half-made copy.</summary>
public sealed partial class ModExportViewModel(
    GameContext game,
    IModExporter exporter,
    IStoragePicker picker,
    IFolderLauncher folders,
    INotificationService notifications,
    IUiDispatcher dispatcher,
    ViewModelWorkRunner work,
    ITextCatalogue text) : ObservableObject
{
    private readonly GameContext _game = game;
    private readonly IModExporter _exporter = exporter;
    private readonly IStoragePicker _picker = picker;
    private readonly IFolderLauncher _folders = folders;
    private readonly INotificationService _notifications = notifications;
    private readonly IUiDispatcher _dispatcher = dispatcher;
    private readonly ViewModelWorkRunner _work = work;
    private readonly ITextCatalogue _text = text;

    private ModExportPlan? _plan;
    private CancellationTokenSource? _running;
    private int _planVersion;

    /// <summary>Whether the panel is on screen.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>Copy only the mods that are switched on.</summary>
    [ObservableProperty]
    private bool _enabledOnly;

    /// <summary>Leave out each mod's <c>.xxsm</c> folder.</summary>
    [ObservableProperty]
    private bool _skipMetadata;

    /// <summary>Every mod straight into the export folder, without its character folder.</summary>
    [ObservableProperty]
    private bool _oneFolder;

    /// <summary>Whether the copies are switched on or off. Only the copies change.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SwitchAsTheyAre), nameof(SwitchAllOn), nameof(SwitchAllOff))]
    private ModExportSwitching _switching;

    /// <summary>The first of the panel's three on/off choices: each copy as its mod is now.</summary>
    public bool SwitchAsTheyAre
    {
        get => Switching == ModExportSwitching.AsTheyAre;
        set => Choose(value, ModExportSwitching.AsTheyAre);
    }

    /// <summary>The second: every copy switched on.</summary>
    public bool SwitchAllOn
    {
        get => Switching == ModExportSwitching.AllOn;
        set => Choose(value, ModExportSwitching.AllOn);
    }

    /// <summary>The third: every copy switched off.</summary>
    public bool SwitchAllOff
    {
        get => Switching == ModExportSwitching.AllOff;
        set => Choose(value, ModExportSwitching.AllOff);
    }

    /// <summary>The folder the copy is made in, or <c>null</c> until one is chosen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DestinationText), nameof(CanExport))]
    private string? _destination;

    /// <summary>How many mods and how much, once worked out.</summary>
    [ObservableProperty]
    private string? _summary;

    /// <summary>Whether a copy is being made now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoosing), nameof(CanExport))]
    private bool _isRunning;

    /// <summary>How far it has got, from 0 to 100.</summary>
    [ObservableProperty]
    private double _percent;

    /// <summary>Which mod it is copying, and how many are done.</summary>
    [ObservableProperty]
    private string? _progressText;

    /// <summary>Whether the options are showing rather than the progress.</summary>
    public bool IsChoosing => !IsRunning;

    /// <summary>Where the copy will go, or a word saying no folder is chosen yet.</summary>
    public string DestinationText => Destination ?? _text[nameof(Strings.Export_Destination_None)];

    /// <summary>Whether there is a folder and something to copy.</summary>
    public bool CanExport => !IsRunning && Destination is not null && _plan is { Mods.Count: > 0 };

    /// <summary>Opens the panel with the options as they were left and a fresh count.</summary>
    public Task OpenAsync()
    {
        IsOpen = true;
        return PlanAsync();
    }

    /// <summary>Closes the panel. Not while a copy is running; Stop is the way out of that.</summary>
    [RelayCommand]
    public void Close()
    {
        if (!IsRunning)
        {
            IsOpen = false;
        }
    }

    /// <summary>Asks where to put the copy.</summary>
    [RelayCommand]
    private async Task ChooseDestinationAsync()
    {
        var chosen = await _picker
            .PickFolderAsync(_text[nameof(Strings.Export_PickerTitle)], Destination, CancellationToken.None)
            .ConfigureAwait(true);

        if (chosen is not null)
        {
            Destination = chosen;
        }
    }

    /// <summary>Makes the copy.</summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        if (!CanExport || _plan is not { } plan || Destination is not { } destination)
        {
            return;
        }

        using var running = new CancellationTokenSource();
        _running = running;
        IsRunning = true;
        var startedIn = (_game.GameId, _game.ModsDirectory);
        Percent = 0;
        ProgressText = null;

        try
        {
            ModExportResult? result = null;
            var progress = new DispatchedProgress(this);

            var ran = await _work.RunAsync(
                _text[nameof(Strings.Export_Heading)],
                async ct => result = await _exporter.ExportAsync(plan, destination, progress, ct).ConfigureAwait(true),
                running.Token).ConfigureAwait(true);

            if (ran && result is not null)
            {
                IsOpen = false;
                Report(result);
            }
            else if (running.IsCancellationRequested)
            {
                _notifications.Add(
                    NotificationSeverity.Information,
                    _text[nameof(Strings.Export_Heading)],
                    _text[nameof(Strings.Export_Stopped)]);
            }
        }
        finally
        {
            _running = null;
            IsRunning = false;

            // The game was switched while it copied: once the copy is over, the panel goes too.
            if (!string.Equals(startedIn.GameId, _game.GameId, StringComparison.OrdinalIgnoreCase)
                || !PathComparer.AreEqual(startedIn.ModsDirectory ?? string.Empty, _game.ModsDirectory ?? string.Empty))
            {
                IsOpen = false;
            }
        }
    }

    /// <summary>Stops a running copy; what was copied is removed.</summary>
    [RelayCommand]
    private void Stop() => _running?.Cancel();

    /// <summary>The count started by the last change of an option, for a test to wait on.</summary>
    internal Task Counting { get; private set; } = Task.CompletedTask;

    partial void OnEnabledOnlyChanged(bool value) => Counting = PlanAsync();

    partial void OnSkipMetadataChanged(bool value) => Counting = PlanAsync();

    partial void OnOneFolderChanged(bool value) => Counting = PlanAsync();

    partial void OnSwitchingChanged(ModExportSwitching value) => Counting = PlanAsync();

    /// <summary>A radio button turned on chooses its switching; one turned off by its neighbour does nothing.</summary>
    private void Choose(bool chosen, ModExportSwitching switching)
    {
        if (chosen)
        {
            Switching = switching;
        }
    }

    private async Task PlanAsync()
    {
        if (_game.ModsDirectory is not { Length: > 0 } mods)
        {
            return;
        }

        // Ticking twice quickly starts two counts; only the last one's answer is shown.
        var version = ++_planVersion;
        var options = new ModExportOptions(EnabledOnly, SkipMetadata, OneFolder, Switching);
        _plan = null;
        Summary = null;
        OnPropertyChanged(nameof(CanExport));

        ModExportPlan? plan = null;

        await _work.RunAsync(
            _text[nameof(Strings.Export_Heading)],
            async ct => plan = await _exporter.PlanAsync(mods, options, ct).ConfigureAwait(true),
            CancellationToken.None).ConfigureAwait(true);

        if (version != _planVersion || plan is null)
        {
            return;
        }

        _plan = plan;
        Summary = _text.Format(nameof(Strings.Export_Summary), _text.Mods(plan.Mods.Count), Xxsm.Core.Text.ByteSize.Describe(plan.TotalBytes)) +
                  (plan.SkippedDisabledCount > 0
                      ? " " + _text.Format(nameof(Strings.Export_Skipped), _text.Mods(plan.SkippedDisabledCount))
                      : string.Empty) +
                  (plan.RenamedCount > 0
                      ? " " + _text.Format(nameof(Strings.Export_Renamed), _text.Mods(plan.RenamedCount))
                      : string.Empty);
        OnPropertyChanged(nameof(CanExport));
    }

    private void Report(ModExportResult result)
    {
        var folder = result.Folder;

        _notifications.Add(
            NotificationSeverity.Information,
            _text[nameof(Strings.Export_Heading)],
            _text.Format(nameof(Strings.Export_Done), _text.Mods(result.ModCount), Xxsm.Core.Text.ByteSize.Describe(result.Bytes), PathDisplay.Show(folder)),
            action: new AsyncRelayCommand(() => _folders.OpenAsync(folder)),
            actionText: _text[nameof(Strings.Export_OpenFolder)]);
    }

    private void Show(ModExportProgress progress)
    {
        Percent = progress.TotalBytes > 0 ? 100.0 * progress.BytesDone / progress.TotalBytes : 0;
        ProgressText = progress.CurrentMod is { } current
            ? _text.Format(
                nameof(Strings.Export_Progress),
                current,
                (progress.ModsDone + 1).ToString(System.Globalization.CultureInfo.CurrentCulture),
                _text.Mods(progress.ModCount))
            : null;
    }

    /// <summary>Hands each report to the window's thread.</summary>
    private sealed class DispatchedProgress(ModExportViewModel owner) : IProgress<ModExportProgress>
    {
        public void Report(ModExportProgress value) => owner._dispatcher.Post(() => owner.Show(value));
    }
}
