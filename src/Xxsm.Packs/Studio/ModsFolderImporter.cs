using Serilog;
using Xxsm.Core;
using Xxsm.Core.Diagnostics;
using Xxsm.Core.Hashes;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Core.Text;
using Xxsm.Packs.Characters;
using Xxsm.Packs.Hashes;
using Xxsm.Packs.Model;

namespace Xxsm.Packs.Studio;

/// <summary>Imports a populated Mods folder: one character per character folder, with its mods' hashes.</summary>
public interface IModsFolderImporter
{
    /// <summary>Reads every character folder and its mods, and works out what importing them would do.</summary>
    /// <param name="draft">The draft it would be imported into.</param>
    /// <param name="modsDirectory">The Mods folder. Nothing in it is modified.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="ModOperationException">The folder does not exist or cannot be listed.</exception>
    Task<CharacterImportPlan> PlanAsync(
        PackDraft draft,
        string modsDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>Applies the rows a person chose. Pure: returns a new draft and writes nothing.</summary>
    /// <param name="draft">The draft to apply it to — normally the one the plan was made against.</param>
    /// <param name="plan">The preview.</param>
    /// <param name="choices">What was decided per row; a row with no choice takes its defaults.</param>
    /// <param name="now">When, for the draft's import history.</param>
    StudioImportOutcome Apply(
        PackDraft draft,
        CharacterImportPlan plan,
        IReadOnlyList<CharacterImportChoice>? choices,
        DateTimeOffset now);
}

/// <summary>The default <see cref="IModsFolderImporter"/>: every mod's hashes in a folder, pooled.</summary>
public sealed class ModsFolderImporter(
    IModRepository repository,
    IModSignalExtractor signals,
    ILogger logger) : IModsFolderImporter
{
    private readonly IModRepository _repository = repository;
    private readonly IModSignalExtractor _signals = signals;
    private readonly ILogger _logger = logger.ForContext<ModsFolderImporter>();

    /// <inheritdoc />
    public async Task<CharacterImportPlan> PlanAsync(
        PackDraft draft,
        string modsDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);

        var resolved = PathComparer.TryResolveExisting(modsDirectory, out var found)
            ? found
            : PathComparer.Normalize(Path.GetFullPath(modsDirectory));

        if (!Directory.Exists(resolved))
        {
            throw new ModOperationException($"There is no folder at '{PathDisplay.Show(resolved)}'.", resolved);
        }

        var inventory = await _repository.ScanAsync(resolved, cancellationToken).ConfigureAwait(false);
        var diagnostics = new List<Diagnostic>(inventory.Diagnostics);

        if (inventory.UnfiledMods.Count > 0)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Info,
                StudioImportCodes.NoCharacterFolder,
                $"{EnglishCount.Plural(inventory.UnfiledMods.Count, "mod sits", "mods sit")} loose in the Mods folder rather than " +
                "in a character's folder, so there is no telling who they are for. They were not read."));
        }

        var existing = CharacterImports.VariantsById(draft);
        var existingHashes = CharacterImports.HashKeysById(draft);
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var shaders = new SortedSet<string>(StringComparer.Ordinal);
        var folders = new List<Folder>();

        foreach (var folder in inventory.VariantFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.Equals(folder.Name, ModsFolderLayout.UnsortedFolderName, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Info,
                    StudioImportCodes.NoCharacterFolder,
                    $"'{folder.Name}' holds the mods nobody could identify, so it is not a character. It was not read."));
                continue;
            }

            var (hashes, withHashes, rowDiagnostics) = await ReadFolderAsync(folder, cancellationToken).ConfigureAwait(false);

            foreach (var hash in hashes.Where(h => h.Kind == HashKind.RootVs))
            {
                shaders.Add(hash.Hash);
            }

            var id = PackDrafts.IsValidId(folder.Name) ? folder.Name : CharacterNames.Slugify(folder.Name);
            var conflict = claimed.TryGetValue(id, out var first);

            string? note;

            if (conflict)
            {
                note = $"'{folder.Name}' is the same name as '{first}' apart from capitals, and internal names ignore " +
                       "capitals. Give it a different internal name, or leave it out.";
            }
            else
            {
                claimed[id] = folder.Name;

                note = !string.Equals(id, folder.Name, StringComparison.Ordinal)
                    ? $"'{folder.Name}' cannot be an internal name as it is, so it would be '{id}'. Its mods stay in " +
                      $"'{folder.Name}'."
                    : folder.Mods.Count == 0
                        ? "Its folder has no mods in it, so it would have no hashes yet."
                        : withHashes == 0
                            ? $"None of its {EnglishCount.Plural(folder.Mods.Count, "mod", "mods")} has any hashes XXSM could read, " +
                              "so it would have no hashes yet."
                            : null;
            }

            folders.Add(new Folder(folder, id, hashes, withHashes, conflict, note, rowDiagnostics));
        }

        var names = folders.Where(f => !f.Conflict).Select(f => f.Id).Concat(existing.Keys).ToList();
        var proposals = SkinLinks.Propose(names)
            .GroupBy(p => p.InternalName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var rows = new List<CharacterImportRow>(folders.Count);

        foreach (var folder in folders)
        {
            var link = proposals.GetValueOrDefault(folder.Id)
                       ?? new SkinLinkProposal(folder.Id, null, LinkConfidence.High, "No other character's name begins its name.");

            var (action, newCount, missingCount, currentBase) = folder.Conflict
                ? (HashImportAction.Conflict, 0, 0, null)
                : CharacterImports.Compare(folder.Id, folder.Hashes, existing, existingHashes);

            rows.Add(new CharacterImportRow
            {
                SourcePath = folder.Source.Name,
                FolderName = folder.Source.Name,
                InternalName = folder.Id,

                // The folder already holds mods under this name, so the character keeps filing there.
                ModFilesName = ModsFolderLayout.DescribeUnusableFolderName(folder.Source.Name) is null
                    ? folder.Source.Name
                    : null,
                ModCount = folder.Source.Mods.Count,
                Hashes = folder.Hashes,
                Action = action,
                NewHashCount = newCount,
                MissingHashCount = missingCount,
                Link = link,
                CurrentBaseCharacterId = currentBase,
                SelectedByDefault = action is HashImportAction.Create or HashImportAction.AddHashes,
                Note = folder.Note,
                Diagnostics = folder.Diagnostics,
            });
        }

        var plan = new CharacterImportPlan
        {
            Source = resolved,
            Rows = rows,
            SharedShaderHashes = [.. shaders],
            Diagnostics = diagnostics,
        };

        _logger.Information(
            "Planned a Mods folder import from {Source}: {Folders} character folders, {Create} to create, " +
            "{Add} to update, {Unchanged} unchanged, {Loose} loose mods not read",
            resolved,
            rows.Count,
            plan.CreateCount,
            plan.AddHashesCount,
            plan.UnchangedCount,
            inventory.UnfiledMods.Count);

        return plan;
    }

    /// <inheritdoc />
    public StudioImportOutcome Apply(
        PackDraft draft,
        CharacterImportPlan plan,
        IReadOnlyList<CharacterImportChoice>? choices,
        DateTimeOffset now)
    {
        var outcome = CharacterImports.Apply(draft, plan, choices, StudioImportKinds.ModsFolder, now);

        _logger.Information(
            "Applied a Mods folder import from {Source}: created {Created}, updated {Updated}, skipped {Skipped}",
            plan.Source,
            outcome.Created.Count,
            outcome.Updated.Count,
            outcome.Skipped.Count);

        return outcome;
    }

    /// <summary>Pools the hashes of every mod in one character folder.</summary>
    private async Task<(List<PackHashEntry> Hashes, int ModsWithHashes, List<Diagnostic> Diagnostics)> ReadFolderAsync(
        VariantFolder folder,
        CancellationToken cancellationToken)
    {
        var parsed = new List<ParsedHash>();
        var files = new List<string>();
        var diagnostics = new List<Diagnostic>();
        var withHashes = 0;

        foreach (var mod in folder.Mods)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ModSignals signals;

            try
            {
                signals = await _signals.ExtractAsync(mod.Path, bounds: null, cancellationToken).ConfigureAwait(false);
            }
            catch (ModOperationException ex)
            {
                // One mod that cannot be read must not cost the character its other mods.
                _logger.Warning(ex, "Could not read the mod {Mod} during a Mods folder import", mod.Path);
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    StudioImportCodes.UnreadableFile,
                    $"The mod '{mod.FolderName}' could not be read and was skipped: {ex.Message}")
                {
                    Paths = [mod.Path],
                });
                continue;
            }

            diagnostics.AddRange(signals.Diagnostics);
            var before = parsed.Count;

            foreach (var relative in signals.IniFiles)
            {
                await ModHashLearner.ReadAsync(signals.Root, relative, isJson: false, parsed, files, diagnostics, cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (var relative in signals.HashJsonFiles)
            {
                await ModHashLearner.ReadAsync(signals.Root, relative, isJson: true, parsed, files, diagnostics, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (signals.Hashes.Count > 0 || parsed.Count > before)
            {
                withHashes++;
            }
        }

        var hashes = parsed
            .Where(p => HashText.Normalize(p.Entry.Hash) is not null)
            .Select(p => p.Entry with { Variant = string.Empty })
            .ToList();

        return (hashes, withHashes, diagnostics);
    }

    private sealed record Folder(
        VariantFolder Source,
        string Id,
        List<PackHashEntry> Hashes,
        int ModsWithHashes,
        bool Conflict,
        string? Note,
        List<Diagnostic> Diagnostics);
}
