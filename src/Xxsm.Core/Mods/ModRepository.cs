using Serilog;
using Xxsm.Core.Diagnostics;
using Xxsm.Core.Io;

namespace Xxsm.Core.Mods;

/// <summary>The default <see cref="IModRepository"/>: characters, then their mods, two levels deep.</summary>
public sealed class ModRepository(IModConfigStore configs, ILogger logger) : IModRepository
{
    private readonly IModConfigStore _configs = configs;
    private readonly ILogger _logger = logger.ForContext<ModRepository>();

    /// <inheritdoc />
    public async Task<ModsInventory> ScanAsync(
        string modsDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);

        if (!PathComparer.TryResolveExisting(modsDirectory, out var root) || !Directory.Exists(root))
        {
            throw new ModOperationException(
                $"The Mods folder '{PathDisplay.Show(modsDirectory)}' does not exist. " +
                "Point XXSM at your game's Mods directory, for example …/GIMI/Mods.",
                modsDirectory);
        }

        var diagnostics = new List<Diagnostic>();
        var variantFolders = new List<VariantFolder>();
        var unfiled = new List<InstalledMod>();

        foreach (var entry in ListDirectories(root, diagnostics))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(entry);

            if (ReportLeftover(entry, diagnostics))
            {
                continue;
            }

            if (ModsFolderLayout.IsReservedEntry(name))
            {
                ReportTrashInside(entry, diagnostics);
                continue;
            }

            if (ModsFolderLayout.LooksLikeModFolder(entry))
            {
                // A mod dropped straight into the Mods folder is a loose mod, not a character.
                var mod = await ReadModCoreAsync(entry, variantFolderName: null, diagnostics, cancellationToken)
                    .ConfigureAwait(false);

                unfiled.Add(mod);

                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Info,
                    ModDiagnosticCodes.UnfiledMod,
                    $"'{name}' is a mod sitting at the top of your Mods folder rather than " +
                    "under a character. Auto-sort can file it.")
                {
                    Paths = [mod.Path],
                });

                continue;
            }

            var mods = new List<InstalledMod>();

            foreach (var child in ListDirectories(entry, diagnostics))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (ReportLeftover(child, diagnostics))
                {
                    continue;
                }

                if (ModsFolderLayout.IsReservedEntry(Path.GetFileName(child)))
                {
                    ReportTrashInside(child, diagnostics);
                    continue;
                }

                mods.Add(await ReadModCoreAsync(child, name, diagnostics, cancellationToken)
                    .ConfigureAwait(false));
            }

            mods.Sort(static (left, right) => PathComparer.Instance.Compare(left.Name, right.Name));

            if (mods.Count == 0)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Info,
                    ModDiagnosticCodes.EmptyVariantFolder,
                    $"'{name}' has no mods in it.")
                {
                    Paths = [PathComparer.Normalize(entry)],
                });
            }

            variantFolders.Add(new VariantFolder
            {
                Name = name,
                Path = PathComparer.Normalize(entry),
                Mods = mods,
                OtherEntryCount = mods.Count == 0 ? CountEntries(entry) : 0,
            });

            ReportCaseCollisions(name, mods.Select(mod => (mod.FolderName, mod.Path)), diagnostics);
        }

        variantFolders.Sort(static (left, right) => PathComparer.Instance.Compare(left.Name, right.Name));
        unfiled.Sort(static (left, right) => PathComparer.Instance.Compare(left.Name, right.Name));

        ReportCaseCollisions(
            null,
            variantFolders.Select(folder => (folder.Name, folder.Path))
                .Concat(unfiled.Select(mod => (mod.FolderName, mod.Path))),
            diagnostics);

        var inventory = new ModsInventory
        {
            ModsDirectory = PathComparer.Normalize(root),
            VariantFolders = variantFolders,
            UnfiledMods = unfiled,
            Diagnostics = diagnostics,
        };

        _logger.Information(
            "Scanned {ModsDirectory}: {Folders} character folders, {Mods} mods, {Enabled} enabled",
            inventory.ModsDirectory,
            variantFolders.Count,
            inventory.ModCount,
            inventory.EnabledModCount);

        return inventory;
    }

    /// <inheritdoc />
    public async Task<InstalledMod> ReadModAsync(
        string modFolder, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modFolder);

        if (!PathComparer.TryResolveExisting(modFolder, out var resolved) || !Directory.Exists(resolved))
        {
            throw new ModOperationException(
                $"The mod folder '{PathDisplay.Show(modFolder)}' does not exist.", modFolder);
        }

        var parent = Path.GetDirectoryName(resolved);

        return await ReadModCoreAsync(
                resolved,
                parent is null ? null : Path.GetFileName(parent),
                diagnostics: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<InstalledMod> ReadModCoreAsync(
        string path,
        string? variantFolderName,
        List<Diagnostic>? diagnostics,
        CancellationToken cancellationToken)
    {
        var folderName = Path.GetFileName(path);
        ModConfig? config = null;
        string? configError = null;

        try
        {
            config = await _configs.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (ModOperationException ex)
        {
            // Recorded, not swallowed: nothing will overwrite a file XXSM could not read.
            configError = ex.Message;

            diagnostics?.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                ModDiagnosticCodes.UnreadableModConfig,
                ex.Message)
            {
                Paths = [PathComparer.Normalize(path)],
            });

            _logger.Warning(ex, "Could not read mod details in {Path}", path);
        }

        return new InstalledMod
        {
            Path = PathComparer.Normalize(path),
            FolderName = folderName,
            Name = ModsFolderLayout.StripDisabledPrefix(folderName),
            IsEnabled = !ModsFolderLayout.IsDisabled(folderName),
            VariantFolderName = variantFolderName,
            LastWriteTimeUtc = LastWriteTimeOrNull(path),
            Config = config,
            ConfigError = configError,
        };
    }

    /// <summary>When a mod folder last changed, or null when the filesystem will not say.</summary>
    private static DateTimeOffset? LastWriteTimeOrNull(string path)
    {
        try
        {
            return new DateTimeOffset(Directory.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private List<string> ListDirectories(string directory, List<Diagnostic> diagnostics)
    {
        try
        {
            return Directory.EnumerateDirectories(directory).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                ModDiagnosticCodes.UnreadableDirectory,
                $"Could not list '{PathDisplay.Show(directory)}': {ex.Message}")
            {
                Paths = [PathComparer.Normalize(directory)],
            });

            _logger.Warning(ex, "Could not list {Directory}", directory);
            return [];
        }
    }

    /// <summary>How many entries sit directly inside a folder, or zero when it cannot be listed.</summary>
    private int CountEntries(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory).Count();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(ex, "Could not count what is in {Directory}", directory);
            return 0;
        }
    }

    /// <summary>Reports what an interrupted update or move left, which is never read as a mod or character.</summary>
    /// <returns>Whether <paramref name="folder"/> was one.</returns>
    private static bool ReportLeftover(string folder, List<Diagnostic> diagnostics)
    {
        if (ModsFolderLeftovers.Examine(folder) is not { } leftover)
        {
            return false;
        }

        var (severity, code) = leftover.Kind switch
        {
            LeftoverKind.Spare => (DiagnosticSeverity.Info, ModDiagnosticCodes.LeftoverSpareCopy),
            LeftoverKind.OnlyCopy => (DiagnosticSeverity.Warning, ModDiagnosticCodes.LeftoverOnlyCopy),
            _ => (DiagnosticSeverity.Warning, ModDiagnosticCodes.LeftoverUnrecognised),
        };

        diagnostics.Add(new Diagnostic(severity, code, leftover.Description) { Paths = [leftover.Path] });
        return true;
    }

    /// <summary>Warns of a trash folder an older XXSM made inside Mods, where the game still loads its mods.</summary>
    private static void ReportTrashInside(string folder, List<Diagnostic> diagnostics)
    {
        if (!string.Equals(Path.GetFileName(folder), FreedesktopTrashService.FallbackDirectoryName, StringComparison.Ordinal))
        {
            return;
        }

        diagnostics.Add(new Diagnostic(
            DiagnosticSeverity.Warning,
            ModDiagnosticCodes.TrashInsideMods,
            $"XXSM's trash folder '{PathDisplay.Show(folder)}' is inside your Mods folder, so the game still loads the " +
            "deleted mods in it. Move it beside the Mods folder, where the game does not look.")
        {
            Paths = [PathComparer.Normalize(folder)],
        });
    }

    /// <summary>Reports sibling folders that differ only by case, which the game sees as one.</summary>
    private static void ReportCaseCollisions(
        string? parentName, IEnumerable<(string Name, string Path)> entries, List<Diagnostic> diagnostics)
    {
        var groups = entries
            .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1);

        foreach (var group in groups)
        {
            var where = parentName is null ? "your Mods folder" : $"'{parentName}'";
            var ordered = group.OrderBy(entry => entry.Name, StringComparer.Ordinal).ToList();

            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                ModDiagnosticCodes.CaseCollision,
                $"{string.Join(" and ", ordered.Select(entry => $"'{entry.Name}'"))} " +
                $"in {where} differ only by capitalisation. 3DMigoto under Wine will treat " +
                "them as one folder, so one of them will not do anything. Rename one.")
            {
                Paths = [.. ordered.Select(entry => entry.Path)],
            });
        }
    }
}
