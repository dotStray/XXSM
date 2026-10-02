using System.Globalization;
using Serilog;
using Xxsm.Core.Io;

namespace Xxsm.Core.Mods;

/// <summary>Whether the copies are switched on or off.</summary>
public enum ModExportSwitching
{
    /// <summary>Each copy is on or off as its mod is now.</summary>
    AsTheyAre,

    /// <summary>Every copy is switched on: its <c>DISABLED_</c> prefix is left off.</summary>
    AllOn,

    /// <summary>Every copy is switched off: its folder name starts <c>DISABLED_</c>.</summary>
    AllOff,
}

/// <summary>What an export copies.</summary>
/// <param name="EnabledOnly">Copy only the mods that are switched on.</param>
/// <param name="SkipMetadata">Leave out each mod's <c>.xxsm/</c> folder: its details, picture and sort record.</param>
/// <param name="OneFolder">Put every mod straight into the export folder, without its character folder.</param>
/// <param name="Switching">Whether the copies are switched on or off. Only the copies' names change.</param>
public sealed record ModExportOptions(
    bool EnabledOnly = false,
    bool SkipMetadata = false,
    bool OneFolder = false,
    ModExportSwitching Switching = ModExportSwitching.AsTheyAre);

/// <summary>What an export would copy.</summary>
public sealed record ModExportPlan
{
    /// <summary>The Mods folder.</summary>
    public required string ModsDirectory { get; init; }

    /// <summary>The options it was planned with.</summary>
    public required ModExportOptions Options { get; init; }

    /// <summary>The mods to copy, in the scan's order.</summary>
    public required IReadOnlyList<InstalledMod> Mods { get; init; }

    /// <summary>Where each of <see cref="Mods"/> goes, relative to the export folder, in the same order.</summary>
    public required IReadOnlyList<string> Targets { get; init; }

    /// <summary>How many copies had a number added, <c>Name (2)</c>, to keep names in one folder apart.</summary>
    public required int RenamedCount { get; init; }

    /// <summary>How many switched-off mods are left out.</summary>
    public required int SkippedDisabledCount { get; init; }

    /// <summary>How many files would be copied.</summary>
    public required int FileCount { get; init; }

    /// <summary>How many bytes would be copied.</summary>
    public required long TotalBytes { get; init; }
}

/// <summary>How far an export has got.</summary>
/// <param name="ModsDone">Mods copied so far.</param>
/// <param name="ModCount">Mods to copy in all.</param>
/// <param name="BytesDone">Bytes copied so far.</param>
/// <param name="TotalBytes">Bytes to copy in all.</param>
/// <param name="CurrentMod">The mod being copied, or <c>null</c> once it is finished.</param>
public sealed record ModExportProgress(int ModsDone, int ModCount, long BytesDone, long TotalBytes, string? CurrentMod);

/// <summary>What an export made.</summary>
/// <param name="Folder">The new folder holding the copy.</param>
/// <param name="ModCount">How many mods it holds.</param>
/// <param name="FileCount">How many files were copied.</param>
/// <param name="Bytes">How many bytes were copied.</param>
public sealed record ModExportResult(string Folder, int ModCount, int FileCount, long Bytes);

/// <summary>Copies a Mods folder's mods into a new folder of their own; the Mods folder is not changed.</summary>
public interface IModExporter
{
    /// <summary>Works out what an export would copy. Changes nothing.</summary>
    /// <param name="modsDirectory">The Mods folder.</param>
    /// <param name="options">What to copy.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>The mods, and how many files and bytes they come to.</returns>
    /// <exception cref="ModOperationException">The Mods folder could not be read.</exception>
    Task<ModExportPlan> PlanAsync(string modsDirectory, ModExportOptions options, CancellationToken cancellationToken = default);

    /// <summary>Copies the planned mods into a new folder inside the one given.</summary>
    /// <param name="plan">A plan from <see cref="PlanAsync"/>.</param>
    /// <param name="destinationParent">The folder to make the export in. Must exist, outside the Mods folder.</param>
    /// <param name="progress">Told after each file. Optional.</param>
    /// <param name="cancellationToken">Cancels between files, removing the half-made folder.</param>
    /// <returns>Where the copy is, and what it holds.</returns>
    /// <exception cref="ModOperationException">The destination is missing or inside Mods, or a copy failed; with the
    /// system's words. A failed export removes its half-made folder.</exception>
    Task<ModExportResult> ExportAsync(
        ModExportPlan plan,
        string destinationParent,
        IProgress<ModExportProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>The default <see cref="IModExporter"/>.</summary>
public sealed class ModExporter(IModRepository repository, TimeProvider time, ILogger logger) : IModExporter
{
    private const int MaximumDepth = 16;

    private readonly IModRepository _repository = repository;
    private readonly TimeProvider _time = time;
    private readonly ILogger _logger = logger.ForContext<ModExporter>();

    /// <inheritdoc />
    public async Task<ModExportPlan> PlanAsync(
        string modsDirectory, ModExportOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var inventory = await _repository.ScanAsync(modsDirectory, cancellationToken).ConfigureAwait(false);
        var mods = inventory.AllMods.Where(mod => !options.EnabledOnly || mod.IsEnabled).ToList();
        var fileCount = 0;
        long bytes = 0;

        foreach (var mod in mods)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                foreach (var file in Files(mod.Path, options))
                {
                    fileCount++;
                    bytes += Length(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ModOperationException(
                    $"Could not read '{PathDisplay.Show(mod.Path)}' to export it: {ex.Message}", mod.Path, ex);
            }
        }

        var (targets, renamed) = PlaceCopies(inventory.ModsDirectory, mods, options);

        return new ModExportPlan
        {
            ModsDirectory = inventory.ModsDirectory,
            Options = options,
            Mods = mods,
            Targets = targets,
            RenamedCount = renamed,
            SkippedDisabledCount = inventory.ModCount - mods.Count,
            FileCount = fileCount,
            TotalBytes = bytes,
        };
    }

    /// <inheritdoc />
    public async Task<ModExportResult> ExportAsync(
        ModExportPlan plan,
        string destinationParent,
        IProgress<ModExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationParent);

        if (!PathComparer.TryResolveExisting(destinationParent, out var parent) || !Directory.Exists(parent))
        {
            throw new ModOperationException(
                $"The folder '{PathDisplay.Show(destinationParent)}' does not exist. Choose a folder to put the export in.",
                destinationParent);
        }

        if (PathComparer.IsSameOrUnder(plan.ModsDirectory, parent))
        {
            throw new ModOperationException(
                $"'{PathDisplay.Show(parent)}' is inside the Mods folder. An export there would be loaded by the game as a " +
                "second copy of every mod; choose a folder outside it.",
                parent);
        }

        // Made in a working folder no one else can have chosen, renamed only once complete.
        var working = Path.Combine(parent, $".xxsm-export-{Guid.NewGuid():n}");
        var name = ExportName(plan.ModsDirectory);
        var created = false;
        string target;
        var filesDone = 0;
        long bytesDone = 0;

        try
        {
            if (Path.Exists(working))
            {
                throw new IOException($"'{PathDisplay.Show(working)}' already exists.");
            }

            Directory.CreateDirectory(working);
            created = true;

            for (var index = 0; index < plan.Mods.Count; index++)
            {
                var mod = plan.Mods[index];
                var destination = Path.Combine(working, plan.Targets[index]);

                progress?.Report(new ModExportProgress(index, plan.Mods.Count, bytesDone, plan.TotalBytes, mod.DisplayName));

                foreach (var file in Files(mod.Path, plan.Options))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var copyTo = Path.Combine(destination, Path.GetRelativePath(mod.Path, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(copyTo)!);

                    bytesDone += await CopyAsync(file, copyTo, cancellationToken).ConfigureAwait(false);
                    filesDone++;
                }

                Directory.CreateDirectory(destination);
            }

            cancellationToken.ThrowIfCancellationRequested();
            target = MoveToFreeName(working, parent, name);
        }
        catch (OperationCanceledException)
        {
            RemovePartial(working, created);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RemovePartial(working, created);

            throw new ModOperationException(
                $"The export stopped: {ex.Message}. Nothing in the Mods folder was changed, and the " +
                $"half-made copy at '{PathDisplay.Show(working)}' has been removed.",
                working,
                ex);
        }

        progress?.Report(new ModExportProgress(plan.Mods.Count, plan.Mods.Count, bytesDone, plan.TotalBytes, null));

        _logger.Information(
            "Exported {Mods} mods ({Files} files, {Bytes} bytes) from {ModsDirectory} to {Target}",
            plan.Mods.Count,
            filesDone,
            bytesDone,
            plan.ModsDirectory,
            target);

        return new ModExportResult(target, plan.Mods.Count, filesDone, bytesDone);
    }

    /// <summary>Where each mod goes in the copy; names clashing in one folder, ignoring case, get a number.</summary>
    private static (IReadOnlyList<string> Targets, int Renamed) PlaceCopies(
        string modsDirectory, List<InstalledMod> mods, ModExportOptions options)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new List<string>(mods.Count);
        var renamed = 0;

        foreach (var mod in mods)
        {
            var relative = PathComparer.Normalize(
                PathComparer.TryGetRelativePath(modsDirectory, mod.Path) ?? mod.FolderName).Trim('/');
            var cut = relative.LastIndexOf('/');
            var folder = options.OneFolder || cut < 0 ? string.Empty : relative[..cut];
            var name = cut < 0 ? relative : relative[(cut + 1)..];

            name = options.Switching switch
            {
                ModExportSwitching.AllOn => ModsFolderLayout.StripDisabledPrefix(name),
                ModExportSwitching.AllOff => ModsFolderLayout.AddDisabledPrefix(name),
                _ => name,
            };

            var target = Join(folder, name);

            if (!taken.Add(target))
            {
                renamed++;
                var number = 2;

                while (!taken.Add(target = Join(folder, $"{name} ({number.ToString(CultureInfo.InvariantCulture)})")))
                {
                    number++;
                }
            }

            targets.Add(target);
        }

        return (targets, renamed);
    }

    private static string Join(string folder, string name) => folder.Length == 0 ? name : $"{folder}/{name}";

    private string ExportName(string modsDirectory) =>
        $"{Path.GetFileName(PathComparer.Normalize(modsDirectory).TrimEnd('/'))} export " +
        _time.GetLocalNow().ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);

    private static string FreeName(string parent, string name)
    {
        var candidate = Path.Combine(parent, name);

        for (var suffix = 2; PathComparer.TryResolveExisting(candidate, out _); suffix++)
        {
            candidate = Path.Combine(parent, $"{name} ({suffix.ToString(CultureInfo.InvariantCulture)})");
        }

        return candidate;
    }

    /// <summary>Renames the finished copy to the first free export name, trying the next if one is taken.</summary>
    private string MoveToFreeName(string working, string parent, string name)
    {
        const int Attempts = 100;

        for (var attempt = 1; ; attempt++)
        {
            var target = FreeName(parent, name);

            try
            {
                Directory.Move(working, target);

                _logger.Information("Renamed the finished export {Working} to {Target}", working, target);
                return target;
            }
            catch (IOException) when (attempt < Attempts && Path.Exists(target) && Directory.Exists(working))
            {
            }
        }
    }

    /// <summary>Every file in a mod that the export copies, without following a link to a folder.</summary>
    private static IEnumerable<string> Files(string modFolder, ModExportOptions options)
    {
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((modFolder, 0));

        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Pop();

            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                if (Directory.Exists(entry))
                {
                    var isOwnMetadata = depth == 0 &&
                                        string.Equals(Path.GetFileName(entry), ModsFolderLayout.StateDirectoryName, StringComparison.Ordinal);

                    // A link to a folder can point back up the tree: not followed.
                    if ((options.SkipMetadata && isOwnMetadata) || depth + 1 > MaximumDepth ||
                        new DirectoryInfo(entry).LinkTarget is not null)
                    {
                        continue;
                    }

                    pending.Push((entry, depth + 1));
                    continue;
                }

                // A link to a file is not exported either: it could copy anything into an export that gets shared.
                if (new FileInfo(entry).LinkTarget is not null)
                {
                    continue;
                }

                yield return entry;
            }
        }
    }

    private static long Length(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static async Task<long> CopyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var reading = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);

        await using var writing = new FileStream(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);

        await reading.CopyToAsync(writing, cancellationToken).ConfigureAwait(false);

        return writing.Length;
    }

    /// <summary>Removes an export that stopped part-way: XXSM's own half-written copy, never user content.</summary>
    private void RemovePartial(string working, bool created)
    {
        if (!created)
        {
            return;
        }

        OwnScratch.TryDeleteFolder(working, _logger);
    }
}
