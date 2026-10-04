using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Core;
using Xxsm.Core.GameBanana;
using Xxsm.Core.Io;
using Xxsm.Desktop.Services;
using Xxsm.Packs.GameBanana;

namespace Xxsm.Desktop.ViewModels;

/// <summary>The one GameBanana update check at a time, whichever page asked; a second request joins it.</summary>
/// <remarks>Owned by no page, so leaving one does not stop it. A forced check waits for an unforced one.</remarks>
public sealed partial class UpdateCheckRun(
    IModUpdateChecker checker,
    INotificationService notifications,
    ViewModelWorkRunner work,
    ITextCatalogue text,
    GameContext game,
    Func<CancellationToken, Task> rescan,
    Action<string?> showMods) : ObservableObject
{
    private readonly IModUpdateChecker _checker = checker;
    private readonly INotificationService _notifications = notifications;
    private readonly ViewModelWorkRunner _work = work;
    private readonly ITextCatalogue _text = text;
    private readonly Func<CancellationToken, Task> _rescan = rescan;
    private readonly GameContext _game = game;
    private readonly Action<string?> _showMods = showMods;
    private readonly Lock _gate = new();

    private Task<ModUpdateReport?>? _current;
    private string? _currentFolder;
    private bool _currentForced;
    private int _running;

    /// <summary>Whether a check is running, for greying every button that would start one.</summary>
    public bool IsRunning => Volatile.Read(ref _running) > 0;

    /// <summary>Checks a Mods folder, or joins the check already running on it. Never cancelled by a page.</summary>
    /// <param name="modsDirectory">The Mods folder.</param>
    /// <param name="force">Ask about every linked mod, however recently asked (<em>Check now</em>).</param>
    /// <returns>What it found, or null when it could not check; the notice says why.</returns>
    public Task<ModUpdateReport?> RunAsync(string modsDirectory, bool force)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);

        Task<ModUpdateReport?> run;

        lock (_gate)
        {
            if (_current is { IsCompleted: false } running &&
                _currentFolder is not null && PathComparer.AreEqual(_currentFolder, modsDirectory) &&
                (_currentForced || !force))
            {
                return running;
            }

            Interlocked.Increment(ref _running);

            // The game is the one whose folder this is, read now: the user may switch before it ends.
            run = RunAfterAsync(_current, modsDirectory, force, _game.GameId, _game.DisplayName ?? _game.GameId ?? string.Empty);
            _current = run;
            _currentFolder = modsDirectory;
            _currentForced = force;
        }

        OnPropertyChanged(nameof(IsRunning));
        return run;
    }

    private async Task<ModUpdateReport?> RunAfterAsync(
        Task<ModUpdateReport?>? before, string modsDirectory, bool force, string? gameId, string gameName)
    {
        try
        {
            if (before is not null)
            {
                await before.ConfigureAwait(true);
            }

            ModUpdateReport? report = null;

            await _work.RunAsync(
                _text[nameof(Strings.Mods_Updates_Heading)],
                async ct =>
                {
                    try
                    {
                        report = await _checker.CheckAsync(modsDirectory, force, cancellationToken: ct).ConfigureAwait(true);
                    }
                    catch (Exception ex) when (ex is GameBananaException or ModOperationException)
                    {
                        // An outage never blocks mod management: a warning, and nothing more.
                        _notifications.Add(
                            NotificationSeverity.Warning, _text[nameof(Strings.Mods_Updates_Heading)], ex.Message);
                        return;
                    }

                    _notifications.ReportUpdates(_text, report, gameName, new AsyncRelayCommand(() =>
                    {
                        _showMods(gameId);
                        return Task.CompletedTask;
                    }));

                    // The marks are in each mod's metadata, which the watcher ignores, so rescan.
                    if (report.Checked.Count > 0)
                    {
                        await _rescan(ct).ConfigureAwait(true);
                    }
                },
                CancellationToken.None).ConfigureAwait(true);

            return report;
        }
        finally
        {
            Interlocked.Decrement(ref _running);
            OnPropertyChanged(nameof(IsRunning));
        }
    }
}
