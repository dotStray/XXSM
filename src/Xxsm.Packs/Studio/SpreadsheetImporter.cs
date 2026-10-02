using System.Globalization;
using System.Text;
using Serilog;
using Xxsm.Core;
using Xxsm.Core.Diagnostics;
using Xxsm.Core.Io;
using Xxsm.Core.Text;
using Xxsm.Packs.Characters;
using Xxsm.Packs.Model;
using Xxsm.Packs.Sorting;

namespace Xxsm.Packs.Studio;

/// <summary>What a spreadsheet column is used for.</summary>
public enum SpreadsheetField
{
    /// <summary>Not used.</summary>
    Ignore = 0,

    /// <summary>The character's internal name.</summary>
    InternalName,

    /// <summary>The name shown in the grid.</summary>
    DisplayName,

    /// <summary>The character this is an outfit of, by internal or display name.</summary>
    BaseCharacter,

    /// <summary>Whether this is its family's default outfit.</summary>
    IsDefault,

    /// <summary>Other names, separated by semicolons or bars.</summary>
    Aliases,

    /// <summary>The folder mods are filed in.</summary>
    ModFilesName,

    /// <summary>The release date.</summary>
    ReleaseDate,

    /// <summary>Free-text notes.</summary>
    Notes,

    /// <summary>Whether it is hidden from the grid.</summary>
    Hidden,

    /// <summary>A portrait: a web address, a picture already in the pack, or a picture file on this computer.</summary>
    Image,

    /// <summary>One of the game's attributes, named by <see cref="SpreadsheetColumn.AttributeId"/>.</summary>
    Attribute,
}

/// <summary>What one column of a spreadsheet is used for.</summary>
/// <param name="Index">The 0-based column.</param>
/// <param name="Header">The column's header text.</param>
/// <param name="Field">What it fills in.</param>
/// <param name="AttributeId">For <see cref="SpreadsheetField.Attribute"/>, which attribute.</param>
public sealed record SpreadsheetColumn(int Index, string Header, SpreadsheetField Field, string? AttributeId = null);

/// <summary>What importing one spreadsheet row would do.</summary>
public enum SpreadsheetRowAction
{
    /// <summary>A character the draft does not have yet.</summary>
    Create,

    /// <summary>A character the draft has, with values that differ.</summary>
    Update,

    /// <summary>A character the draft has, already exactly as the row says.</summary>
    Unchanged,

    /// <summary>A row that cannot become a character; <see cref="SpreadsheetImportRow.Notes"/> says why.</summary>
    Invalid,
}

/// <summary>One row of a spreadsheet, and what importing it would do.</summary>
public sealed record SpreadsheetImportRow
{
    /// <summary>The line the row starts on in the file. The row's key.</summary>
    public required int Line { get; init; }

    /// <summary>The character it is about, or null when the row has no usable name.</summary>
    public string? InternalName { get; init; }

    /// <summary>What importing it would do.</summary>
    public required SpreadsheetRowAction Action { get; init; }

    /// <summary>The character as it would be after the import, or null for an invalid row.</summary>
    public PackVariant? Variant { get; init; }

    /// <summary>For an update, the fields that would change, by their pack names.</summary>
    public IReadOnlyList<string> ChangedFields { get; init; } = [];

    /// <summary>A picture file on this computer to copy into the pack as its portrait, or null.</summary>
    public string? ImageFile { get; init; }

    /// <summary>Whether the row starts ticked: rows that create or change a character.</summary>
    public required bool SelectedByDefault { get; init; }

    /// <summary>Sentences about the row: why it is invalid, or which cells were not understood.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>What importing a spreadsheet would do. Reading it changed nothing.</summary>
public sealed record SpreadsheetImportPlan
{
    /// <summary>The file that was read.</summary>
    public required string Source { get; init; }

    /// <summary>The table as read, so a different column mapping can be tried without reading again.</summary>
    public required DelimitedTable Table { get; init; }

    /// <summary>What each column is used for in this plan.</summary>
    public required IReadOnlyList<SpreadsheetColumn> Columns { get; init; }

    /// <summary>One row per spreadsheet row.</summary>
    public required IReadOnlyList<SpreadsheetImportRow> Rows { get; init; }

    /// <summary>Anything noticed about the file as a whole.</summary>
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }

    /// <summary>How many rows would create a character.</summary>
    public int CreateCount => Rows.Count(r => r.Action == SpreadsheetRowAction.Create);

    /// <summary>How many rows would change a character.</summary>
    public int UpdateCount => Rows.Count(r => r.Action == SpreadsheetRowAction.Update);

    /// <summary>How many rows cannot be used.</summary>
    public int InvalidCount => Rows.Count(r => r.Action == SpreadsheetRowAction.Invalid);
}

/// <summary>Whether to import one spreadsheet row.</summary>
/// <param name="Line">The row, by <see cref="SpreadsheetImportRow.Line"/>.</param>
/// <param name="Include">Whether to import it.</param>
public sealed record SpreadsheetImportChoice(int Line, bool Include);

/// <summary>Imports characters from a CSV or TSV file with a header row, mapped column by column.</summary>
public interface ISpreadsheetImporter
{
    /// <summary>Reads a spreadsheet and works out what importing it would do. Changes nothing.</summary>
    /// <param name="draft">The draft it would be imported into.</param>
    /// <param name="file">The CSV or TSV file.</param>
    /// <param name="columns">What each column is for, or null to have it suggested from the header.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="ModOperationException">The file does not exist, is too large to be a roster, or cannot be
    /// read.</exception>
    Task<SpreadsheetImportPlan> PlanAsync(
        PackDraft draft,
        string file,
        IReadOnlyList<SpreadsheetColumn>? columns = null,
        CancellationToken cancellationToken = default);

    /// <summary>Applies the chosen rows, copying any portrait files into the draft's folder.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="plan">The preview.</param>
    /// <param name="choices">Which rows to import; a row with no choice takes its default.</param>
    /// <param name="now">When, for the draft's history.</param>
    /// <param name="cancellationToken">Cancels between rows.</param>
    /// <returns>The new draft and what changed; a portrait that cannot be copied is named as skipped.</returns>
    Task<StudioImportOutcome> ApplyAsync(
        PackDraft draft,
        SpreadsheetImportPlan plan,
        IReadOnlyList<SpreadsheetImportChoice>? choices,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

/// <summary>The default <see cref="ISpreadsheetImporter"/>. A blank cell leaves a value as it is.</summary>
public sealed class SpreadsheetImporter(IStudioDraftStore store, ILogger logger) : ISpreadsheetImporter
{
    /// <summary>The largest file read. A roster of thousands of characters is well under this.</summary>
    public const long MaxFileBytes = 16L * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly IStudioDraftStore _store = store;
    private readonly ILogger _logger = logger.ForContext<SpreadsheetImporter>();

    /// <inheritdoc />
    public async Task<SpreadsheetImportPlan> PlanAsync(
        PackDraft draft,
        string file,
        IReadOnlyList<SpreadsheetColumn>? columns = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(file);

        var resolved = PathComparer.TryResolveExisting(file, out var found)
            ? found
            : PathComparer.Normalize(Path.GetFullPath(file));

        if (!File.Exists(resolved))
        {
            throw new ModOperationException($"There is no file at '{PathDisplay.Show(resolved)}'.", resolved);
        }

        string text;

        try
        {
            if (new FileInfo(resolved).Length > MaxFileBytes)
            {
                throw new ModOperationException(
                    $"'{PathDisplay.Show(resolved)}' is too large to be a list of characters.", resolved);
            }

            var bytes = await File.ReadAllBytesAsync(resolved, cancellationToken).ConfigureAwait(false);
            text = Decode(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException($"Could not read '{PathDisplay.Show(resolved)}': {ex.Message}", resolved, ex);
        }

        var table = DelimitedText.Parse(text);
        var plan = Build(draft, resolved, table, columns ?? SuggestColumns(table.Header, draft.Game));

        _logger.Information(
            "Planned a spreadsheet import from {Source}: {Rows} rows, {Create} to create, {Update} to update, {Invalid} invalid",
            resolved,
            plan.Rows.Count,
            plan.CreateCount,
            plan.UpdateCount,
            plan.InvalidCount);

        return plan;
    }

    /// <inheritdoc />
    public async Task<StudioImportOutcome> ApplyAsync(
        PackDraft draft,
        SpreadsheetImportPlan plan,
        IReadOnlyList<SpreadsheetImportChoice>? choices,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(plan);

        var decided = (choices ?? []).GroupBy(c => c.Line).ToDictionary(g => g.Key, g => g.Last().Include);
        var variants = draft.Variants.ToList();
        var created = new List<string>();
        var updated = new List<string>();
        var skipped = new List<string>();

        foreach (var row in plan.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!(decided.TryGetValue(row.Line, out var include) ? include : row.SelectedByDefault))
            {
                continue;
            }

            if (row.Variant is null || row.Action is SpreadsheetRowAction.Invalid)
            {
                skipped.Add($"Row {Number(row.Line)}: {string.Join(" ", row.Notes)}");
                continue;
            }

            var variant = row.Variant;

            if (row.ImageFile is not null)
            {
                try
                {
                    var image = await _store.StoreImageAsync(draft.GameId, variant.InternalName, row.ImageFile, cancellationToken)
                        .ConfigureAwait(false);
                    variant = variant with { Image = image };
                }
                catch (ModOperationException ex)
                {
                    skipped.Add($"Row {Number(row.Line)}: the portrait was not added. {ex.Message}");
                }
            }

            var index = variants.FindIndex(v => string.Equals(v.InternalName, variant.InternalName, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                variants.Add(variant);
                created.Add(variant.InternalName);
            }
            else
            {
                variants[index] = variant;
                updated.Add(variant.InternalName);
            }
        }

        _logger.Information(
            "Applied a spreadsheet import from {Source}: created {Created}, updated {Updated}, skipped {Skipped}",
            plan.Source,
            created.Count,
            updated.Count,
            skipped.Count);

        if (created.Count + updated.Count == 0)
        {
            return new StudioImportOutcome(draft, [], [], skipped);
        }

        var record = new StudioImportRecord
        {
            Kind = StudioImportKinds.Spreadsheet,
            Source = plan.Source,
            ImportedAt = now,
            Variants = [.. created, .. updated],
        };

        return new StudioImportOutcome(
            draft with { Variants = variants, Info = draft.Info with { Imports = [.. draft.Info.Imports ?? [], record] } },
            created,
            updated,
            skipped);
    }

    /// <summary>Suggests each column's use from its header, compared as names are, and the game's attributes.</summary>
    /// <param name="header">The header row.</param>
    /// <param name="game">The game, whose attributes a column may be named after.</param>
    /// <returns>One mapping per column; anything not recognised is <see cref="SpreadsheetField.Ignore"/>.</returns>
    public static IReadOnlyList<SpreadsheetColumn> SuggestColumns(IReadOnlyList<string> header, GameDefinition game)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(game);

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (id, definition) in game.Attributes ?? new Dictionary<string, AttributeDefinition>())
        {
            attributes.TryAdd(SortName.Normalize(id), id);

            if (!string.IsNullOrWhiteSpace(definition.DisplayName))
            {
                attributes.TryAdd(SortName.Normalize(definition.DisplayName), id);
            }
        }

        var taken = new HashSet<SpreadsheetField>();
        var takenAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var columns = new List<SpreadsheetColumn>(header.Count);

        for (var index = 0; index < header.Count; index++)
        {
            var key = SortName.Normalize(header[index]);

            if (attributes.TryGetValue(key, out var attributeId) && takenAttributes.Add(attributeId))
            {
                columns.Add(new SpreadsheetColumn(index, header[index], SpreadsheetField.Attribute, attributeId));
                continue;
            }

            var field = HeaderWords.GetValueOrDefault(key);

            columns.Add(field != SpreadsheetField.Ignore && taken.Add(field)
                ? new SpreadsheetColumn(index, header[index], field)
                : new SpreadsheetColumn(index, header[index], SpreadsheetField.Ignore));
        }

        return columns;
    }

    /// <summary>Works out what importing a table would do with a given column mapping. Pure.</summary>
    /// <param name="draft">The draft it would be imported into.</param>
    /// <param name="source">The file the table came from; relative portrait paths are found beside it.</param>
    /// <param name="table">The table.</param>
    /// <param name="columns">What each column is for.</param>
    public static SpreadsheetImportPlan Build(
        PackDraft draft,
        string source,
        DelimitedTable table,
        IReadOnlyList<SpreadsheetColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(columns);

        var diagnostics = new List<Diagnostic>();

        if (table.UnterminatedQuote)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                StudioImportCodes.UnreadableFile,
                "A cell starts with a quote mark that is never closed, so everything after it was read as one cell. " +
                "Check the file for a stray quote."));
        }

        if (table.Header.Count == 0)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning, StudioImportCodes.NothingFound, $"'{PathDisplay.Show(source)}' is empty."));
        }

        var mapped = columns.Where(c => c.Field != SpreadsheetField.Ignore).ToList();

        if (table.Header.Count > 0
            && !mapped.Exists(c => c.Field is SpreadsheetField.InternalName or SpreadsheetField.DisplayName))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                StudioImportCodes.NothingFound,
                "No column is used for a character's name or internal name, so no row can say which character " +
                "it is. Choose which column holds the names."));
        }

        var folder = Path.GetDirectoryName(source) ?? string.Empty;
        var existing = CharacterImports.VariantsById(draft);
        var byDisplayName = existing.Values
            .Where(v => !string.IsNullOrWhiteSpace(v.DisplayName))
            .GroupBy(v => v.DisplayName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var sheetNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in table.Rows)
        {
            var (id, display) = Identify(row, mapped, existing, byDisplayName);
            if (id is not null && PackDrafts.IsValidId(id))
            {
                sheetNames.TryAdd(id, id);
                if (display is not null)
                {
                    sheetNames.TryAdd(display, id);
                }
            }
        }

        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<SpreadsheetImportRow>(table.Rows.Count);

        foreach (var row in table.Rows)
        {
            rows.Add(BuildRow(row, mapped, draft.Game, folder, existing, byDisplayName, sheetNames, seen));
        }

        return new SpreadsheetImportPlan
        {
            Source = source,
            Table = table,
            Columns = columns,
            Rows = rows,
            Diagnostics = diagnostics,
        };
    }

    private static SpreadsheetImportRow BuildRow(
        DelimitedRow row,
        List<SpreadsheetColumn> mapped,
        GameDefinition game,
        string folder,
        Dictionary<string, PackVariant> existing,
        Dictionary<string, PackVariant> byDisplayName,
        Dictionary<string, string> sheetNames,
        Dictionary<string, int> seen)
    {
        var notes = new List<string>();
        var (id, display) = Identify(row, mapped, existing, byDisplayName);

        if (id is null)
        {
            return Invalid(row.Line, null, "It has no name, so there is no telling which character it is.");
        }

        if (!PackDrafts.IsValidId(id))
        {
            return Invalid(
                row.Line,
                null,
                $"'{id}' cannot be an internal name — only letters, digits, underscores and hyphens are allowed.");
        }

        if (seen.TryGetValue(id, out var firstLine))
        {
            return Invalid(row.Line, id, $"It is the same character as row {Number(firstLine)}.");
        }

        seen[id] = row.Line;

        var before = existing.GetValueOrDefault(id);
        var variant = before ?? new PackVariant
        {
            InternalName = id,
            DisplayName = display ?? id,
            ModFilesName = id,
            IsDefaultVariant = true,
        };

        string? imageFile = null;
        var setsDefault = false;
        var setsBase = false;

        foreach (var column in mapped)
        {
            var value = row.Cell(column.Index).Trim();

            if (value.Length == 0)
            {
                continue;
            }

            switch (column.Field)
            {
                case SpreadsheetField.DisplayName:
                    variant = variant with { DisplayName = value };
                    break;

                case SpreadsheetField.BaseCharacter:
                    if (sheetNames.TryGetValue(value, out var baseFromSheet)
                        || (existing.TryGetValue(value, out var baseVariant) && (baseFromSheet = baseVariant.InternalName) is not null)
                        || (byDisplayName.TryGetValue(value, out baseVariant) && (baseFromSheet = baseVariant.InternalName) is not null))
                    {
                        if (!string.Equals(baseFromSheet, id, StringComparison.OrdinalIgnoreCase))
                        {
                            variant = variant with { BaseCharacterId = baseFromSheet };
                            setsBase = true;
                        }
                    }
                    else
                    {
                        notes.Add($"{column.Header} says '{value}', which is not a character in the pack or the sheet.");
                        variant = variant with { BaseCharacterId = value };
                        setsBase = true;
                    }

                    break;

                case SpreadsheetField.IsDefault:
                    if (ParseYesNo(value) is { } isDefault)
                    {
                        variant = variant with { IsDefaultVariant = isDefault };
                        setsDefault = true;
                    }
                    else
                    {
                        notes.Add($"'{value}' in {column.Header} is not yes or no, so it was left as it is.");
                    }

                    break;

                case SpreadsheetField.Hidden:
                    if (ParseYesNo(value) is { } hidden)
                    {
                        variant = variant with { Hidden = hidden };
                    }
                    else
                    {
                        notes.Add($"'{value}' in {column.Header} is not yes or no, so it was left as it is.");
                    }

                    break;

                case SpreadsheetField.Aliases:
                    variant = variant with { Aliases = SplitList(value) };
                    break;

                case SpreadsheetField.ModFilesName:
                    variant = variant with { ModFilesName = value };
                    break;

                case SpreadsheetField.ReleaseDate:
                    variant = variant with { ReleaseDate = value };
                    break;

                case SpreadsheetField.Notes:
                    variant = variant with { Notes = value };
                    break;

                case SpreadsheetField.Image:
                    var (image, file, note) = ResolveImage(value, folder);
                    if (note is not null)
                    {
                        notes.Add(note);
                    }

                    if (image is not null)
                    {
                        variant = variant with { Image = image };
                    }

                    imageFile = file ?? imageFile;
                    break;

                case SpreadsheetField.Attribute when column.AttributeId is not null:
                    var (attribute, attributeNote) = ParseAttribute(value, column, game);
                    if (attributeNote is not null)
                    {
                        notes.Add(attributeNote);
                    }

                    if (attribute is not null)
                    {
                        var values = new Dictionary<string, AttributeValue>(
                            variant.Attributes ?? new Dictionary<string, AttributeValue>(), StringComparer.OrdinalIgnoreCase)
                        {
                            [column.AttributeId] = attribute,
                        };
                        variant = variant with { Attributes = values };
                    }

                    break;
            }
        }

        if (setsBase && !setsDefault)
        {
            variant = variant with { IsDefaultVariant = variant.BaseCharacterId is null };
        }

        var changed = before is null ? [] : ChangedFields(before, variant);
        var action = before is null
            ? SpreadsheetRowAction.Create
            : changed.Count > 0 || imageFile is not null ? SpreadsheetRowAction.Update : SpreadsheetRowAction.Unchanged;

        return new SpreadsheetImportRow
        {
            Line = row.Line,
            InternalName = variant.InternalName,
            Action = action,
            Variant = variant,
            ChangedFields = changed,
            ImageFile = imageFile,
            SelectedByDefault = action is SpreadsheetRowAction.Create or SpreadsheetRowAction.Update,
            Notes = notes,
        };
    }

    /// <summary>Which character a row is about: by internal name, else display name, else a new id.</summary>
    private static (string? Id, string? Display) Identify(
        DelimitedRow row,
        List<SpreadsheetColumn> mapped,
        Dictionary<string, PackVariant> existing,
        Dictionary<string, PackVariant> byDisplayName)
    {
        string? Value(SpreadsheetField field) =>
            mapped.Find(c => c.Field == field) is { } column && row.Cell(column.Index).Trim() is { Length: > 0 } text
                ? text
                : null;

        var id = Value(SpreadsheetField.InternalName);
        var display = Value(SpreadsheetField.DisplayName);

        if (id is not null)
        {
            return (existing.TryGetValue(id, out var byId) ? byId.InternalName : id, display);
        }

        if (display is null)
        {
            return (null, null);
        }

        if (byDisplayName.TryGetValue(display, out var named))
        {
            return (named.InternalName, display);
        }

        var slug = CharacterNames.Slugify(display);
        return (existing.TryGetValue(slug, out var bySlug) ? bySlug.InternalName : slug, display);
    }

    private static (string? Image, string? File, string? Note) ResolveImage(string value, string folder)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            return (value, null, null);
        }

        var candidate = Path.IsPathRooted(value) ? value : Path.Combine(folder, value);

        if (PathComparer.TryResolveExisting(candidate, out var found) && File.Exists(found))
        {
            return StudioDraftStore.ImageExtensions.Contains(Path.GetExtension(found).ToLowerInvariant(), StringComparer.Ordinal)
                ? (null, found, null)
                : (null, null, $"'{value}' is not a PNG, JPEG or WebP picture, so the portrait was left as it is.");
        }

        if (value.StartsWith(PackSchema.ImagesDirectory + "/", StringComparison.OrdinalIgnoreCase))
        {
            return (value, null, null);
        }

        return (null, null, $"The picture '{value}' was not found, so the portrait was left as it is.");
    }

    private static (AttributeValue? Value, string? Note) ParseAttribute(string value, SpreadsheetColumn column, GameDefinition game)
    {
        var definition = (game.Attributes ?? new Dictionary<string, AttributeDefinition>())
            .FirstOrDefault(a => string.Equals(a.Key, column.AttributeId, StringComparison.OrdinalIgnoreCase))
            .Value;

        if (definition?.IsNumeric == true)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? (AttributeValue.FromNumber(number), null)
                : (null, $"'{value}' in {column.Header} is not a number, so it was left as it is.");
        }

        var ids = SplitList(value)
            .Select(v => (definition?.Values ?? [])
                .FirstOrDefault(d => string.Equals(d.Id, v, StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(d.DisplayName, v, StringComparison.OrdinalIgnoreCase))
                ?.Id ?? v)
            .ToList();

        return ids.Count switch
        {
            0 => (null, null),
            1 => (AttributeValue.FromString(ids[0]), null),
            _ => (AttributeValue.FromArray(ids), null),
        };
    }

    private static List<string> SplitList(string value) =>
        [.. value.Split([';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static bool? ParseYesNo(string value) =>
        value.ToUpperInvariant() switch
        {
            "YES" or "Y" or "TRUE" or "1" or "X" => true,
            "NO" or "N" or "FALSE" or "0" => false,
            _ => null,
        };

    private static List<string> ChangedFields(PackVariant before, PackVariant after)
    {
        var changed = new List<string>();

        void Check(string name, bool same)
        {
            if (!same)
            {
                changed.Add(name);
            }
        }

        Check(OverlayFields.DisplayName, before.DisplayName == after.DisplayName);
        Check(OverlayFields.BaseCharacterId, string.Equals(before.BaseCharacterId, after.BaseCharacterId, StringComparison.OrdinalIgnoreCase));
        Check(OverlayFields.IsDefaultVariant, before.IsDefaultVariant == after.IsDefaultVariant);
        Check(OverlayFields.Aliases, (before.Aliases ?? []).SequenceEqual(after.Aliases ?? []));
        Check(OverlayFields.ModFilesName, before.ModFilesName == after.ModFilesName);
        Check(OverlayFields.ReleaseDate, before.ReleaseDate == after.ReleaseDate);
        Check(OverlayFields.Notes, before.Notes == after.Notes);
        Check(OverlayFields.Hidden, before.Hidden == after.Hidden);
        Check(OverlayFields.Image, before.Image == after.Image);
        Check(OverlayFields.Attributes, SameAttributes(before.Attributes, after.Attributes));

        return changed;
    }

    private static bool SameAttributes(
        IReadOnlyDictionary<string, AttributeValue>? one,
        IReadOnlyDictionary<string, AttributeValue>? two)
    {
        one ??= new Dictionary<string, AttributeValue>();
        two ??= new Dictionary<string, AttributeValue>();

        return one.Count == two.Count
               && one.All(pair => two.TryGetValue(pair.Key, out var other)
                                  && pair.Value.Ids.SequenceEqual(other.Ids, StringComparer.OrdinalIgnoreCase));
    }

    private static SpreadsheetImportRow Invalid(int line, string? id, string reason) => new()
    {
        Line = line,
        InternalName = id,
        Action = SpreadsheetRowAction.Invalid,
        SelectedByDefault = false,
        Notes = [reason],
    };

    /// <summary>Reads the file as UTF-8, or as Latin-1 when it is not, as older spreadsheet programs save.</summary>
    private static string Decode(byte[] bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static string Number(int line) => line.ToString(CultureInfo.InvariantCulture);

    /// <summary>Header words each built-in field answers to, in normalised form.</summary>
    private static readonly Dictionary<string, SpreadsheetField> HeaderWords = BuildHeaderWords();

    private static Dictionary<string, SpreadsheetField> BuildHeaderWords()
    {
        var words = new (SpreadsheetField Field, string[] Headers)[]
        {
            (SpreadsheetField.InternalName, ["internal name", "internalName", "id", "key"]),
            (SpreadsheetField.DisplayName, ["name", "display name", "displayName", "character"]),
            (SpreadsheetField.BaseCharacter, ["base", "base character", "baseCharacterId", "outfit of", "skin of"]),
            (SpreadsheetField.IsDefault, ["default", "is default", "isDefaultVariant"]),
            (SpreadsheetField.Aliases, ["aliases", "alias", "other names"]),
            (SpreadsheetField.ModFilesName, ["mod files name", "modFilesName", "folder", "mod folder"]),
            (SpreadsheetField.ReleaseDate, ["release date", "releaseDate", "release", "released"]),
            (SpreadsheetField.Notes, ["notes", "note"]),
            (SpreadsheetField.Hidden, ["hidden", "hide"]),
            (SpreadsheetField.Image, ["image", "portrait", "picture", "icon"]),
        };

        var map = new Dictionary<string, SpreadsheetField>(StringComparer.Ordinal);

        foreach (var (field, headers) in words)
        {
            foreach (var header in headers)
            {
                map.TryAdd(SortName.Normalize(header), field);
            }
        }

        return map;
    }
}
