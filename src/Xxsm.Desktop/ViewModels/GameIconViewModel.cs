using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Xxsm.Desktop.Services;

namespace Xxsm.Desktop.ViewModels;

/// <summary>A game's icon beside its name: its pack's picture, or its first letter when there is none.</summary>
public sealed partial class GameIconViewModel : ObservableObject
{
    private readonly object _gate = new();
    private readonly List<Task> _pending = [];
    private IBitmapLease? _lease;

    /// <summary>The game's name, from which <see cref="Initial"/> comes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Initial))]
    private string _name = string.Empty;

    /// <summary>The picture, or null while there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBitmap))]
    private Bitmap? _bitmap;

    /// <summary>Whether there is a picture; the initial shows otherwise.</summary>
    public bool HasBitmap => Bitmap is not null;

    /// <summary>What stands in for a missing picture.</summary>
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

    /// <summary>What the picture showing, or being read, was read from. Null for none.</summary>
    internal string? Key { get; set; }

    /// <summary>Waits for every read started so far. For tests, and for a render that needs the picture.</summary>
    public Task WhenIdleAsync()
    {
        lock (_gate)
        {
            return Task.WhenAll([.. _pending]);
        }
    }

    /// <summary>Shows a picture, letting go of the one before it.</summary>
    /// <param name="key">What it was read from, or null for none.</param>
    /// <param name="lease">The decoded picture, or null to show the initial.</param>
    internal void Show(string? key, IBitmapLease? lease)
    {
        Key = key;

        // The binding lets go of the old bitmap before its lease is released.
        var old = _lease;
        _lease = lease;
        Bitmap = lease?.Bitmap;
        old?.Dispose();
    }

    internal void Track(Task work)
    {
        lock (_gate)
        {
            _pending.RemoveAll(task => task.IsCompleted);
            _pending.Add(work);
        }
    }
}
