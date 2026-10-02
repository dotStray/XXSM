using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Xxsm.Core.GameBanana;
using Xxsm.Core.Text;

namespace Xxsm.Desktop.ViewModels;

/// <summary>One of a mod page's files, as the chooser lists it.</summary>
public sealed class GameBananaFileChoiceViewModel
{
    /// <summary>Creates the row.</summary>
    /// <param name="file">The file.</param>
    /// <param name="isInstalled">Whether it is the file the installed mod came from.</param>
    /// <param name="text">The interface's wording.</param>
    public GameBananaFileChoiceViewModel(GameBananaFile file, bool isInstalled, ITextCatalogue text)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(text);

        File = file;
        IsInstalled = isInstalled;

        var name = Trimmed(file.File)
                   ?? file.IdRow?.ToString(CultureInfo.InvariantCulture)
                   ?? string.Empty;

        // The author's description is what tells two files apart.
        if (Trimmed(file.Description) is { } description)
        {
            Title = description;
            FileName = name;
        }
        else
        {
            Title = name;
        }

        var details = new List<string>();

        if (Trimmed(file.Version) is { } version)
        {
            details.Add(text.Format(nameof(Strings.GameBananaFiles_Version), version));
        }

        if (file.Filesize is > 0 and var size)
        {
            details.Add(ByteSize.Describe(size));
        }

        if (GameBananaMod.UploadedOf(file) is { } uploaded)
        {
            details.Add(text.Format(
                nameof(Strings.GameBananaFiles_Uploaded),
                Resources.TextDates.Date(uploaded)));
        }

        if (file.DownloadCount is >= 0 and var count)
        {
            details.Add(text.Downloads((int)Math.Min(count, int.MaxValue)));
        }

        DetailText = string.Join("  ·  ", details);
    }

    /// <summary>The file.</summary>
    public GameBananaFile File { get; }

    /// <summary>Its id on GameBanana.</summary>
    public long? Id => File.IdRow;

    /// <summary>What the author calls it, or its file name when they said nothing.</summary>
    public string Title { get; }

    /// <summary>Its file name, when <see cref="Title"/> is the author's description; otherwise null.</summary>
    public string? FileName { get; }

    /// <summary>Whether there is a file name to show under the title.</summary>
    public bool HasFileName => FileName is { Length: > 0 };

    /// <summary>Its version, size, upload date and download count, as far as the page says.</summary>
    public string DetailText { get; }

    /// <summary>Whether it is the file the installed mod came from.</summary>
    public bool IsInstalled { get; }

    private static string? Trimmed(string? value) => value?.Trim() is { Length: > 0 } trimmed ? trimmed : null;
}

/// <summary>"This page has 2 files. Which one?", before any download, for the install and update panels.</summary>
public sealed partial class GameBananaFileChooserViewModel(ITextCatalogue text) : ObservableObject
{
    private readonly ITextCatalogue _text = text;

    /// <summary>The page's files, in the page's own order.</summary>
    public ObservableCollection<GameBananaFileChoiceViewModel> Files { get; } = [];

    /// <summary>The file chosen, or null until one is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private GameBananaFileChoiceViewModel? _selected;

    /// <summary>Whether a file has been chosen.</summary>
    public bool HasSelection => Selected is not null;

    /// <summary>"Yae Miko – Elegant Outfit has 2 files. Which one do you want?"</summary>
    [ObservableProperty]
    private string? _promptText;

    /// <summary>Lists a page's files.</summary>
    /// <param name="page">The page.</param>
    /// <param name="installedFileId">The file an installed copy came from, chosen to begin with; null chooses
    /// nothing.</param>
    public void Show(GameBananaMod page, long? installedFileId = null)
    {
        ArgumentNullException.ThrowIfNull(page);

        Clear();

        foreach (var file in page.Files)
        {
            Files.Add(new GameBananaFileChoiceViewModel(
                file, installedFileId is { } id && file.IdRow == id, _text));
        }

        PromptText = _text.Format(
            nameof(Strings.GameBananaFiles_Prompt),
            page.Name ?? GameBananaUrl.ForMod(page.ModId),
            _text.Files(page.Files.Count));

        Selected = Files.FirstOrDefault(file => file.IsInstalled);
    }

    /// <summary>Empties the list.</summary>
    public void Clear()
    {
        Selected = null;
        Files.Clear();
        PromptText = null;
    }
}
