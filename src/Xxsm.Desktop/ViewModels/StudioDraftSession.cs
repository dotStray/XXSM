using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;
using Xxsm.Core;
using Xxsm.Desktop.Services;
using Xxsm.Packs.Studio;

namespace Xxsm.Desktop.ViewModels;

/// <summary>Where a Studio draft's latest change is, relative to the disk.</summary>
public enum StudioSaveState
{
    /// <summary>Everything on screen is on disk.</summary>
    Saved,

    /// <summary>A change is waiting for the typing to stop.</summary>
    Pending,

    /// <summary>A write is in progress.</summary>
    Saving,

    /// <summary>The last write failed. The change is kept in memory and tried again.</summary>
    Failed,
}

/// <summary>One open Pack Studio draft: its latest state, Undo and Redo, saved a second after a change.</summary>
public sealed partial class StudioDraftSession : ObservableObject, IDisposable
{
    /// <summary>How long after the last change the draft is written.</summary>
    public static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);

    /// <summary>How many steps Undo keeps. Each is a whole draft, but drafts share almost everything.</summary>
    private const int UndoDepth = 200;

    private readonly IStudioDraftStore _store;
    private readonly ITextCatalogue _text;
    private readonly ILogger _logger;
    private readonly ITimer _timer;
    private readonly List<PackDraft> _undo = [];
    private readonly List<PackDraft> _redo = [];

    private PackDraft _current;
    private StudioSaveState _saveState;
    private string? _saveError;
    private PackDraft _saved;
    private Task _pendingSave = Task.CompletedTask;
    private bool _disposed;

    /// <summary>Opens a draft that has just been read from, or written to, the store.</summary>
    /// <param name="draft">The draft as it is on disk.</param>
    /// <param name="store">Writes it.</param>
    /// <param name="time">Times the debounce.</param>
    /// <param name="dispatcher">Brings the debounce back onto the UI thread.</param>
    /// <param name="text">The interface's wording, for the status line.</param>
    /// <param name="logger">Structured log sink.</param>
    public StudioDraftSession(
        PackDraft draft,
        IStudioDraftStore store,
        TimeProvider time,
        IUiDispatcher dispatcher,
        ITextCatalogue text,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _text = text;
        _logger = logger.ForContext<StudioDraftSession>();
        _current = draft;
        _saved = draft;

        _timer = time.CreateTimer(
            _ => dispatcher.Post(() => StartSave(CancellationToken.None)),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>Raised after every change, Undo and Redo, once <see cref="Current"/> is the new draft.</summary>
    public event EventHandler? DraftChanged;

    /// <summary>The draft as the user sees it now.</summary>
    public PackDraft Current
    {
        get => _current;
        private set => SetProperty(ref _current, value);
    }

    /// <summary>Where the latest change is.</summary>
    public StudioSaveState SaveState
    {
        get => _saveState;
        private set
        {
            if (SetProperty(ref _saveState, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(HasFailed));
            }
        }
    }

    /// <summary>Why the last write failed, in the OS's words, or null.</summary>
    public string? SaveError
    {
        get => _saveError;
        private set
        {
            if (SetProperty(ref _saveError, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    /// <summary>Whether the draft on screen differs from the one on disk.</summary>
    public bool HasUnsavedChanges => !ReferenceEquals(Current, _saved);

    /// <summary>The header's status line: <em>Saving…</em>, <em>All changes saved</em>, or why not.</summary>
    public string StatusText => SaveState switch
    {
        StudioSaveState.Saved => _text[nameof(Strings.Studio_Status_Saved)],
        StudioSaveState.Failed => _text.Format(nameof(Strings.Studio_Status_Failed), SaveError),
        _ => _text[nameof(Strings.Studio_Status_Saving)],
    };

    /// <summary>Whether the last write failed.</summary>
    public bool HasFailed => SaveState == StudioSaveState.Failed;

    /// <summary>Whether there is a change to step back from.</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>Whether there is an undone change to step forward to.</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Makes a change, and schedules it to be saved.</summary>
    /// <param name="changed">The new draft, normally from <see cref="DraftEdits"/>.</param>
    public void Apply(PackDraft changed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(changed);

        if (ReferenceEquals(changed, Current))
        {
            return;
        }

        _undo.Add(Current);

        if (_undo.Count > UndoDepth)
        {
            _undo.RemoveAt(0);
        }

        _redo.Clear();
        MoveTo(changed);
    }

    /// <summary>Steps back one change. Does nothing when there is none.</summary>
    public void Undo() => Step(_undo, _redo);

    /// <summary>Steps forward one undone change. Does nothing when there is none.</summary>
    public void Redo() => Step(_redo, _undo);

    /// <summary>Writes any change now rather than after the delay, as Studio is left or the app closes.</summary>
    /// <returns>A task that completes when the draft on screen is on disk, or the write has failed.</returns>
    public Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return _pendingSave;
        }

        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        return StartSave(cancellationToken);
    }

    /// <summary>Completes when the save in progress, if any, has finished.</summary>
    public Task WhenIdleAsync() => _pendingSave;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
    }

    private void Step(List<PackDraft> from, List<PackDraft> to)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (from.Count == 0)
        {
            return;
        }

        to.Add(Current);

        var target = from[^1];
        from.RemoveAt(from.Count - 1);

        MoveTo(target);
    }

    private void MoveTo(PackDraft draft)
    {
        Current = draft;

        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));

        if (ReferenceEquals(draft, _saved))
        {
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            if (SaveState == StudioSaveState.Pending)
            {
                SaveState = StudioSaveState.Saved;
            }
        }
        else
        {
            SaveError = null;
            SaveState = StudioSaveState.Pending;
            _timer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }

        DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    private Task StartSave(CancellationToken cancellationToken)
    {
        _pendingSave = SaveAfterAsync(_pendingSave, cancellationToken);
        return _pendingSave;
    }

    private async Task SaveAfterAsync(Task previous, CancellationToken cancellationToken)
    {
        try
        {
            await previous.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Waiting was cancelled, not the write; this save goes ahead.
        }

        try
        {
            while (!ReferenceEquals(Current, _saved))
            {
                var snapshot = Current;

                SaveState = StudioSaveState.Saving;

                await _store.WriteAsync(snapshot, cancellationToken).ConfigureAwait(true);

                _saved = snapshot;
            }

            SaveError = null;
            SaveState = StudioSaveState.Saved;
        }
        catch (ModOperationException ex)
        {
            _logger.Warning(ex, "Could not save Studio draft {GameId}", Current.GameId);

            SaveError = ex.Message;
            SaveState = StudioSaveState.Failed;
        }
        catch (OperationCanceledException)
        {
            SaveState = StudioSaveState.Pending;
            throw;
        }
    }
}
