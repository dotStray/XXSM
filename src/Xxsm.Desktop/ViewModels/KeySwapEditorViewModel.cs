using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Core;
using Xxsm.Core.Ini;
using Xxsm.Core.Io;

namespace Xxsm.Desktop.ViewModels;

/// <summary>One editable line of a key binding.</summary>
public sealed partial class KeySwapFieldViewModel : ObservableObject
{
    /// <summary>Creates the field.</summary>
    /// <param name="line">The line as it was read.</param>
    /// <param name="label">What to call it: "Key", "Back", or the variable's own name.</param>
    internal KeySwapFieldViewModel(KeySwapField line, string label)
    {
        Field = line;
        Label = label;
        _value = line.Value;
    }

    /// <summary>The line as it was read.</summary>
    public KeySwapField Field { get; }

    /// <summary>What to call it.</summary>
    public string Label { get; }

    /// <summary>The value in the box.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged))]
    private string _value;

    /// <summary>Whether the box differs from the file.</summary>
    public bool IsChanged => !string.Equals(Value.Trim(), Field.Value, StringComparison.Ordinal);
}

/// <summary>One key binding: a <c>[Key…]</c> section of one of the mod's INIs.</summary>
public sealed class KeySwapSectionViewModel
{
    /// <summary>Creates the binding.</summary>
    /// <param name="section">The binding as it was read.</param>
    /// <param name="fields">Its editable lines.</param>
    internal KeySwapSectionViewModel(KeySwapSection section, IReadOnlyList<KeySwapFieldViewModel> fields)
    {
        Section = section;
        Fields = fields;
    }

    /// <summary>The binding as it was read.</summary>
    public KeySwapSection Section { get; }

    /// <summary>Its name, as the INI has it.</summary>
    public string Title => Section.Section;

    /// <summary>Which file it is in, and what kind of binding it is.</summary>
    public string Subtitle => Section.Type is { Length: > 0 } type ? $"{PathDisplay.Show(Section.File)} · {type}" : PathDisplay.Show(Section.File);

    /// <summary>Its editable lines.</summary>
    public IReadOnlyList<KeySwapFieldViewModel> Fields { get; }
}

/// <summary>A mod's key bindings in its detail pane, edited in place and saved by the pane's own Save.</summary>
public sealed partial class KeySwapEditorViewModel(IKeySwapService keys, ITextCatalogue text) : ObservableObject
{
    private readonly IKeySwapService _keys = keys;
    private readonly ITextCatalogue _text = text;
    private int _version;

    /// <summary>The mod whose bindings are showing, or <c>null</c>.</summary>
    public string? ModFolder { get; private set; }

    /// <summary>How many bindings show before <em>Show all</em>, as the download list does.</summary>
    public const int ShownAtFirst = 2;

    /// <summary>The bindings.</summary>
    public ObservableCollection<KeySwapSectionViewModel> Sections { get; } = [];

    /// <summary>The bindings drawn: the first <see cref="ShownAtFirst"/>, or all of them once asked.</summary>
    public ObservableCollection<KeySwapSectionViewModel> ShownSections { get; } = [];

    /// <summary>Whether every binding is drawn.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAllText))]
    private bool _showAll;

    /// <summary>Whether there are more bindings than are drawn at first.</summary>
    public bool HasMore => Sections.Count > ShownAtFirst;

    /// <summary>The button under the list: <em>Show all 7 keys</em>, or <em>Show fewer</em>.</summary>
    public string ShowAllText => ShowAll
        ? _text[nameof(Strings.Keys_ShowFewer)]
        : _text.Format(nameof(Strings.Keys_ShowAll), _text.Keys(Sections.Count));

    /// <summary>Draws every binding, or the first few again.</summary>
    [RelayCommand]
    private void ToggleShowAll() => ShowAll = !ShowAll;

    partial void OnShowAllChanged(bool value) => RefreshShown();

    private void RefreshShown()
    {
        ShownSections.Clear();

        foreach (var section in ShowAll ? Sections : Sections.Take(ShownAtFirst))
        {
            ShownSections.Add(section);
        }

        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(ShowAllText));
    }

    /// <summary>INIs that could not be read, each as a sentence.</summary>
    public ObservableCollection<string> Problems { get; } = [];

    /// <summary>Whether the mod has any bindings, so the section is shown.</summary>
    public bool HasBindings => Sections.Count > 0;

    /// <summary>Whether any INI could not be read.</summary>
    public bool HasProblems => Problems.Count > 0;

    /// <summary>Whether any box differs from its file.</summary>
    public bool IsDirty => Sections.Any(section => section.Fields.Any(box => box.IsChanged));

    /// <summary>Reads a mod's bindings, or clears the section.</summary>
    /// <param name="modFolder">The mod, or <c>null</c> for none.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task LoadAsync(string? modFolder, CancellationToken cancellationToken)
    {
        // Selecting quickly starts several reads; only the last one's answer is shown.
        var version = ++_version;
        KeySwapReadResult? read = null;

        if (modFolder is { Length: > 0 })
        {
            try
            {
                read = await _keys.ReadAsync(modFolder, cancellationToken).ConfigureAwait(true);
            }
            catch (ModOperationException ex)
            {
                read = new KeySwapReadResult([], [ex.Message]);
            }
        }

        if (version != _version)
        {
            return;
        }

        // Another mod starts folded again; the same one read again keeps what was typed into lines that did not change.
        var same = string.Equals(ModFolder, modFolder, StringComparison.Ordinal);
        var typed = same ? Edits() : [];

        if (!same)
        {
            ShowAll = false;
        }

        ModFolder = modFolder;
        Show(read);

        foreach (var edit in typed)
        {
            var box = Sections
                .Where(section => string.Equals(section.Section.File, edit.File, StringComparison.Ordinal))
                .SelectMany(section => section.Fields)
                .FirstOrDefault(field => field.Field.Line == edit.Line && string.Equals(field.Field.Value, edit.ExpectedValue, StringComparison.Ordinal));

            box?.Value = edit.Value;
        }
    }

    /// <summary>The changes to write.</summary>
    public IReadOnlyList<KeySwapEdit> Edits() =>
    [
        .. Sections.SelectMany(section => section.Fields
            .Where(box => box.IsChanged)
            .Select(box => new KeySwapEdit(section.Section.File, box.Field.Line, box.Field.Value, box.Value))),
    ];

    /// <summary>Writes the changed boxes into the mod's INIs and reads them again at once.</summary>
    /// <exception cref="ModOperationException">A value is refused, or a file changed since it was read.</exception>
    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (!IsDirty || ModFolder is not { } folder)
        {
            return;
        }

        await _keys.WriteAsync(folder, Edits(), cancellationToken).ConfigureAwait(true);
        await LoadAsync(folder, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Puts every box back to what its file says.</summary>
    public void Revert()
    {
        foreach (var box in Sections.SelectMany(section => section.Fields))
        {
            box.Value = box.Field.Value;
        }
    }

    private void Show(KeySwapReadResult? read)
    {
        foreach (var box in Sections.SelectMany(section => section.Fields))
        {
            box.PropertyChanged -= OnFieldChanged;
        }

        Sections.Clear();
        Problems.Clear();

        foreach (var section in read?.Sections ?? [])
        {
            var fields = section.Fields.Select(line => new KeySwapFieldViewModel(line, Label(line))).ToList();

            foreach (var box in fields)
            {
                box.PropertyChanged += OnFieldChanged;
            }

            Sections.Add(new KeySwapSectionViewModel(section, fields));
        }

        foreach (var problem in read?.Problems ?? [])
        {
            Problems.Add(problem);
        }

        RefreshShown();
        OnPropertyChanged(nameof(HasBindings));
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(IsDirty));
    }

    private string Label(KeySwapField line) => line.Kind switch
    {
        KeySwapFieldKind.Key => _text[nameof(Strings.Keys_Key)],
        KeySwapFieldKind.Back => _text[nameof(Strings.Keys_Back)],
        _ => line.Name,
    };

    private void OnFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(KeySwapFieldViewModel.IsChanged))
        {
            OnPropertyChanged(nameof(IsDirty));
        }
    }
}
