using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Core;
using Xxsm.Core.Io;
using Xxsm.Desktop.Services;
using Xxsm.Packs.Characters;
using Xxsm.Packs.Model;
using Xxsm.Packs.Serialization;
using Xxsm.Packs.Studio;

namespace Xxsm.Desktop.ViewModels;

/// <summary>One of the game's attributes in the Game settings panel.</summary>
public sealed partial class StudioAttributeEditorViewModel : ObservableObject
{
    /// <summary>Creates a row.</summary>
    /// <param name="id">The attribute's id; empty for one being added.</param>
    /// <param name="definition">What the game says about it, or null for a new one.</param>
    public StudioAttributeEditorViewModel(string id, AttributeDefinition? definition)
    {
        OriginalId = id.Length > 0 ? id : null;
        _id = id;
        _label = definition?.DisplayName ?? string.Empty;
        _isNumber = definition?.IsNumeric ?? false;
        _valuesText = string.Join(
            ", ",
            (definition?.Values ?? []).Select(v =>
                string.IsNullOrWhiteSpace(v.DisplayName) || string.Equals(v.DisplayName, v.Id, StringComparison.Ordinal)
                    ? v.Id
                    : $"{v.Id}={v.DisplayName}"));
    }

    /// <summary>The id the game had for it when the panel opened, or null when it is new.</summary>
    public string? OriginalId { get; }

    /// <summary>Whether it is being added, so its id can still be typed.</summary>
    public bool IsNew => OriginalId is null;

    /// <summary>The attribute's id, such as <c>element</c>. Fixed once the game has it: characters store it.</summary>
    [ObservableProperty]
    private string _id;

    /// <summary>What its filter chip and table column are called, such as <c>Element</c>.</summary>
    [ObservableProperty]
    private string _label;

    /// <summary>Whether it holds a number, such as a rarity, rather than one of a list of values.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValues))]
    private bool _isNumber;

    /// <summary>Its values, comma-separated, each as <c>id</c> or <c>id=Label</c>.</summary>
    [ObservableProperty]
    private string _valuesText;

    /// <summary>Whether a Problems panel button opened the panel for this attribute.</summary>
    [ObservableProperty]
    private bool _isHighlighted;

    /// <summary>Whether the values box applies.</summary>
    public bool HasValues => !IsNumber;

    /// <summary>What the game will hold for it.</summary>
    public AttributeDefinition ToDefinition() => new()
    {
        DisplayName = string.IsNullOrWhiteSpace(Label) ? null : Label.Trim(),
        Kind = IsNumber ? "number" : null,
        Values = IsNumber
            ? null
            : [
                .. ValuesText
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => value.Split('=', 2, StringSplitOptions.TrimEntries))
                    .Select(parts => new AttributeValueDefinition
                    {
                        Id = parts[0],
                        DisplayName = parts.Length == 2 && parts[1].Length > 0 ? parts[1] : null,
                    }),
            ],
    };
}

/// <summary>The Game settings panel: the game's details and attributes, saved as one change.</summary>
public sealed partial class StudioGameSettingsViewModel : ObservableObject
{
    private readonly IStudioDraftStore _store;
    private readonly IStoragePicker _picker;
    private readonly ITextCatalogue _text;
    private readonly Func<StudioDraftSession?> _session;
    private readonly Func<Func<CancellationToken, Task>, Task<bool>> _run;
    private readonly List<string> _removed = [];

    /// <summary>Creates the panel.</summary>
    /// <param name="store">Copies an icon into the draft.</param>
    /// <param name="picker">Chooses an icon and a Mods folder.</param>
    /// <param name="text">The interface's wording.</param>
    /// <param name="session">The open draft, or null.</param>
    /// <param name="run">Runs work, turning a failure into a notice.</param>
    public StudioGameSettingsViewModel(
        IStudioDraftStore store,
        IStoragePicker picker,
        ITextCatalogue text,
        Func<StudioDraftSession?> session,
        Func<Func<CancellationToken, Task>, Task<bool>> run)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(picker);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(run);

        _store = store;
        _picker = picker;
        _text = text;
        _session = session;
        _run = run;
    }

    /// <summary>Whether the panel is showing.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>The game's name.</summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>A short form of the name.</summary>
    [ObservableProperty]
    private string _shortName = string.Empty;

    /// <summary>The folder name XXMI uses for the game.</summary>
    [ObservableProperty]
    private string _importer = string.Empty;

    /// <summary>What marks a disabled mod folder. Empty means the usual <c>DISABLED_</c>.</summary>
    [ObservableProperty]
    private string _disabledPrefix = string.Empty;

    /// <summary>The game's Mods folder, which Try it starts at.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModsFolderText))]
    private string? _modsFolder;

    /// <summary>The Mods folder, or that none has been chosen.</summary>
    public string ModsFolderText =>
        ModsFolder is { } folder ? PathDisplay.Show(folder) : _text[nameof(Strings.Studio_Settings_NoModsFolder)];

    /// <summary>The icon the game has now, as the draft names it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconText))]
    private string? _icon;

    /// <summary>A picture chosen to be the new icon, copied in on Save.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconText))]
    private string? _newIcon;

    /// <summary>Why the last Save was refused, or null.</summary>
    [ObservableProperty]
    private string? _error;

    /// <summary>The icon in words: the new file's name, the current one's, or that there is none.</summary>
    public string IconText => NewIcon is { } chosen
        ? Path.GetFileName(chosen)
        : Icon is { Length: > 0 } current
            ? current
            : _text[nameof(Strings.Studio_Settings_NoIcon)];

    /// <summary>The game's attributes.</summary>
    public ObservableCollection<StudioAttributeEditorViewModel> Attributes { get; } = [];

    /// <summary>Opens the panel on the open draft's game.</summary>
    [RelayCommand]
    public void Open()
    {
        if (_session() is not { } session)
        {
            return;
        }

        var draft = session.Current;
        var game = draft.Game;

        Name = game.DisplayName;
        ShortName = game.ShortName ?? string.Empty;
        Importer = game.Importer ?? string.Empty;
        DisabledPrefix = string.Equals(game.DisabledPrefix, PackSchema.DefaultDisabledPrefix, StringComparison.Ordinal)
            ? string.Empty
            : game.DisabledPrefix ?? string.Empty;
        ModsFolder = draft.Info.ModsDirectory;
        Icon = game.Icon;
        NewIcon = null;
        Error = null;
        IsNameHighlighted = false;
        _removed.Clear();

        Attributes.Clear();

        foreach (var (id, definition) in game.Attributes ?? new Dictionary<string, AttributeDefinition>())
        {
            Attributes.Add(new StudioAttributeEditorViewModel(id, definition));
        }

        IsOpen = true;
    }

    /// <summary>Whether a Problems panel button opened the panel for the game's name.</summary>
    [ObservableProperty]
    private bool _isNameHighlighted;

    /// <summary>Opens the panel with one attribute or the name marked; an undeclared one in use is added.</summary>
    /// <param name="attributeId">The attribute, or null.</param>
    /// <param name="name">Whether the name is what needs fixing.</param>
    public void OpenAt(string? attributeId, bool name)
    {
        Open();

        if (!IsOpen)
        {
            return;
        }

        IsNameHighlighted = name;

        if (attributeId is not { Length: > 0 })
        {
            return;
        }

        var attribute = Attributes.FirstOrDefault(a => string.Equals(a.Id, attributeId, StringComparison.OrdinalIgnoreCase));

        if (attribute is null)
        {
            attribute = new StudioAttributeEditorViewModel(string.Empty, null) { Id = attributeId };
            Attributes.Add(attribute);
        }

        attribute.IsHighlighted = true;
    }

    /// <summary>Closes the panel without changing anything.</summary>
    [RelayCommand]
    public void Close() => IsOpen = false;

    /// <summary>Moves an attribute to another place in the list, written on <em>Save</em>.</summary>
    /// <param name="from">Where it is.</param>
    /// <param name="to">Where it goes. Clamped to the list.</param>
    public void MoveAttribute(int from, int to)
    {
        if (from < 0 || from >= Attributes.Count)
        {
            return;
        }

        to = Math.Clamp(to, 0, Attributes.Count - 1);

        if (from != to)
        {
            Attributes.Move(from, to);
        }
    }

    /// <summary>Moves an attribute one place up: the Up arrow on its handle.</summary>
    [RelayCommand]
    public void MoveAttributeUp(StudioAttributeEditorViewModel? attribute)
    {
        if (attribute is not null && Attributes.IndexOf(attribute) is var at and > 0)
        {
            MoveAttribute(at, at - 1);
        }
    }

    /// <summary>Moves an attribute one place down: the Down arrow on its handle.</summary>
    [RelayCommand]
    public void MoveAttributeDown(StudioAttributeEditorViewModel? attribute)
    {
        if (attribute is not null && Attributes.IndexOf(attribute) is var at and >= 0)
        {
            MoveAttribute(at, at + 1);
        }
    }

    /// <summary>Adds an empty attribute to fill in.</summary>
    [RelayCommand]
    public void AddAttribute() => Attributes.Add(new StudioAttributeEditorViewModel(string.Empty, null));

    /// <summary>Removes an attribute. On Save it goes from every character too.</summary>
    [RelayCommand]
    public void RemoveAttribute(StudioAttributeEditorViewModel? attribute)
    {
        if (attribute is null || !Attributes.Remove(attribute))
        {
            return;
        }

        if (attribute.OriginalId is { } id)
        {
            _removed.Add(id);
        }
    }

    /// <summary>Chooses a picture for the game's icon.</summary>
    [RelayCommand]
    public Task ChooseIconAsync() =>
        _run(async ct =>
        {
            var file = await _picker
                .PickFileAsync(
                    _text[nameof(Strings.Studio_Wizard_PickIcon)],
                    [new FileTypeFilter(_text[nameof(Strings.Studio_Wizard_Images)], StudioDraftStore.ImageExtensions)],
                    null,
                    ct)
                .ConfigureAwait(true);

            if (file is not null)
            {
                NewIcon = file;
            }
        });

    /// <summary>Takes the icon away. On Save the game has none.</summary>
    [RelayCommand]
    public void RemoveIcon()
    {
        NewIcon = null;
        Icon = null;
    }

    /// <summary>Chooses the game's Mods folder.</summary>
    [RelayCommand]
    public Task ChooseModsFolderAsync() =>
        _run(async ct =>
        {
            var folder = await _picker
                .PickFolderAsync(_text[nameof(Strings.Studio_Wizard_PickMods)], ModsFolder, ct)
                .ConfigureAwait(true);

            if (folder is not null)
            {
                ModsFolder = folder;
            }
        });

    /// <summary>Writes the panel into the draft as one change and closes it; a refusal keeps it open.</summary>
    /// <returns>A task that completes when the draft has the change, or it was refused.</returns>
    [RelayCommand]
    public Task SaveAsync()
    {
        if (_session() is not { } session)
        {
            return Task.CompletedTask;
        }

        return _run(async ct =>
        {
            Error = null;

            try
            {
                var draft = Build(session.Current);

                if (NewIcon is { } picture)
                {
                    var stored = await _store.StoreImageAsync(
                        draft.GameId, "_game", Xxsm.Core.Mods.PreviewImageSource.FromFile(picture), ct).ConfigureAwait(true);
                    draft = DraftEdits.EditGame(draft, new GameEdit { Icon = EditField<string>.To(stored) });
                }

                if (!ReferenceEquals(_session(), session))
                {
                    throw new ModOperationException(_text[nameof(Strings.Studio_Manager_DraftClosed)]);
                }

                // Saving what was already there is no change, and leaves no Undo step.
                if (!Same(draft, session.Current))
                {
                    session.Apply(draft);
                }

                IsOpen = false;
            }
            catch (ModOperationException ex)
            {
                Error = ex.Message;
            }
        });
    }

    private PackDraft Build(PackDraft draft)
    {
        draft = DraftEdits.EditGame(draft, new GameEdit
        {
            DisplayName = EditField<string>.To(Name),
            ShortName = EditField<string>.To(ShortName),
            Importer = EditField<string>.To(Importer),
            DisabledPrefix = EditField<string>.To(DisabledPrefix),
            Icon = Changed(Icon, draft.Game.Icon) ? EditField<string>.To(Icon) : EditField<string>.Unchanged,
        });

        foreach (var id in _removed)
        {
            draft = DraftEdits.RemoveAttributeDefinition(draft, id);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var attribute in Attributes)
        {
            var id = attribute.Id.Trim();

            if (id.Length == 0)
            {
                throw new ModOperationException(_text[nameof(Strings.Studio_Settings_AttributeNeedsId)]);
            }

            if (!seen.Add(id))
            {
                throw new ModOperationException(_text.Format(nameof(Strings.Studio_Settings_AttributeTwice), id));
            }

            if (attribute.IsNew && (draft.Game.Attributes?.Keys.Contains(id, StringComparer.OrdinalIgnoreCase) ?? false))
            {
                throw new ModOperationException(_text.Format(nameof(Strings.Studio_Settings_AttributeTwice), id));
            }

            draft = DraftEdits.SetAttributeDefinition(draft, id, attribute.ToDefinition());
        }

        draft = DraftEdits.OrderAttributes(draft, [.. Attributes.Select(attribute => attribute.Id.Trim())]);

        var folder = string.IsNullOrWhiteSpace(ModsFolder) ? null : ModsFolder;

        return string.Equals(folder, draft.Info.ModsDirectory, StringComparison.Ordinal)
            ? draft
            : draft with { Info = draft.Info with { ModsDirectory = folder } };
    }

    private static bool Same(PackDraft one, PackDraft two) =>
        string.Equals(one.Info.ModsDirectory, two.Info.ModsDirectory, StringComparison.Ordinal)
        && string.Equals(
            JsonSerializer.Serialize(one.Game, PackJsonContext.Default.GameDefinition),
            JsonSerializer.Serialize(two.Game, PackJsonContext.Default.GameDefinition),
            StringComparison.Ordinal)
        && ReferenceEquals(one.Variants, two.Variants);

    private static bool Changed(string? left, string? right) =>
        !string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);
}
