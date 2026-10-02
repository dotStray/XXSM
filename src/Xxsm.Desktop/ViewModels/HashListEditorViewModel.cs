using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Packs.Hashes;

namespace Xxsm.Desktop.ViewModels;

/// <summary>One character's hashes as text, one per line, with a live count of what it holds and rejects.</summary>
public sealed partial class HashListEditorViewModel(ITextCatalogue text) : ObservableObject
{
    private readonly ITextCatalogue _text = text;
    private Func<string, bool>? _save;
    private Func<string, bool>? _stopIgnoring;

    /// <summary>Whether the editor is showing.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>"Hashes for 'Ganyu'".</summary>
    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>What is in the box: the character's whole list once saved, one hash per line.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview))]
    private string _hashesText = string.Empty;

    /// <summary>Hashes worth a look, and why: the shared ones, or an entry that is not a hash.</summary>
    public ObservableCollection<StudioMarkedHashViewModel> Marks { get; } = [];

    /// <summary>Whether anything is marked.</summary>
    public bool HasMarks => Marks.Count > 0;

    /// <summary>What the box holds, read as you type: "12 hashes", and how much was not understood.</summary>
    public string Preview
    {
        get
        {
            var parsed = HashPaste.Parse(HashesText);
            var found = parsed.Hashes.Select(h => h.Entry.Hash).Distinct(StringComparer.OrdinalIgnoreCase).Count();

            return parsed.Rejected.Count == 0
                ? _text.Format(nameof(Strings.Studio_Hashes_Preview), _text.Hashes(found))
                : _text.Format(nameof(Strings.Studio_Hashes_PreviewRejected), _text.Hashes(found), parsed.Rejected.Count);
        }
    }

    /// <summary>Opens the editor on a list of hashes.</summary>
    /// <param name="title">What the panel is called.</param>
    /// <param name="hashes">The hashes, in the order they should be shown.</param>
    /// <param name="save">What <em>Save</em> does with the box; true closes the editor, false keeps what was
    /// typed.</param>
    /// <param name="marks">Hashes to point out, and why. None by default.</param>
    /// <param name="stopIgnoring">Makes a marked ignored hash count again, true when it worked; null when the host
    /// has no ignore list.</param>
    public void Open(
        string title,
        IEnumerable<string> hashes,
        Func<string, bool> save,
        IReadOnlyList<StudioMarkedHashViewModel>? marks = null,
        Func<string, bool>? stopIgnoring = null)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        ArgumentNullException.ThrowIfNull(save);

        Title = title ?? string.Empty;
        HashesText = string.Join(Environment.NewLine, hashes);
        _save = save;
        _stopIgnoring = stopIgnoring;

        Marks.Clear();

        foreach (var mark in marks ?? [])
        {
            Marks.Add(mark);
        }

        OnPropertyChanged(nameof(HasMarks));
        IsOpen = true;
    }

    /// <summary>Takes one marked hash out of the box. Nothing is written until <em>Save</em>.</summary>
    [RelayCommand]
    public void RemoveMarked(StudioMarkedHashViewModel? mark)
    {
        if (mark is null || !Marks.Contains(mark))
        {
            return;
        }

        HashesText = string.Join(
            Environment.NewLine,
            HashesText
                .Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .Where(line => !string.Equals(line.Trim(), mark.Hash.Trim(), StringComparison.OrdinalIgnoreCase)));

        Marks.Remove(mark);
        OnPropertyChanged(nameof(HasMarks));
    }

    /// <summary>Makes a hash the pack ignores count again; it stays in the box.</summary>
    [RelayCommand]
    public void StopIgnoring(StudioMarkedHashViewModel? mark)
    {
        if (mark is not { IsIgnored: true } || _stopIgnoring is not { } stop || !Marks.Contains(mark))
        {
            return;
        }

        if (stop(mark.Hash))
        {
            Marks.Remove(mark);
            OnPropertyChanged(nameof(HasMarks));
        }
    }

    /// <summary>Whether a marked hash can be made to count again here.</summary>
    public bool CanStopIgnoring => _stopIgnoring is not null;

    /// <summary>Hands the box to whoever opened the editor, and closes it if they took it.</summary>
    [RelayCommand]
    public void Save()
    {
        if (_save is { } save && save(HashesText))
        {
            Close();
        }
    }

    /// <summary>Closes the editor without changing anything.</summary>
    [RelayCommand]
    public void Cancel() => Close();

    /// <summary>Closes the editor and forgets what it was showing.</summary>
    public void Close()
    {
        IsOpen = false;
        _save = null;
        _stopIgnoring = null;
        Marks.Clear();
        OnPropertyChanged(nameof(HasMarks));
    }
}
