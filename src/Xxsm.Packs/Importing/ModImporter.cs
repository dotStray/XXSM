using Serilog;
using Xxsm.Core;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Packs.Merge;
using Xxsm.Packs.Sorting;

namespace Xxsm.Packs.Importing;

/// <summary>Brings in what JASM or XX-Mod-Manager knew about a Mods folder's mods.</summary>
/// <remarks>Writes only each mod's <c>.xxsm/mod.json</c>, and only fills fields XXSM has nothing in.</remarks>
public interface IModImporter
{
    /// <summary>Finds every mod with another manager's details and works out what bringing them in changes.</summary>
    /// <param name="modsDirectory">The Mods folder, searched a few levels deep.</param>
    /// <param name="data">The merged pack and overlay, which says what each character tag means.</param>
    /// <param name="characterChoices">The user's answers for tags that name no known character: tag to internal
    /// name, ignoring case.</param>
    /// <param name="compareHashes">Whether to read each tagged mod's hashes to find disagreements. Slower.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <exception cref="ModOperationException">The Mods folder does not exist.</exception>
    Task<ModImportPlan> PlanAsync(
        string modsDirectory,
        GameData data,
        IReadOnlyDictionary<string, string>? characterChoices = null,
        bool compareHashes = true,
        CancellationToken cancellationToken = default);

    /// <summary>Writes the details the rows bring in.</summary>
    /// <param name="rows">Rows from a plan. A row with nothing to fill, or a problem, is counted and skipped.</param>
    /// <param name="cancellationToken">Cancels between mods; what was written stays written.</param>
    Task<ModImportResult> ApplyAsync(IReadOnlyList<ModImportRow> rows, CancellationToken cancellationToken = default);
}

/// <summary>The default <see cref="IModImporter"/>.</summary>
public sealed class ModImporter(
    IModConfigStore configs,
    IModSignalExtractor signals,
    IModSorter sorter,
    ILogger logger) : IModImporter
{
    private const int MaximumDepth = 4;

    private readonly IModConfigStore _configs = configs;
    private readonly IModSignalExtractor _signals = signals;
    private readonly IModSorter _sorter = sorter;
    private readonly ILogger _logger = logger.ForContext<ModImporter>();

    /// <inheritdoc />
    public async Task<ModImportPlan> PlanAsync(
        string modsDirectory,
        GameData data,
        IReadOnlyDictionary<string, string>? characterChoices = null,
        bool compareHashes = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);
        ArgumentNullException.ThrowIfNull(data);

        if (!PathComparer.TryResolveExisting(modsDirectory, out var resolved) || !Directory.Exists(resolved))
        {
            throw new ModOperationException($"The Mods folder '{PathDisplay.Show(modsDirectory)}' does not exist.", modsDirectory);
        }

        var root = PathComparer.Normalize(resolved);
        var choices = new Dictionary<string, string>(characterChoices ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        var index = compareHashes ? SortIndex.Build(data) : null;
        var rows = new List<ModImportRow>();

        foreach (var (folder, source, file) in Find(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await PlanRowAsync(folder, source, file, data, choices, index, cancellationToken).ConfigureAwait(false) is { } row)
            {
                rows.Add(row);
            }
        }

        var unmatched = rows
            .Where(row => row.Character is null && !row.ToOthers && row.Details.Character is { Length: > 0 })
            .GroupBy(row => row.Details.Character!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new UnmatchedCharacterTag(group.First().Details.Character!, group.Count()))
            .OrderByDescending(tag => tag.ModCount)
            .ThenBy(tag => tag.Tag, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ModImportPlan
        {
            ModsDirectory = root,
            Rows = rows,
            UnmatchedTags = unmatched,
        };
    }

    /// <inheritdoc />
    public async Task<ModImportResult> ApplyAsync(IReadOnlyList<ModImportRow> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var written = new List<string>();
        var failures = new List<(string, string)>();
        var unchanged = 0;

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!row.ChangesAnything)
            {
                unchanged++;
                continue;
            }

            try
            {
                // Checked again at write time: the user may have filled a field in the app since.
                var before = await _configs.ReadAsync(row.ModFolder, cancellationToken).ConfigureAwait(false);

                if (before is not null && ReferenceEquals(Merge(before, row, isNew: false), before))
                {
                    unchanged++;
                    continue;
                }

                await _configs
                    .UpdateAsync(row.ModFolder, config => Merge(config, row, isNew: before is null), cancellationToken)
                    .ConfigureAwait(false);

                written.Add(row.ModFolder);

                _logger.Information(
                    "Brought in {Fields} from {Source} ({File}) for {Mod}",
                    string.Join(", ", row.Fills),
                    row.Source,
                    row.SourceFile,
                    row.ModFolder);
            }
            catch (ModOperationException ex)
            {
                failures.Add((row.ModFolder, ex.Message));
                _logger.Warning(ex, "Could not bring in the details of {Mod}", row.ModFolder);
            }
        }

        return new ModImportResult(written, unchanged, failures);
    }

    private async Task<ModImportRow?> PlanRowAsync(
        string folder,
        ModImportSource source,
        string file,
        GameData data,
        Dictionary<string, string> choices,
        SortIndex? index,
        CancellationToken cancellationToken)
    {
        var name = ModsFolderLayout.StripDisabledPrefix(Path.GetFileName(folder));
        var row = new ModImportRow
        {
            ModFolder = folder,
            ModName = name,
            Source = source,
            SourceFile = file,
            Details = new ImportedDetails(),
            Fills = [],
        };

        string json;

        try
        {
            json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return row with { Problem = $"{Path.GetFileName(file)} could not be read: {ex.Message}" };
        }

        if (!ImportSourceReader.TryParse(json, out var document, out var problem))
        {
            if (source == ModImportSource.XxModManager)
            {
                return null;
            }

            return row with { Problem = $"{Path.GetFileName(file)} could not be read. {problem}" };
        }

        ImportedDetails details;

        using (document)
        {
            if (source == ModImportSource.XxModManager && !ImportSourceReader.IsXxModManager(document!.RootElement))
            {
                return null;
            }

            details = source == ModImportSource.Jasm
                ? ImportSourceReader.ReadJasm(document!.RootElement, folder)
                : ImportSourceReader.ReadXxModManager(document!.RootElement, folder);
        }

        row = row with { Details = details };

        ModConfig? existing;

        try
        {
            existing = await _configs.ReadAsync(folder, cancellationToken).ConfigureAwait(false);
        }
        catch (ModOperationException ex)
        {
            // Never written over: an unreadable file may hold details the user cannot get back.
            return row with { Problem = $"XXSM's own details for this mod cannot be read, so nothing will be written: {ex.Message}" };
        }

        var (character, chosen, toOthers) = Resolve(details.Character, data, choices);

        row = row with
        {
            Character = character,
            CharacterChosen = chosen,
            ToOthers = toOthers,
            Fills = Fills(details, existing, name, character, toOthers),
        };

        if (index is not null && character is not null)
        {
            row = row with { HashCharacter = await HashDisagreementAsync(folder, name, character, data, index, cancellationToken).ConfigureAwait(false) };
        }

        return row;
    }

    private async Task<MergedVariant?> HashDisagreementAsync(
        string folder, string name, MergedVariant tagged, GameData data, SortIndex index, CancellationToken cancellationToken)
    {
        ModSignals signals;

        try
        {
            signals = await _signals.ExtractAsync(folder, null, cancellationToken).ConfigureAwait(false);
        }
        catch (ModOperationException ex)
        {
            _logger.Warning(ex, "Could not read the hashes of {Mod} to compare with its tag", folder);
            return null;
        }

        var decision = _sorter.Sort(index, signals, new SortRequest(null, name));

        if (decision.DecidedBy != SortDecidedBy.Hash || decision.VariantId is not { } variantId ||
            data.Find(variantId) is not { } hashed)
        {
            return null;
        }

        // Within one family only a hash-certain outfit outweighs the user's tag.
        var differentFamily = !string.Equals(hashed.FamilyId, tagged.FamilyId, StringComparison.Ordinal);
        var differentOutfit = tagged.Hashes.Count > 0 &&
                              !decision.MemberDecidedByName && !decision.IsDefaultVariantFallback &&
                              !string.Equals(hashed.InternalName, tagged.InternalName, StringComparison.OrdinalIgnoreCase);

        return differentFamily || differentOutfit ? hashed : null;
    }

    private static (MergedVariant? Character, bool Chosen, bool ToOthers) Resolve(
        string? tag, GameData data, Dictionary<string, string> choices)
    {
        if (tag is not { Length: > 0 })
        {
            return (null, false, false);
        }

        if (choices.TryGetValue(tag, out var choice))
        {
            if (data.Find(choice) is { } picked)
            {
                return (picked, true, false);
            }

            if (PathComparer.AreNamesEqual(choice, ModsFolderLayout.UnsortedFolderName))
            {
                return (null, true, true);
            }
        }

        var wanted = SortName.Normalize(tag);

        if (wanted.Length == 0)
        {
            return (null, false, false);
        }

        var matches = data.Variants
            .Where(variant => Names(variant).Any(candidate =>
                string.Equals(SortName.Normalize(candidate), wanted, StringComparison.Ordinal)))
            .ToList();

        return (matches.Count == 1 ? matches[0] : null, false, false);
    }

    private static IEnumerable<string> Names(MergedVariant variant)
    {
        yield return variant.InternalName;
        yield return variant.DisplayName;
        yield return variant.ModFilesName;

        foreach (var alias in variant.Aliases)
        {
            yield return alias;
        }
    }

    private static List<string> Fills(ImportedDetails details, ModConfig? existing, string modName, MergedVariant? character, bool toOthers)
    {
        var fills = new List<string>();

        if (details.Name is { } name && Empty(existing?.CustomName) && !string.Equals(name, modName.Trim(), StringComparison.Ordinal))
        {
            fills.Add("name");
        }

        if (details.Author is not null && Empty(existing?.Author))
        {
            fills.Add("author");
        }

        if (details.Version is not null && Empty(existing?.Version))
        {
            fills.Add("version");
        }

        if (details.Description is not null && Empty(existing?.Description))
        {
            fills.Add("description");
        }

        if (details.Url is not null && Empty(existing?.ModUrl))
        {
            fills.Add("link");
        }

        if (details.ImagePath is not null && Empty(existing?.ImagePath) && existing?.NoImage != true)
        {
            fills.Add("picture");
        }

        if (NewTags(details.Tags, existing?.Tags).Count > 0)
        {
            fills.Add("tags");
        }

        if (details.Notes is not null && Empty(existing?.Notes))
        {
            fills.Add("notes");
        }

        if (details.DateAdded is not null && existing is null)
        {
            fills.Add("date");
        }

        if ((character is not null || toOthers) && Empty(existing?.VariantOverride))
        {
            fills.Add("character");
        }

        return fills;
    }

    /// <summary>Fills what is empty and nothing else, checked again at write time.</summary>
    private static ModConfig Merge(ModConfig config, ModImportRow row, bool isNew)
    {
        var details = row.Details;
        var merged = config;

        if (row.Fills.Contains("name") && Empty(merged.CustomName))
        {
            merged = merged with { CustomName = details.Name };
        }

        if (Empty(merged.Author) && details.Author is not null)
        {
            merged = merged with { Author = details.Author };
        }

        if (Empty(merged.Version) && details.Version is not null)
        {
            merged = merged with { Version = details.Version };
        }

        if (Empty(merged.Description) && details.Description is not null)
        {
            merged = merged with { Description = details.Description };
        }

        if (Empty(merged.ModUrl) && details.Url is not null)
        {
            merged = merged.WithModUrl(details.Url);
        }

        if (Empty(merged.ImagePath) && !merged.NoImage && details.ImagePath is not null)
        {
            merged = merged with { ImagePath = details.ImagePath };
        }

        if (NewTags(details.Tags, merged.Tags) is { Count: > 0 } added)
        {
            merged = merged with { Tags = [.. merged.Tags ?? [], .. added] };
        }

        if (Empty(merged.Notes) && details.Notes is not null)
        {
            merged = merged with { Notes = details.Notes };
        }

        if (isNew && details.DateAdded is { } date)
        {
            merged = merged with { DateAdded = date };
        }

        if (row.FollowTag && row.Character is { } character && Empty(merged.VariantOverride))
        {
            merged = merged with { VariantOverride = character.InternalName };
        }
        else if (row.FollowTag && row.ToOthers && Empty(merged.VariantOverride))
        {
            merged = merged with { VariantOverride = ModsFolderLayout.UnsortedFolderName };
        }

        return merged;
    }

    private static List<string> NewTags(IReadOnlyList<string> incoming, IReadOnlyList<string>? existing) =>
        [.. incoming
            .Where(tag => !(existing ?? []).Contains(tag, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    private static bool Empty(string? value) => string.IsNullOrWhiteSpace(value);

    /// <summary>Every folder holding JASM's or XX-Mod-Manager's file, not looking inside a mod once found.</summary>
    private static IEnumerable<(string Folder, ModImportSource Source, string File)> Find(string root)
    {
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((root, 0));

        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Pop();
            string[] children;

            try
            {
                children = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // Reverse order onto a stack gives name order off it.
            foreach (var child in children.Order(StringComparer.Ordinal).Reverse())
            {
                if (Path.GetFileName(child).StartsWith('.') || new DirectoryInfo(child).LinkTarget is not null)
                {
                    continue;
                }

                if (Source(child) is { } found)
                {
                    yield return (PathComparer.Normalize(child), found.Source, found.File);
                    continue;
                }

                if (depth + 1 < MaximumDepth)
                {
                    pending.Push((child, depth + 1));
                }
            }
        }
    }

    /// <summary>Which manager's file a folder has; a <c>mod.json</c> is only a candidate until it is read.</summary>
    private static (ModImportSource Source, string File)? Source(string folder)
    {
        if (PathComparer.TryResolveExisting(Path.Combine(folder, ImportSourceReader.JasmFileName), out var jasm) && File.Exists(jasm))
        {
            return (ModImportSource.Jasm, jasm);
        }

        var xxmm = Path.Combine(folder, ImportSourceReader.XxModManagerFileName);

        return File.Exists(xxmm) ? (ModImportSource.XxModManager, xxmm) : null;
    }
}
