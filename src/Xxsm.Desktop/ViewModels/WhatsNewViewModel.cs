using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Packs.Merge;
using Xxsm.Packs.Registry;

namespace Xxsm.Desktop.ViewModels;

/// <summary>One kind of change in <em>What's new</em>: its heading with a count, and the names.</summary>
/// <param name="Heading">For example <c>New portraits (3)</c>.</param>
/// <param name="Names">The characters, comma-separated, as one wrapping paragraph.</param>
public sealed record WhatsNewSection(string Heading, string Names);

/// <summary><em>What's new</em>: what a game's last pack update changed from the version it replaced.</summary>
public sealed partial class WhatsNewViewModel(ITextCatalogue text) : ObservableObject
{
    private readonly ITextCatalogue _text = text;

    /// <summary>Whether the panel is on screen.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>For example <c>What's new in Honkai: Star Rail</c>.</summary>
    [ObservableProperty]
    private string _heading = string.Empty;

    /// <summary>Which version replaced which.</summary>
    [ObservableProperty]
    private string _versionsText = string.Empty;

    /// <summary>Whether the new version changed none of the things the list names.</summary>
    [ObservableProperty]
    private bool _isEmpty;

    /// <summary>One entry per kind of change that happened, in a fixed order.</summary>
    public ObservableCollection<WhatsNewSection> Sections { get; } = [];

    /// <summary>Fills the panel and opens it.</summary>
    /// <param name="changes">What the update changed.</param>
    /// <param name="gameName">The game's name, for the heading.</param>
    public void Show(PackChanges changes, string gameName)
    {
        ArgumentNullException.ThrowIfNull(changes);

        Heading = _text.Format(nameof(Strings.WhatsNew_Heading), gameName is { Length: > 0 } ? gameName : changes.GameId);
        VersionsText = _text.Format(nameof(Strings.WhatsNew_Versions), PackVersionText.Display(changes.FromVersion), PackVersionText.Display(changes.ToVersion));
        IsEmpty = changes.IsEmpty;

        Sections.Clear();
        Add(nameof(Strings.WhatsNew_Added), changes.Added);
        Add(nameof(Strings.WhatsNew_AddedOutfits), changes.AddedOutfits);
        Add(nameof(Strings.WhatsNew_Removed), changes.Removed);
        Add(nameof(Strings.WhatsNew_Renamed),
            [.. changes.Renamed.Select(r => _text.Format(nameof(Strings.WhatsNew_RenamedItem), r.From, r.To))]);
        Add(nameof(Strings.WhatsNew_FirstHashes), changes.FirstHashes);
        Add(nameof(Strings.WhatsNew_HashesChanged),
            [.. changes.HashesChanged.Select(Describe)]);
        Add(nameof(Strings.WhatsNew_NewPortraits), changes.NewPortraits);
        Add(nameof(Strings.WhatsNew_ChangedPortraits), changes.ChangedPortraits);

        IsOpen = true;
    }

    /// <summary>Closes the panel. The list stays on the card's <em>What's new</em> button.</summary>
    [RelayCommand]
    private void Close() => IsOpen = false;

    /// <summary>For example <c>Ganyu (2 added, 1 removed)</c>; a side with none is left out.</summary>
    private string Describe(PackHashChange change)
    {
        var parts = new List<string>(2);

        if (change.AddedCount > 0)
        {
            parts.Add(_text.Format(nameof(Strings.WhatsNew_HashesAdded), change.AddedCount));
        }

        if (change.RemovedCount > 0)
        {
            parts.Add(_text.Format(nameof(Strings.WhatsNew_HashesRemoved), change.RemovedCount));
        }

        return _text.Format(nameof(Strings.WhatsNew_HashesItem), change.Name, string.Join(_text[nameof(Strings.WhatsNew_Separator)], parts));
    }

    private void Add(string heading, IReadOnlyList<string> names)
    {
        if (names.Count > 0)
        {
            Sections.Add(new WhatsNewSection(
                _text.Format(nameof(Strings.WhatsNew_Section), _text[heading], names.Count),
                string.Join(_text[nameof(Strings.WhatsNew_Separator)], names)));
        }
    }
}
