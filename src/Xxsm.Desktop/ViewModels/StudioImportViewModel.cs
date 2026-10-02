using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Desktop.Services;
using Xxsm.Packs.Characters;
using Xxsm.Packs.Studio;

namespace Xxsm.Desktop.ViewModels;

/// <summary>Where a Studio import reads from.</summary>
public enum StudioImportKind
{
    /// <summary>A model-importer assets repository, as a folder or an archive.</summary>
    Assets,

    /// <summary>A populated Mods folder.</summary>
    ModsFolder,

    /// <summary>A folder of pictures.</summary>
    Pictures,

    /// <summary>A CSV or TSV spreadsheet.</summary>
    Spreadsheet,

    /// <summary>The corrections a user made in the Character Manager.</summary>
    Corrections,
}

/// <summary>The Core services behind the import panel, as one dependency.</summary>
/// <param name="Assets">Reads an assets repository.</param>
/// <param name="ModsFolder">Reads a Mods folder.</param>
/// <param name="Pictures">Matches a folder of pictures.</param>
/// <param name="Spreadsheet">Reads a spreadsheet.</param>
/// <param name="Corrections">Promotes the user's corrections.</param>
public sealed record StudioImportServices(
    IHashAssetsImporter Assets,
    IModsFolderImporter ModsFolder,
    IPortraitFolderImporter Pictures,
    ISpreadsheetImporter Spreadsheet,
    IOverlayPromotion Corrections);

/// <summary>One row of an import preview, with its tick box.</summary>
/// <param name="name">What the row is about.</param>
/// <param name="detail">What importing it would do.</param>
/// <param name="include">Whether it starts ticked.</param>
/// <param name="link">The outfit link the importer guessed, in words, or null.</param>
/// <param name="notes">Anything worth saying beside it, or null.</param>
/// <param name="assignTo">For a picture, the character it matched, or null.</param>
/// <param name="characters">For a picture, the characters it could be given to; empty otherwise.</param>
public sealed partial class StudioImportRowViewModel(
    string name,
    string detail,
    bool include,
    string? link = null,
    string? notes = null,
    string? assignTo = null,
    IReadOnlyList<string>? characters = null) : ObservableObject
{
    /// <summary>What the row is about.</summary>
    public string Name { get; } = name;

    /// <summary>What importing it would do.</summary>
    public string Detail { get; } = detail;

    /// <summary>The outfit link the importer guessed, in words, or null.</summary>
    public string? LinkText { get; } = link;

    /// <summary>Whether there is a guessed outfit link to keep or drop.</summary>
    public bool HasLink => LinkText is not null;

    /// <summary>Anything worth saying beside the row, or null.</summary>
    public string? Notes { get; } = notes;

    /// <summary>Whether there are notes.</summary>
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);

    /// <summary>For a picture, the characters it could be given to.</summary>
    public IReadOnlyList<string> Characters { get; } = characters ?? [];

    /// <summary>Whether a character can be chosen for the row.</summary>
    public bool CanAssign => Characters.Count > 0;

    /// <summary>Whether to import the row.</summary>
    [ObservableProperty]
    private bool _include = include;

    /// <summary>Whether to keep the guessed outfit link. Unticked, the character is not an outfit of anyone.</summary>
    [ObservableProperty]
    private bool _keepLink = true;

    /// <summary>For a picture, the character to give it to.</summary>
    [ObservableProperty]
    private string? _assignTo = assignTo;

    partial void OnAssignToChanged(string? value)
    {
        // Choosing a character for a picture is saying to use it.
        if (value is not null)
        {
            Include = true;
        }
    }
}

/// <summary>A choice of what a spreadsheet column is for.</summary>
/// <param name="Label">The words shown for it.</param>
/// <param name="Field">The field.</param>
/// <param name="AttributeId">For an attribute, which one.</param>
public sealed record StudioSheetFieldOption(string Label, SpreadsheetField Field, string? AttributeId);

/// <summary>One spreadsheet column, and what it is used for.</summary>
/// <param name="index">The 0-based column.</param>
/// <param name="header">Its header.</param>
/// <param name="options">What it could be used for.</param>
/// <param name="selected">What it is used for now.</param>
public sealed partial class StudioSheetColumnViewModel(
    int index,
    string header,
    IReadOnlyList<StudioSheetFieldOption> options,
    StudioSheetFieldOption selected) : ObservableObject
{
    /// <summary>The 0-based column.</summary>
    public int Index { get; } = index;

    /// <summary>Its header.</summary>
    public string Header { get; } = header;

    /// <summary>What it could be used for.</summary>
    public IReadOnlyList<StudioSheetFieldOption> Options { get; } = options;

    /// <summary>What it is used for.</summary>
    [ObservableProperty]
    private StudioSheetFieldOption? _selected = selected;
}

/// <summary>The import panel: every importer's preview, ticked rows applied to the draft as one change.</summary>
public sealed partial class StudioImportViewModel : ObservableObject
{
    private static readonly string[] ArchiveExtensions = [".zip", ".rar", ".7z", ".tar", ".gz"];
    private static readonly string[] SheetExtensions = [".csv", ".tsv", ".txt"];

    private readonly StudioImportServices _importers;
    private readonly IStoragePicker _picker;
    private readonly ITextCatalogue _text;
    private readonly TimeProvider _time;
    private readonly Func<StudioDraftSession?> _session;
    private readonly Func<Func<CancellationToken, Task>, Task<bool>> _run;
    private readonly Func<string, Func<CancellationToken, Task>, Task<bool>> _runBusy;

    private CharacterImportPlan? _characterPlan;
    private PortraitImportPlan? _portraitPlan;
    private SpreadsheetImportPlan? _sheetPlan;
    private OverlayPromotionPlan? _promotionPlan;
    private bool _buildingColumns;

    /// <summary>Creates the panel.</summary>
    /// <param name="importers">The importers.</param>
    /// <param name="picker">Chooses the source.</param>
    /// <param name="text">The interface's wording.</param>
    /// <param name="time">Stamps the draft's import history.</param>
    /// <param name="session">The open draft, or null.</param>
    /// <param name="run">Runs work so a failure becomes a notice.</param>
    /// <param name="runBusy">The same, with the page's busy message showing.</param>
    public StudioImportViewModel(
        StudioImportServices importers,
        IStoragePicker picker,
        ITextCatalogue text,
        TimeProvider time,
        Func<StudioDraftSession?> session,
        Func<Func<CancellationToken, Task>, Task<bool>> run,
        Func<string, Func<CancellationToken, Task>, Task<bool>> runBusy)
    {
        ArgumentNullException.ThrowIfNull(importers);
        ArgumentNullException.ThrowIfNull(picker);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(runBusy);

        _importers = importers;
        _picker = picker;
        _text = text;
        _time = time;
        _session = session;
        _run = run;
        _runBusy = runBusy;
    }

    /// <summary>Whether the panel is showing.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>Which importer the panel is for.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading), nameof(Intro), nameof(NeedsSource), nameof(SourceIsFile))]
    [NotifyPropertyChangedFor(nameof(SourceIsFolder), nameof(CanChooseArchive))]
    private StudioImportKind _kind;

    /// <summary>The folder or file to import from.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSource))]
    private string? _source;

    /// <summary>The panel's heading.</summary>
    public string Heading => _text[Kind switch
    {
        StudioImportKind.Assets => nameof(Strings.Studio_Import_Assets_Heading),
        StudioImportKind.ModsFolder => nameof(Strings.Studio_Import_Mods_Heading),
        StudioImportKind.Pictures => nameof(Strings.Studio_Import_Pictures_Heading),
        StudioImportKind.Spreadsheet => nameof(Strings.Studio_Import_Sheet_Heading),
        _ => nameof(Strings.Studio_Import_Corrections_Heading),
    }];

    /// <summary>What this import does, in a sentence or two.</summary>
    public string Intro => _text[Kind switch
    {
        StudioImportKind.Assets => nameof(Strings.Studio_Import_Assets_Intro),
        StudioImportKind.ModsFolder => nameof(Strings.Studio_Import_Mods_Intro),
        StudioImportKind.Pictures => nameof(Strings.Studio_Import_Pictures_Intro),
        StudioImportKind.Spreadsheet => nameof(Strings.Studio_Import_Sheet_Intro),
        _ => nameof(Strings.Studio_Import_Corrections_Intro),
    }];

    /// <summary>Whether a source has been chosen.</summary>
    public bool HasSource => !string.IsNullOrWhiteSpace(Source);

    /// <summary>Whether this import reads a folder or file. Corrections need none.</summary>
    public bool NeedsSource => Kind != StudioImportKind.Corrections;

    /// <summary>Whether the source is a file.</summary>
    public bool SourceIsFile => Kind == StudioImportKind.Spreadsheet;

    /// <summary>Whether the source can be a folder.</summary>
    public bool SourceIsFolder => Kind is StudioImportKind.Assets or StudioImportKind.ModsFolder or StudioImportKind.Pictures;

    /// <summary>Whether the source can be an archive, as GitHub's <em>Download ZIP</em> gives.</summary>
    public bool CanChooseArchive => Kind == StudioImportKind.Assets;

    /// <summary>The preview's rows.</summary>
    public ObservableCollection<StudioImportRowViewModel> Rows { get; } = [];

    /// <summary>A spreadsheet's columns, and what each is for.</summary>
    public ObservableCollection<StudioSheetColumnViewModel> SheetColumns { get; } = [];

    /// <summary>Sentences about the source as a whole, or about rows an import skipped.</summary>
    public ObservableCollection<string> Messages { get; } = [];

    /// <summary>Whether there are rows.</summary>
    public bool HasRows => Rows.Count > 0;

    /// <summary>Whether there are columns to map.</summary>
    public bool HasSheetColumns => SheetColumns.Count > 0;

    /// <summary>Whether there are messages.</summary>
    public bool HasMessages => Messages.Count > 0;

    /// <summary>"135 items found, 120 ticked", or null before anything is read.</summary>
    public string? Summary => HasPlan
        ? _text.Format(nameof(Strings.Studio_Import_Summary), _text.Items(Rows.Count), _text.Items(Rows.Count(r => r.Include)))
        : null;

    /// <summary>Whether there is a summary.</summary>
    public bool HasSummary => HasPlan;

    /// <summary>What the last import did, or null.</summary>
    public string? Outcome { get; private set; }

    /// <summary>Whether there is an outcome to show.</summary>
    public bool HasOutcome => Outcome is not null;

    /// <summary>Whether anything is ticked to import.</summary>
    public bool CanApply => HasPlan && Rows.Any(r => r.Include);

    private bool HasPlan => _characterPlan is not null || _portraitPlan is not null || _sheetPlan is not null || _promotionPlan is not null;

    /// <summary>Opens the panel for an importer. Corrections are read at once; the rest wait for a source.</summary>
    public Task OpenAsync(StudioImportKind kind)
    {
        if (_session() is not { } session)
        {
            return Task.CompletedTask;
        }

        Close();
        Kind = kind;
        Source = kind == StudioImportKind.ModsFolder ? session.Current.Info.ModsDirectory : null;
        IsOpen = true;

        return kind == StudioImportKind.Corrections ? PlanAsync() : Task.CompletedTask;
    }

    /// <summary>Closes the panel and forgets the preview. The draft is not touched.</summary>
    [RelayCommand]
    public void Close()
    {
        IsOpen = false;
        ClearPlan();
        Messages.Clear();
        Outcome = null;
        NotifyAll();
    }

    /// <summary>Chooses a folder to import from, and reads it.</summary>
    [RelayCommand]
    public Task BrowseFolderAsync() =>
        _run(async ct =>
        {
            var title = _text[Kind == StudioImportKind.Assets
                ? nameof(Strings.Studio_Import_PickAssets)
                : nameof(Strings.Studio_Import_PickFolder)];

            if (await _picker.PickFolderAsync(title, Source, ct).ConfigureAwait(true) is { } folder)
            {
                Source = folder;
                await PlanAsync().ConfigureAwait(true);
            }
        });

    /// <summary>Chooses a file to import from — an archive or a spreadsheet — and reads it.</summary>
    [RelayCommand]
    public Task BrowseFileAsync() =>
        _run(async ct =>
        {
            var (title, filter) = Kind == StudioImportKind.Spreadsheet
                ? (_text[nameof(Strings.Studio_Import_PickSheet)],
                   new FileTypeFilter(_text[nameof(Strings.Studio_Import_Sheets)], SheetExtensions))
                : (_text[nameof(Strings.Studio_Import_PickAssets)],
                   new FileTypeFilter(_text[nameof(Strings.Studio_Import_Archives)], ArchiveExtensions));

            if (await _picker.PickFileAsync(title, [filter], null, ct).ConfigureAwait(true) is { } file)
            {
                Source = file;
                await PlanAsync().ConfigureAwait(true);
            }
        });

    /// <summary>Reads the source again and shows what importing it would do. Changes nothing.</summary>
    [RelayCommand]
    public Task PlanAsync()
    {
        if (_session() is not { } session || (NeedsSource && Source is not { Length: > 0 }))
        {
            return Task.CompletedTask;
        }

        var draft = session.Current;
        var source = Source ?? string.Empty;
        var kind = Kind;

        return _runBusy(
            _text[nameof(Strings.Studio_Import_Reading)],
            async ct =>
            {
                ClearPlan();
                Outcome = null;

                switch (kind)
                {
                    case StudioImportKind.Assets:
                        _characterPlan = await _importers.Assets.PlanAsync(draft, source, ct).ConfigureAwait(true);
                        break;
                    case StudioImportKind.ModsFolder:
                        _characterPlan = await _importers.ModsFolder.PlanAsync(draft, source, ct).ConfigureAwait(true);
                        break;
                    case StudioImportKind.Pictures:
                        _portraitPlan = await _importers.Pictures.PlanAsync(draft, source, ct).ConfigureAwait(true);
                        break;
                    case StudioImportKind.Spreadsheet:
                        _sheetPlan = await _importers.Spreadsheet.PlanAsync(draft, source, cancellationToken: ct).ConfigureAwait(true);
                        BuildColumns(draft);
                        break;
                    default:
                        _promotionPlan = await _importers.Corrections.PlanAsync(draft, ct).ConfigureAwait(true);
                        break;
                }

                ShowRows(draft);
            });
    }

    /// <summary>Ticks every row.</summary>
    [RelayCommand]
    public void SelectAll()
    {
        foreach (var row in Rows)
        {
            row.Include = true;
        }
    }

    /// <summary>Unticks every row.</summary>
    [RelayCommand]
    public void SelectNone()
    {
        foreach (var row in Rows)
        {
            row.Include = false;
        }
    }

    /// <summary>Adds the ticked rows to the draft, as one change that Undo takes back.</summary>
    [RelayCommand]
    public Task ApplyAsync()
    {
        if (_session() is not { } session || !CanApply)
        {
            return Task.CompletedTask;
        }

        var picks = Rows.ToList();

        return _runBusy(
            _text[nameof(Strings.Studio_Import_Applying)],
            async ct =>
            {
                var draft = session.Current;
                var now = _time.GetUtcNow();
                StudioImportOutcome outcome;

                if (_characterPlan is { } characters)
                {
                    List<CharacterImportChoice> choices =
                    [
                        .. characters.Rows.Select((row, i) => new CharacterImportChoice
                        {
                            SourcePath = row.SourcePath,
                            Include = picks[i].Include,
                            BaseCharacterId = picks[i].HasLink && !picks[i].KeepLink
                                ? EditField<string>.Cleared()
                                : EditField<string>.Unchanged,
                        }),
                    ];

                    outcome = Kind == StudioImportKind.Assets
                        ? _importers.Assets.Apply(draft, characters, choices, now)
                        : _importers.ModsFolder.Apply(draft, characters, choices, now);
                }
                else if (_portraitPlan is { } pictures)
                {
                    List<PortraitImportChoice> choices =
                    [
                        .. pictures.Rows.Select((row, i) => new PortraitImportChoice
                        {
                            SourceFile = row.SourceFile,
                            Include = picks[i].Include,
                            InternalName = picks[i].AssignTo is { } to
                                           && !string.Equals(to, row.InternalName, StringComparison.OrdinalIgnoreCase)
                                ? to
                                : null,
                        }),
                    ];

                    outcome = await _importers.Pictures.ApplyAsync(draft, pictures, choices, now, ct).ConfigureAwait(true);
                }
                else if (_sheetPlan is { } sheet)
                {
                    List<SpreadsheetImportChoice> choices =
                        [.. sheet.Rows.Select((row, i) => new SpreadsheetImportChoice(row.Line, picks[i].Include))];

                    outcome = await _importers.Spreadsheet.ApplyAsync(draft, sheet, choices, now, ct).ConfigureAwait(true);
                }
                else if (_promotionPlan is { } promotion)
                {
                    List<OverlayPromotionChoice> choices =
                        [.. promotion.Rows.Select((row, i) => new OverlayPromotionChoice(row.InternalName, picks[i].Include))];

                    outcome = await _importers.Corrections.ApplyAsync(draft, promotion, choices, now, ct).ConfigureAwait(true);
                }
                else
                {
                    return;
                }

                if (outcome.Changed)
                {
                    session.Apply(outcome.Draft);
                }

                ClearPlan();
                Messages.Clear();

                foreach (var skipped in outcome.Skipped)
                {
                    Messages.Add(skipped);
                }

                Outcome = _text.Format(
                    nameof(Strings.Studio_Import_Done),
                    _text.Characters(outcome.Created.Count),
                    _text.Characters(outcome.Updated.Count));

                NotifyAll();
            });
    }

    private static string? JoinNotes(IEnumerable<string?> notes)
    {
        var text = string.Join(" ", notes.Where(n => !string.IsNullOrWhiteSpace(n)));
        return text.Length == 0 ? null : text;
    }

    private void ShowRows(PackDraft draft)
    {
        ClearRows();
        Messages.Clear();

        if (_characterPlan is { } characters)
        {
            foreach (var row in characters.Rows)
            {
                AddRow(new StudioImportRowViewModel(
                    row.InternalName,
                    CharacterDetail(row),
                    row.SelectedByDefault,
                    link: row.Link.IsSkin ? LinkText(row.Link) : null,
                    notes: JoinNotes([row.Note, .. row.Diagnostics.Select(d => d.Message)])));
            }

            AddMessages(characters.Diagnostics.Select(d => d.Message));
        }
        else if (_portraitPlan is { } pictures)
        {
            IReadOnlyList<string> names = [.. draft.Variants.Select(v => v.InternalName).Order(StringComparer.OrdinalIgnoreCase)];

            foreach (var row in pictures.Rows)
            {
                var matched = row.Match != PortraitMatch.None && row.InternalName is not null;

                AddRow(new StudioImportRowViewModel(
                    row.FileName,
                    matched
                        ? _text.Format(nameof(Strings.Studio_Import_Picture_For), row.InternalName)
                        : _text[nameof(Strings.Studio_Import_Picture_Nobody)],
                    row.SelectedByDefault,
                    notes: row.Note,
                    assignTo: matched ? row.InternalName : null,
                    characters: names));
            }

            AddMessages(pictures.Diagnostics.Select(d => d.Message));
        }
        else if (_sheetPlan is { } sheet)
        {
            foreach (var row in sheet.Rows)
            {
                AddRow(new StudioImportRowViewModel(
                    row.InternalName ?? _text.Format(nameof(Strings.Studio_Import_Sheet_Row), row.Line),
                    row.Action switch
                    {
                        SpreadsheetRowAction.Create => _text[nameof(Strings.Studio_Import_NewCharacter)],
                        SpreadsheetRowAction.Update => _text.Format(nameof(Strings.Studio_Import_Changes), string.Join(", ", row.ChangedFields)),
                        SpreadsheetRowAction.Unchanged => _text[nameof(Strings.Studio_Import_Unchanged)],
                        _ => _text[nameof(Strings.Studio_Import_Invalid)],
                    },
                    row.SelectedByDefault,
                    notes: JoinNotes(row.Notes)));
            }

            AddMessages(sheet.Diagnostics.Select(d => d.Message));
        }
        else if (_promotionPlan is { } promotion)
        {
            foreach (var row in promotion.Rows)
            {
                AddRow(new StudioImportRowViewModel(
                    string.Equals(row.DisplayName, row.InternalName, StringComparison.Ordinal)
                        ? row.InternalName
                        : _text.Format(nameof(Strings.Studio_Import_NameWithId), row.DisplayName, row.InternalName),
                    row.Action == PromotionAction.Add
                        ? _text[nameof(Strings.Studio_Import_NewCharacter)]
                        : _text.Format(nameof(Strings.Studio_Import_Changes), string.Join(", ", row.Fields)),
                    row.SelectedByDefault,
                    notes: JoinNotes(row.Notes)));
            }

            if (promotion.Rows.Count == 0)
            {
                Messages.Add(_text[nameof(Strings.Studio_Import_NothingToBring)]);
            }

            AddMessages(promotion.Diagnostics.Select(d => d.Message));
        }

        NotifyAll();
    }

    private string CharacterDetail(CharacterImportRow row) => row.Action switch
    {
        // Distinct hashes, as the table counts them.
        HashImportAction.Create => _text.Format(
            nameof(Strings.Studio_Import_New),
            _text.Hashes(row.Hashes.Select(h => h.Hash).Distinct(StringComparer.OrdinalIgnoreCase).Count())),
        HashImportAction.AddHashes => _text.Format(nameof(Strings.Studio_Import_AddsHashes), _text.Hashes(row.NewHashCount)),
        HashImportAction.Unchanged => _text[nameof(Strings.Studio_Import_Unchanged)],
        _ => _text[nameof(Strings.Studio_Import_Conflict)],
    };

    private string LinkText(SkinLinkProposal link) => _text.Format(
        nameof(Strings.Studio_Import_Link),
        link.BaseCharacterId,
        _text[link.Confidence switch
        {
            LinkConfidence.High => nameof(Strings.Studio_Import_Confidence_High),
            LinkConfidence.Medium => nameof(Strings.Studio_Import_Confidence_Medium),
            _ => nameof(Strings.Studio_Import_Confidence_Low),
        }]);

    private void BuildColumns(PackDraft draft)
    {
        ClearColumns();

        if (_sheetPlan is not { } sheet)
        {
            return;
        }

        _buildingColumns = true;

        try
        {
            var options = FieldOptions(draft);

            foreach (var column in sheet.Columns)
            {
                var selected = options.Find(o =>
                                   o.Field == column.Field
                                   && string.Equals(o.AttributeId, column.AttributeId, StringComparison.OrdinalIgnoreCase))
                               ?? options[0];

                var mapped = new StudioSheetColumnViewModel(column.Index, column.Header, options, selected);
                mapped.PropertyChanged += OnColumnChanged;
                SheetColumns.Add(mapped);
            }
        }
        finally
        {
            _buildingColumns = false;
        }
    }

    private List<StudioSheetFieldOption> FieldOptions(PackDraft draft)
    {
        List<StudioSheetFieldOption> options =
        [
            new(_text[nameof(Strings.Studio_Sheet_Ignore)], SpreadsheetField.Ignore, null),
            new(_text[nameof(Strings.Studio_Sheet_DisplayName)], SpreadsheetField.DisplayName, null),
            new(_text[nameof(Strings.Studio_Sheet_InternalName)], SpreadsheetField.InternalName, null),
            new(_text[nameof(Strings.Studio_Sheet_Base)], SpreadsheetField.BaseCharacter, null),
            new(_text[nameof(Strings.Studio_Sheet_Default)], SpreadsheetField.IsDefault, null),
            new(_text[nameof(Strings.Studio_Sheet_Aliases)], SpreadsheetField.Aliases, null),
            new(_text[nameof(Strings.Studio_Sheet_ModFilesName)], SpreadsheetField.ModFilesName, null),
            new(_text[nameof(Strings.Studio_Sheet_ReleaseDate)], SpreadsheetField.ReleaseDate, null),
            new(_text[nameof(Strings.Studio_Sheet_Notes)], SpreadsheetField.Notes, null),
            new(_text[nameof(Strings.Studio_Sheet_Hidden)], SpreadsheetField.Hidden, null),
            new(_text[nameof(Strings.Studio_Sheet_Image)], SpreadsheetField.Image, null),
        ];

        foreach (var id in (draft.Game.Attributes?.Keys ?? []).Order(StringComparer.OrdinalIgnoreCase))
        {
            options.Add(new StudioSheetFieldOption(_text.Format(nameof(Strings.Studio_Sheet_Attribute), id), SpreadsheetField.Attribute, id));
        }

        return options;
    }

    private void OnColumnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_buildingColumns
            || e.PropertyName != nameof(StudioSheetColumnViewModel.Selected)
            || _sheetPlan is not { } sheet
            || _session() is not { } session
            || SheetColumns.Any(c => c.Selected is null))
        {
            return;
        }

        List<SpreadsheetColumn> mapping =
            [.. SheetColumns.Select(c => new SpreadsheetColumn(c.Index, c.Header, c.Selected!.Field, c.Selected.AttributeId))];

        _sheetPlan = SpreadsheetImporter.Build(session.Current, sheet.Source, sheet.Table, mapping);
        ShowRows(session.Current);
    }

    private void AddRow(StudioImportRowViewModel row)
    {
        row.PropertyChanged += OnRowChanged;
        Rows.Add(row);
    }

    private void AddMessages(IEnumerable<string> messages)
    {
        foreach (var message in messages)
        {
            Messages.Add(message);
        }
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StudioImportRowViewModel.Include))
        {
            OnPropertyChanged(nameof(CanApply));
            OnPropertyChanged(nameof(Summary));
        }
    }

    private void ClearRows()
    {
        foreach (var row in Rows)
        {
            row.PropertyChanged -= OnRowChanged;
        }

        Rows.Clear();
    }

    private void ClearColumns()
    {
        foreach (var column in SheetColumns)
        {
            column.PropertyChanged -= OnColumnChanged;
        }

        SheetColumns.Clear();
    }

    private void ClearPlan()
    {
        _characterPlan = null;
        _portraitPlan = null;
        _sheetPlan = null;
        _promotionPlan = null;
        ClearRows();
        ClearColumns();
    }

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(HasSheetColumns));
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasSummary));
        OnPropertyChanged(nameof(Outcome));
        OnPropertyChanged(nameof(HasOutcome));
        OnPropertyChanged(nameof(CanApply));
    }
}
