using Serilog;
using Xxsm.Core;
using Xxsm.Core.Diagnostics;
using Xxsm.Core.Io;
using Xxsm.Packs.Model;
using Xxsm.Packs.Sorting;

namespace Xxsm.Packs.Studio;

/// <summary>How a picture's file name was matched to a character.</summary>
public enum PortraitMatch
{
    /// <summary>Nothing matched, or more than one character did. Listed for a person to assign.</summary>
    None = 0,

    /// <summary>A character's name was found inside a longer file name.</summary>
    Partial = 1,

    /// <summary>The file name is a character's name, alias or folder name, compared as names match.</summary>
    Name = 2,

    /// <summary>The file name is a character's internal name.</summary>
    Exact = 3,
}

/// <summary>One picture found, and the character it would become the portrait of.</summary>
public sealed record PortraitImportRow
{
    /// <summary>The picture's absolute path. The row's key.</summary>
    public required string SourceFile { get; init; }

    /// <summary>The picture's file name.</summary>
    public required string FileName { get; init; }

    /// <summary>The file's size, so a large picture can be pointed out before it goes into a pack.</summary>
    public long Bytes { get; init; }

    /// <summary>The character it matched, or null.</summary>
    public string? InternalName { get; init; }

    /// <summary>How it matched.</summary>
    public required PortraitMatch Match { get; init; }

    /// <summary>The portrait that character has now, when it has one and this would replace it.</summary>
    public string? CurrentImage { get; init; }

    /// <summary>Whether the row starts ticked: a picture matched to a character with no portrait yet.</summary>
    public required bool SelectedByDefault { get; init; }

    /// <summary>A sentence about the row worth showing beside it, or null.</summary>
    public string? Note { get; init; }
}

/// <summary>What importing a folder of pictures would do. Reading it changed nothing.</summary>
public sealed record PortraitImportPlan
{
    /// <summary>The folder that was read.</summary>
    public required string Source { get; init; }

    /// <summary>One row per picture, in file-name order.</summary>
    public required IReadOnlyList<PortraitImportRow> Rows { get; init; }

    /// <summary>Anything noticed about the folder as a whole.</summary>
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }

    /// <summary>The pictures nothing matched, for a person to assign by hand.</summary>
    public IEnumerable<PortraitImportRow> Unmatched => Rows.Where(r => r.Match == PortraitMatch.None);
}

/// <summary>What a person decided about one picture.</summary>
public sealed record PortraitImportChoice
{
    /// <summary>The row, by <see cref="PortraitImportRow.SourceFile"/>.</summary>
    public required string SourceFile { get; init; }

    /// <summary>Whether to use it.</summary>
    public required bool Include { get; init; }

    /// <summary>The character to give it to, or null to keep the match.</summary>
    public string? InternalName { get; init; }
}

/// <summary>Attaches portraits in bulk from a folder of pictures, matched to characters by file name.</summary>
public interface IPortraitFolderImporter
{
    /// <summary>Matches every picture directly inside a folder to a character. Changes nothing.</summary>
    /// <param name="draft">The draft whose characters the pictures are for.</param>
    /// <param name="folder">The folder of pictures. Only its own files are read, not its subfolders.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="ModOperationException">The folder does not exist or cannot be listed.</exception>
    Task<PortraitImportPlan> PlanAsync(PackDraft draft, string folder, CancellationToken cancellationToken = default);

    /// <summary>Copies the chosen pictures into the draft's folder and points each character at its own.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="plan">The preview.</param>
    /// <param name="choices">What was decided per picture; a row with no choice takes its defaults.</param>
    /// <param name="now">When, for the draft's history.</param>
    /// <param name="cancellationToken">Cancels between pictures. Pictures already copied stay copied.</param>
    /// <returns>The new draft, for the caller to save, and what changed; a picture that fails is
    /// skipped.</returns>
    Task<StudioImportOutcome> ApplyAsync(
        PackDraft draft,
        PortraitImportPlan plan,
        IReadOnlyList<PortraitImportChoice>? choices,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

/// <summary>The default <see cref="IPortraitFolderImporter"/>, matching as the sorter's name match does.</summary>
public sealed class PortraitFolderImporter(IStudioDraftStore store, ILogger logger) : IPortraitFolderImporter
{
    private readonly IStudioDraftStore _store = store;
    private readonly ILogger _logger = logger.ForContext<PortraitFolderImporter>();

    /// <inheritdoc />
    public Task<PortraitImportPlan> PlanAsync(PackDraft draft, string folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        var resolved = PathComparer.TryResolveExisting(folder, out var found)
            ? found
            : PathComparer.Normalize(Path.GetFullPath(folder));

        if (!Directory.Exists(resolved))
        {
            throw new ModOperationException($"There is no folder at '{PathDisplay.Show(resolved)}'.", resolved);
        }

        List<string> files;

        try
        {
            files = [.. Directory.EnumerateFiles(resolved).Order(StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException($"Could not look inside '{PathDisplay.Show(resolved)}': {ex.Message}", resolved, ex);
        }

        var diagnostics = new List<Diagnostic>();
        var pictures = files.Where(IsPicture).ToList();
        var others = files.Count - pictures.Count;

        if (others > 0)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Info,
                StudioImportCodes.NothingFound,
                others == 1
                    ? "1 file in the folder is not a PNG, JPEG or WebP picture and was left out."
                    : $"{others} files in the folder are not PNG, JPEG or WebP pictures and were left out."));
        }

        if (pictures.Count == 0)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                StudioImportCodes.NothingFound,
                $"There are no PNG, JPEG or WebP pictures directly inside '{PathDisplay.Show(resolved)}'."));
        }

        var keys = Keys(draft);
        var variants = CharacterImports.VariantsById(draft);
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<PortraitImportRow>(pictures.Count);

        foreach (var path in pictures)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(path);
            var (match, id, note) = MatchOf(Path.GetFileNameWithoutExtension(path), variants, keys);
            string? current = null;
            var selected = match != PortraitMatch.None;

            if (id is not null)
            {
                current = Blank(variants[id].Image);

                if (claimed.TryGetValue(id, out var earlier))
                {
                    selected = false;
                    note = $"'{earlier}' also matches {id}, and only one picture can be its portrait.";
                }
                else
                {
                    claimed[id] = name;

                    if (current is not null)
                    {
                        selected = false;
                        note ??= $"{id} already has a portrait. Tick this to replace it.";
                    }
                }
            }

            rows.Add(new PortraitImportRow
            {
                SourceFile = PathComparer.Normalize(path),
                FileName = name,
                Bytes = SizeOf(path),
                InternalName = id,
                Match = match,
                CurrentImage = current,
                SelectedByDefault = selected,
                Note = note,
            });
        }

        _logger.Information(
            "Planned a portrait import from {Source}: {Pictures} pictures, {Matched} matched, {Unmatched} for a person to assign",
            resolved,
            rows.Count,
            rows.Count(r => r.Match != PortraitMatch.None),
            rows.Count(r => r.Match == PortraitMatch.None));

        return Task.FromResult(new PortraitImportPlan { Source = resolved, Rows = rows, Diagnostics = diagnostics });
    }

    /// <inheritdoc />
    public async Task<StudioImportOutcome> ApplyAsync(
        PackDraft draft,
        PortraitImportPlan plan,
        IReadOnlyList<PortraitImportChoice>? choices,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(plan);

        var decided = new Dictionary<string, PortraitImportChoice>(StringComparer.Ordinal);
        foreach (var choice in choices ?? [])
        {
            decided[choice.SourceFile] = choice;
        }

        var variants = draft.Variants.ToList();
        var given = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var updated = new List<string>();
        var skipped = new List<string>();

        foreach (var row in plan.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var choice = decided.GetValueOrDefault(row.SourceFile);

            if (!(choice?.Include ?? row.SelectedByDefault))
            {
                continue;
            }

            var id = Blank(choice?.InternalName) ?? row.InternalName;

            if (id is null)
            {
                skipped.Add($"{row.FileName}: it was not matched to anyone, so choose who it is for.");
                continue;
            }

            var index = variants.FindIndex(v => string.Equals(v.InternalName, id, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                skipped.Add($"{row.FileName}: there is no character called '{id}'.");
                continue;
            }

            var variant = variants[index];

            if (given.TryGetValue(variant.InternalName, out var earlier))
            {
                skipped.Add($"{row.FileName}: {variant.InternalName} already got '{earlier}' in this import.");
                continue;
            }

            string image;

            try
            {
                image = await _store.StoreImageAsync(draft.GameId, variant.InternalName, row.SourceFile, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ModOperationException ex)
            {
                skipped.Add($"{row.FileName}: {ex.Message}");
                continue;
            }

            variants[index] = variant with { Image = image };
            given[variant.InternalName] = row.FileName;
            updated.Add(variant.InternalName);
        }

        _logger.Information(
            "Applied a portrait import from {Source}: {Updated} portraits set, {Skipped} skipped",
            plan.Source,
            updated.Count,
            skipped.Count);

        if (updated.Count == 0)
        {
            return new StudioImportOutcome(draft, [], [], skipped);
        }

        var record = new StudioImportRecord
        {
            Kind = StudioImportKinds.Images,
            Source = plan.Source,
            ImportedAt = now,
            Variants = updated,
        };

        return new StudioImportOutcome(
            draft with { Variants = variants, Info = draft.Info with { Imports = [.. draft.Info.Imports ?? [], record] } },
            [],
            updated,
            skipped);
    }

    private static bool IsPicture(string path) =>
        StudioDraftStore.ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant(), StringComparer.Ordinal);

    /// <summary>Every comparison key a character answers to, and who answers to it.</summary>
    private static Dictionary<string, HashSet<string>> Keys(PackDraft draft)
    {
        var keys = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var variant in CharacterImports.VariantsById(draft).Values)
        {
            IEnumerable<string?> names =
            [
                variant.InternalName,
                variant.DisplayName,
                variant.ModFilesName,
                .. variant.Aliases ?? [],
            ];

            foreach (var key in names.Select(SortName.Normalize).Where(k => k.Length > 0))
            {
                if (!keys.TryGetValue(key, out var owners))
                {
                    owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    keys[key] = owners;
                }

                owners.Add(variant.InternalName);
            }
        }

        return keys;
    }

    private static (PortraitMatch Match, string? Id, string? Note) MatchOf(
        string stem,
        Dictionary<string, PackVariant> variants,
        Dictionary<string, HashSet<string>> keys)
    {
        if (variants.TryGetValue(stem, out var exact))
        {
            return (PortraitMatch.Exact, exact.InternalName, null);
        }

        var normalized = SortName.Normalize(stem);

        if (normalized.Length == 0)
        {
            return (PortraitMatch.None, null, null);
        }

        if (keys.TryGetValue(normalized, out var owners))
        {
            return owners.Count == 1
                ? (PortraitMatch.Name, owners.First(), null)
                : (PortraitMatch.None, null, $"Its name could be {string.Join(" or ", owners.Order(StringComparer.OrdinalIgnoreCase))}.");
        }

        var longest = keys
            .Where(k => k.Key.Length >= SortName.MinimumTokenLength
                        && normalized.Contains(k.Key, StringComparison.Ordinal))
            .GroupBy(k => k.Key.Length)
            .OrderByDescending(g => g.Key)
            .FirstOrDefault();

        if (longest is null)
        {
            return (PortraitMatch.None, null, null);
        }

        var candidates = longest.SelectMany(k => k.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return candidates.Count == 1
            ? (PortraitMatch.Partial, candidates[0], $"Only part of its name matched {candidates[0]}.")
            : (PortraitMatch.None, null, $"Its name could be {string.Join(" or ", candidates.Order(StringComparer.OrdinalIgnoreCase))}.");
    }

    private static long SizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
