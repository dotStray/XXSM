using System.Globalization;
using Serilog;
using Xxsm.Core.Io;

namespace Xxsm.Core.Settings;

/// <summary>Puts XXSM back as first installed, moving everything in its own folders to the trash.</summary>
/// <remarks>
/// The logs and any Mods folder stay. Nothing is deleted. The window schedules a reset and restarts, and the
/// next start runs it before reading anything; the CLI runs it at once.
/// </remarks>
public interface IFactoryReset
{
    /// <summary>The file whose presence means a reset is waiting for the next start.</summary>
    string ScheduleFile { get; }

    /// <summary>Whether a reset is waiting for the next start.</summary>
    bool IsScheduled { get; }

    /// <summary>Lists what a reset would move to the trash, and what it would leave. Changes nothing.</summary>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="ModOperationException">A folder could not be listed; with the OS error text.</exception>
    Task<FactoryResetPlan> PlanAsync(CancellationToken cancellationToken = default);

    /// <summary>Records that the next start should reset XXSM before it reads anything.</summary>
    /// <param name="cancellationToken">Cancels before anything is written.</param>
    /// <exception cref="ModOperationException">The request could not be written; with the OS error text.</exception>
    Task ScheduleAsync(CancellationToken cancellationToken = default);

    /// <summary>Trashes what <see cref="PlanAsync"/> lists, carrying on past failures, then unschedules.</summary>
    /// <param name="cancellationToken">Cancels between items; an item already moving finishes.</param>
    /// <returns>What moved, and what could not and why.</returns>
    Task<FactoryResetResult> RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>What a reset would do.</summary>
/// <param name="Items">The files and folders that would go to the trash, in order.</param>
/// <param name="Directories">The four directories they are taken from.</param>
/// <param name="Kept">What stays although it is inside those directories: the logs, and any Mods folder.</param>
public sealed record FactoryResetPlan(
    IReadOnlyList<string> Items,
    IReadOnlyList<string> Directories,
    IReadOnlyList<string> Kept);

/// <summary>What a reset did.</summary>
/// <param name="Moved">Each item that went to the trash, and where it is now.</param>
/// <param name="Failed">Each item that could not be moved, and why.</param>
public sealed record FactoryResetResult(
    IReadOnlyList<TrashResult> Moved,
    IReadOnlyList<FactoryResetFailure> Failed)
{
    /// <summary>Whether every item was moved.</summary>
    public bool Succeeded => Failed.Count == 0;
}

/// <summary>An item a reset could not move.</summary>
/// <param name="Path">The file or folder.</param>
/// <param name="Message">Why, in the operating system's own words.</param>
public sealed record FactoryResetFailure(string Path, string Message);

/// <summary>The default <see cref="IFactoryReset"/>.</summary>
public sealed class FactoryReset(
    IAppPaths paths,
    IAppSettingsStore settings,
    ITrashService trash,
    TimeProvider clock,
    ILogger logger) : IFactoryReset
{
    /// <summary>The name of the file that schedules a reset, in the state directory.</summary>
    public const string ScheduleFileName = "reset-scheduled";

    /// <summary>The trash service's own fallback folder: already trash, so a reset leaves it.</summary>
    private const string FallbackTrashName = ".xxsm-trash";

    private readonly IAppPaths _paths = paths;
    private readonly IAppSettingsStore _settings = settings;
    private readonly ITrashService _trash = trash;
    private readonly TimeProvider _clock = clock;
    private readonly ILogger _logger = logger.ForContext<FactoryReset>();

    /// <inheritdoc />
    public string ScheduleFile => PathComparer.Join(_paths.StateDirectory, ScheduleFileName);

    /// <inheritdoc />
    public bool IsScheduled => File.Exists(ScheduleFile);

    /// <inheritdoc />
    public async Task<FactoryResetPlan> PlanAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var roots = Roots();

        // Only folders XXSM made for itself, so an XXSM_*_HOME pointed at the home folder cannot be emptied.
        if (roots.FirstOrDefault(root => Directory.Exists(root) && !OwnFolder.IsOwn(root)) is { } foreign)
        {
            throw new ModOperationException(
                $"'{foreign}' is not a folder XXSM made for itself: it is not named {AppInfo.Slug} and has no " +
                $"{OwnFolder.MarkerName} in it, so Reset XXSM will not empty it. If an XXSM_*_HOME setting points " +
                "there, point it at a folder of XXSM's own. Nothing was moved.",
                foreign);
        }

        var kept = await KeptAsync(cancellationToken).ConfigureAwait(false);

        // The schedule is the reset's own bookkeeping, neither listed nor kept; the markers stay.
        var untouchable = kept.Append(ScheduleFile)
            .Append(_paths.HomeTrashDirectory)
            .Concat(roots.Select(root => Path.Combine(root, OwnFolder.MarkerName)))
            .ToList();

        var items = new List<string>();
        var seen = new HashSet<string>(PathComparer.Instance);

        foreach (var root in roots)
        {
            Collect(root, roots, untouchable, items, seen, cancellationToken);
        }

        return new FactoryResetPlan(
            items,
            roots,
            [.. kept.Where(path => roots.Any(root => PathComparer.IsSameOrUnder(root, path)) && Exists(path))]);
    }

    /// <inheritdoc />
    public async Task ScheduleAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var file = ScheduleFile;

        try
        {
            Directory.CreateDirectory(_paths.StateDirectory);

            await File.WriteAllTextAsync(
                file,
                "XXSM moves everything in its own folders to the trash at its next start, then removes this file." +
                Environment.NewLine +
                _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture) + Environment.NewLine,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException(
                $"The reset could not be scheduled, because '{PathDisplay.Show(file)}' could not be written: {ex.Message}",
                file,
                ex);
        }

        _logger.Information("Scheduled a reset for the next start in {ScheduleFile}", file);
    }

    /// <inheritdoc />
    public async Task<FactoryResetResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var plan = await PlanAsync(cancellationToken).ConfigureAwait(false);

        _logger.Information(
            "Resetting XXSM: moving {Count} items from {Directories} to the trash; keeping {Kept}",
            plan.Items.Count, plan.Directories, plan.Kept);

        var moved = new List<TrashResult>();
        var failed = new List<FactoryResetFailure>();

        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var parent = Path.GetDirectoryName(item) ?? item;

                moved.Add(await _trash.TrashAsync(item, parent, cancellationToken).ConfigureAwait(false));
            }
            catch (ModOperationException ex)
            {
                _logger.Warning(ex, "The reset could not move {Path} to the trash", item);
                failed.Add(new FactoryResetFailure(item, ex.Message));
            }
        }

        if (ClearSchedule() is { } stuck)
        {
            failed.Add(stuck);
        }

        _logger.Information(
            "Reset finished: {Moved} moved to the trash, {Failed} could not be moved",
            moved.Count, failed.Count);

        return new FactoryResetResult(moved, failed);
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool IsEmptyDirectory(string path)
    {
        try
        {
            return Directory.Exists(path)
                && !new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint)
                && !Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Lists one directory, going inside any entry that holds something that stays.</summary>
    private static void Collect(
        string directory,
        IReadOnlyList<string> roots,
        IReadOnlyList<string> untouchable,
        List<string> items,
        HashSet<string> seen,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        List<string> entries;

        try
        {
            entries = [.. Directory.EnumerateFileSystemEntries(directory)
                .Select(PathComparer.Normalize)
                .Order(PathComparer.Instance)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException(
                $"XXSM could not list what is in '{PathDisplay.Show(directory)}' to reset it: {ex.Message}",
                directory,
                ex);
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (PathComparer.AreNamesEqual(Path.GetFileName(entry), FallbackTrashName)
                || untouchable.Any(path => PathComparer.AreEqual(path, entry))
                || roots.Any(root => PathComparer.AreEqual(root, entry)))
            {
                continue;
            }

            if (untouchable.Concat(roots).Any(path => PathComparer.IsSameOrUnder(entry, path)))
            {
                // A folder holding something that stays: its other contents go. A link is never followed.
                if (!new FileInfo(entry).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    Collect(entry, roots, untouchable, items, seen, cancellationToken);
                }

                continue;
            }

            if (IsEmptyDirectory(entry))
            {
                continue;
            }

            if (seen.Add(entry))
            {
                items.Add(entry);
            }
        }
    }

    /// <summary>The four directories, each once, in the order a person would list them.</summary>
    private List<string> Roots() =>
        [
            .. new[]
                {
                    _paths.ConfigDirectory,
                    _paths.DataDirectory,
                    _paths.CacheDirectory,
                    _paths.StateDirectory,
                }
                .Select(PathComparer.Normalize)
                .Distinct(PathComparer.Instance),
        ];

    /// <summary>The logs and every game's Mods folder: what a reset leaves where it is.</summary>
    private async Task<List<string>> KeptAsync(CancellationToken cancellationToken)
    {
        var kept = new List<string> { PathComparer.Normalize(_paths.LogsDirectory) };

        try
        {
            var settings = await _settings.ReadAsync(cancellationToken).ConfigureAwait(false);

            kept.AddRange(settings.Games.Values
                .Select(game => game.ModsDirectory)
                .OfType<string>()
                .Where(path => path.Length > 0)
                .Select(PathComparer.Normalize));
        }
        catch (SettingsLoadException ex)
        {
            _logger.Warning(ex, "The reset could not read which Mods folders to leave alone");
        }

        return [.. kept.Distinct(PathComparer.Instance)];
    }

    /// <summary>Removes the schedule, or says why it is still there.</summary>
    private FactoryResetFailure? ClearSchedule()
    {
        var file = ScheduleFile;

        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            // XXSM's own marker, written a moment ago: nothing to trash.
            File.Delete(file);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Said out loud: left in place, it resets XXSM again at the next start.
            _logger.Warning(ex, "Could not remove {ScheduleFile} after the reset", file);

            return new FactoryResetFailure(
                file,
                $"{ex.Message} XXSM will reset itself again at its next start unless this file is removed.");
        }
    }
}
