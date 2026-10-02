using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;
using Xxsm.Core;
using Xxsm.Desktop.Services;

namespace Xxsm.Desktop.ViewModels;

/// <summary>A view model that knows when it is on screen, and cleans up when it is not.</summary>
/// <remarks>What <see cref="OnActivated"/> subscribes, <see cref="OnDeactivated"/> undoes; deactivating twice is
/// safe.</remarks>
public abstract partial class ViewModelBase : ObservableObject, IDisposable
{
    private readonly List<Task> _pending = [];
    private readonly Lock _gate = new();

    private CancellationTokenSource? _activation;
    private bool _disposed;

    /// <summary>Whether the view model is currently on screen.</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>Whether something long-running is in flight.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>What that something is, for the progress indicator.</summary>
    [ObservableProperty]
    private string? _busyMessage;

    /// <summary>Cancelled when the view model leaves the screen; every async operation it starts honours it.</summary>
    protected CancellationToken ActivationToken =>
        _activation?.Token ?? new CancellationToken(canceled: true);

    /// <summary>Puts the view model on screen.</summary>
    public void Activate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsActive)
        {
            return;
        }

        _activation = new CancellationTokenSource();
        IsActive = true;

        OnActivated();
    }

    /// <summary>Takes the view model off screen and undoes everything activation did.</summary>
    public void Deactivate()
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;

        // Cancelled before OnDeactivated, so an awaiting handler sees the cancellation first.
        _activation?.Cancel();

        try
        {
            OnDeactivated();
        }
        finally
        {
            _activation?.Dispose();
            _activation = null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases what the view model holds.</summary>
    /// <param name="disposing">True when called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed || !disposing)
        {
            return;
        }

        Deactivate();
        _disposed = true;
    }

    /// <summary>Completes when everything started so far has finished; later work is not waited for.</summary>
    public Task WhenIdleAsync()
    {
        Task[] outstanding;

        lock (_gate)
        {
            outstanding = [.. _pending];
        }

        return Task.WhenAll(outstanding);
    }

    /// <summary>Records fire-and-forget work so <see cref="WhenIdleAsync"/> can await it.</summary>
    /// <param name="work">The task that was started.</param>
    protected void Track(Task work)
    {
        ArgumentNullException.ThrowIfNull(work);

        lock (_gate)
        {
            _pending.RemoveAll(task => task.IsCompleted);
            _pending.Add(work);
        }
    }

    /// <summary>Called when the view model comes on screen.</summary>
    protected virtual void OnActivated()
    {
    }

    /// <summary>Called when it leaves. Unsubscribe here, and dispose anything holding an OS resource.</summary>
    protected virtual void OnDeactivated()
    {
    }
}

/// <summary>Runs a view model's asynchronous work so a failure becomes a notice rather than a crash.</summary>
public sealed class ViewModelWorkRunner(INotificationService notifications, ILogger logger)
{
    private readonly INotificationService _notifications = notifications;
    private readonly ILogger _logger = logger.ForContext<ViewModelWorkRunner>();

    /// <summary>Runs work, reporting any failure as a notice.</summary>
    /// <param name="failureTitle">The headline to show if it fails.</param>
    /// <param name="work">The work.</param>
    /// <param name="cancellationToken">Cancels it.</param>
    /// <returns>True when the work completed, false when it failed or was cancelled.</returns>
    public async Task<bool> RunAsync(
        string failureTitle,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureTitle);
        ArgumentNullException.ThrowIfNull(work);

        try
        {
            await work(cancellationToken).ConfigureAwait(true);
            return true;
        }
        catch (OperationCanceledException)
        {
            // Leaving a page mid-load is not a failure.
            return false;
        }
        catch (XxsmException ex)
        {
            _logger.Warning(ex, "{Title} failed", failureTitle);
            _notifications.Add(NotificationSeverity.Error, failureTitle, ex.Message);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            _logger.Error(ex, "{Title} failed", failureTitle);
            _notifications.Add(NotificationSeverity.Error, failureTitle, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            // A bug of XXSM's: logged in full, and said on screen in the system's words.
            _logger.Error(ex, "{Title} failed on an error nothing expected", failureTitle);
            _notifications.Add(NotificationSeverity.Error, failureTitle, ex.Message);
            return false;
        }
    }
}
