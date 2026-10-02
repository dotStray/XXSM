using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using Xxsm.Core.Io;
using Xxsm.Desktop.Services;
using Xxsm.Packs.Downloads;

namespace Xxsm.Desktop.ViewModels;

/// <summary>What has been downloaded and is downloading: one list for the app, updated on the UI thread.</summary>
public sealed partial class DownloadsViewModel : ObservableObject, IDisposable
{
    private readonly IDownloadManager _downloads;
    private readonly ModInstallViewModel _install;
    private readonly INotificationService _notifications;
    private readonly ViewModelWorkRunner _work;
    private readonly IUrlLauncher _urls;
    private readonly IUiDispatcher _ui;
    private readonly ITextCatalogue _text;
    private readonly GameContext _game;
    private readonly ILogger _logger;

    private bool _disposed;

    /// <summary>Creates the list.</summary>
    /// <param name="downloads">Runs the downloads and keeps the list.</param>
    /// <param name="install">The panel a finished download is installed from.</param>
    /// <param name="notifications">Where a finished download announces itself.</param>
    /// <param name="work">Turns a failure into a notice rather than a crash.</param>
    /// <param name="urls">Opens a mod's page.</param>
    /// <param name="ui">Marshals the manager's events onto the thread that owns the window.</param>
    /// <param name="text">The interface's wording.</param>
    /// <param name="game">The selected game, for deciding which entries can be installed here.</param>
    /// <param name="logger">Structured log sink.</param>
    public DownloadsViewModel(
        IDownloadManager downloads,
        ModInstallViewModel install,
        INotificationService notifications,
        ViewModelWorkRunner work,
        IUrlLauncher urls,
        IUiDispatcher ui,
        ITextCatalogue text,
        GameContext game,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(urls);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(logger);

        _downloads = downloads;
        _install = install;
        _notifications = notifications;
        _work = work;
        _urls = urls;
        _ui = ui;
        _text = text;
        Rows.CollectionChanged += OnRowsChanged;
        _game = game;
        _logger = logger.ForContext<DownloadsViewModel>();

        _downloads.Changed += OnChanged;
        _downloads.Progress += OnProgress;
        _install.PropertyChanged += OnInstallChanged;
    }

    /// <summary>Raised when a row asks to be shown where the install panel can be drawn; the shell navigates.</summary>
    public event EventHandler<DownloadRowViewModel>? OpenRequested;

    /// <summary>How many downloads the Mods page shows before <em>Show all</em>.</summary>
    public const int ShownAtFirst = 3;

    /// <summary>Every download, newest first.</summary>
    public ObservableCollection<DownloadRowViewModel> Rows { get; } = [];

    /// <summary>What the Mods page draws: the newest few, or every one after <em>Show all</em>.</summary>
    public ObservableCollection<DownloadRowViewModel> ShownRows { get; } = [];

    /// <summary>Whether the whole list is shown rather than the newest few.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAllText))]
    private bool _showAll;

    /// <summary>Whether there are more downloads than are shown at first, so the toggle is needed.</summary>
    public bool HasMore => Rows.Count > ShownAtFirst;

    /// <summary>"Show all 12 downloads", or "Show fewer" once they are.</summary>
    public string ShowAllText => ShowAll
        ? _text[nameof(Strings.Downloads_ShowFewer)]
        : _text.Format(nameof(Strings.Downloads_ShowAll), _text.Downloads(Rows.Count));

    /// <summary>Shows every download, or only the newest few again.</summary>
    [RelayCommand]
    private void ToggleShowAll() => ShowAll = !ShowAll;

    partial void OnShowAllChanged(bool value) => RefreshShown();

    private void OnRowsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        RefreshShown();

    private void RefreshShown()
    {
        var wanted = ShowAll ? [.. Rows] : Rows.Take(ShownAtFirst).ToList();

        if (!ShownRows.SequenceEqual(wanted))
        {
            ShownRows.Clear();

            foreach (var row in wanted)
            {
                ShownRows.Add(row);
            }
        }

        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(ShowAllText));
    }

    /// <summary>The one being downloaded now, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActive))]
    private DownloadRowViewModel? _active;

    /// <summary>Whether anything at all is downloading, whoever is showing it.</summary>
    public bool IsDownloading => Active is not null;

    /// <summary>Whether the top strip has something to show: not while the install panel shows that download.</summary>
    public bool HasActive =>
        Active is { } running && !(_install.IsOpen && _install.AttachedDownloadId == running.Id);

    /// <summary>Whether there is anything at all to show.</summary>
    public bool HasRows => Rows.Count > 0;

    /// <summary>Whether anything on the list is finished and could be cleared.</summary>
    public bool HasFinished => Rows.Any(row => !row.IsRunning);

    /// <summary>Reads the list from disk and prunes what has expired.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _work.RunAsync(
            _text[nameof(Strings.Downloads_Heading)],
            async ct =>
            {
                var jobs = await _downloads.LoadAsync(ct).ConfigureAwait(true);

                Rows.Clear();

                foreach (var job in jobs)
                {
                    Rows.Add(new DownloadRowViewModel(job, _text) { IsForThisGame = IsForThisGame(job) });
                }

                RaiseCounts();
            },
            cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Brings a running download's panel back.</summary>
    [RelayCommand]
    public void Show(DownloadRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        _install.Attach(row.Job);
    }

    /// <summary>Brings the running download's panel back.</summary>
    [RelayCommand]
    public void ShowActive() => Show(Active);

    /// <summary>Stops a running download.</summary>
    [RelayCommand]
    public void Cancel(DownloadRowViewModel? row)
    {
        if (row is not null)
        {
            _downloads.Cancel(row.Id);
        }
    }

    /// <summary>Stops the running download.</summary>
    [RelayCommand]
    public void CancelActive() => Cancel(Active);

    /// <summary>Opens the install screen for a download that has finished.</summary>
    /// <returns>A task that completes when the card is on screen, or the reason is.</returns>
    [RelayCommand]
    public Task InstallAsync(DownloadRowViewModel? row) => row is null
        ? Task.CompletedTask
        : _work.RunAsync(
            _text[nameof(Strings.Downloads_Heading)],
            ct => _install.OpenFromDownloadAsync(row.Id, ct),
            CancellationToken.None);

    /// <summary>Asks the shell to show a page that can draw the install panel, then installs.</summary>
    /// <returns>A task that completes when the card is on screen, or the reason is.</returns>
    [RelayCommand]
    public Task OpenAsync(DownloadRowViewModel? row)
    {
        if (row is null)
        {
            return Task.CompletedTask;
        }

        OpenRequested?.Invoke(this, row);

        return InstallAsync(row);
    }

    /// <summary>Fetches a cancelled, failed or installed download again.</summary>
    /// <returns>A task that completes when the new download has started, or the reason is.</returns>
    [RelayCommand]
    public Task RetryAsync(DownloadRowViewModel? row) => row is null
        ? Task.CompletedTask
        : _work.RunAsync(
            _text[nameof(Strings.Downloads_Heading)],
            async ct =>
            {
                if (await _downloads.RetryAsync(row.Id, ct).ConfigureAwait(true) is { } job)
                {
                    _install.Attach(job);
                }
            },
            CancellationToken.None);

    /// <summary>Opens a download's mod page in the browser.</summary>
    [RelayCommand]
    public Task OpenPageAsync(DownloadRowViewModel? row) => row?.PageUrl is { Length: > 0 } address
        ? _urls.OpenAsync(address, CancellationToken.None)
        : Task.CompletedTask;

    /// <summary>Takes a download off the list and removes its archive.</summary>
    [RelayCommand]
    public Task RemoveAsync(DownloadRowViewModel? row) => row is null
        ? Task.CompletedTask
        : _work.RunAsync(
            _text[nameof(Strings.Downloads_Heading)],
            async ct =>
            {
                var name = row.DisplayName;

                await _downloads.ForgetAsync(row.Id, ct).ConfigureAwait(true);

                _notifications.Add(
                    NotificationSeverity.Information,
                    _text[nameof(Strings.Downloads_Heading)],
                    _text.Format(nameof(Strings.Downloads_Removed), name));
            },
            CancellationToken.None);

    /// <summary>Empties the list of everything that is not still downloading.</summary>
    [RelayCommand]
    public Task ClearFinishedAsync() => _work.RunAsync(
        _text[nameof(Strings.Downloads_Heading)],
        async ct =>
        {
            foreach (var row in Rows.Where(entry => !entry.IsRunning).ToList())
            {
                await _downloads.ForgetAsync(row.Id, ct).ConfigureAwait(true);
            }
        },
        CancellationToken.None);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _downloads.Changed -= OnChanged;
        _downloads.Progress -= OnProgress;
        _install.PropertyChanged -= OnInstallChanged;
    }

    private void OnInstallChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ModInstallViewModel.IsOpen)
            or nameof(ModInstallViewModel.AttachedDownloadId))
        {
            OnPropertyChanged(nameof(HasActive));
        }
    }

    private void OnProgress(object? sender, DownloadChange change) =>
        _ui.Post(() =>
        {
            if (Rows.FirstOrDefault(row => row.Id == change.Job.Id) is { } row)
            {
                row.RaiseProgress();
            }
        });

    private void OnChanged(object? sender, DownloadChange change) =>
        _ui.Post(() => Apply(change.Job));

    private void Apply(DownloadJob job)
    {
        var known = _downloads.Jobs.Any(entry => ReferenceEquals(entry, job));
        var row = Rows.FirstOrDefault(entry => entry.Id == job.Id);

        if (!known)
        {
            if (row is not null)
            {
                Rows.Remove(row);
            }

            RaiseCounts();

            return;
        }

        if (row is null)
        {
            row = new DownloadRowViewModel(job, _text);
            Rows.Insert(0, row);
        }
        else
        {
            row.RaiseAll();
        }

        row.IsForThisGame = IsForThisGame(job);

        // Finished while its panel was elsewhere: say so, since the strip has just gone.
        if (job.State == DownloadState.Ready && _install.AttachedDownloadId != job.Id)
        {
            _notifications.Add(
                NotificationSeverity.Information,
                _text[nameof(Strings.Downloads_Heading)],
                _text.Format(nameof(Strings.Downloads_Ready_Notice), row.DisplayName),
                action: new AsyncRelayCommand(() => OpenAsync(row)),
                actionText: _text[nameof(Strings.Downloads_Ready_Action)]);

            _logger.Information(
                "GameBanana download {ModId} finished while the install panel was elsewhere",
                job.Record.ModId);
        }

        RaiseCounts();
    }

    /// <summary>Whether an entry can be installed into the open Mods folder; one with no game recorded can.</summary>
    private bool IsForThisGame(DownloadJob job) =>
        job.Record.GameId is not { Length: > 0 } game
        || string.Equals(game, _game.GameId, StringComparison.OrdinalIgnoreCase);

    private void RaiseCounts()
    {
        Active = Rows.FirstOrDefault(row => row.IsRunning);

        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(HasFinished));
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(HasActive));
    }
}
