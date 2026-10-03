using Serilog;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;

namespace Xxsm.Core.Ini;

/// <summary>Which kind of line XXSM changed in a mod's INI.</summary>
public enum IniChangeKind
{
    /// <summary>A key binding's <c>key</c>, <c>back</c> or <c>$variable</c> line.</summary>
    Key,

    /// <summary>A saved setting's default, on its <c>global persist</c> line.</summary>
    Default,
}

/// <summary>One line of a mod's INI that differs from the author's, beside the author's value.</summary>
/// <param name="Kind">A key binding's line or a saved setting's default.</param>
/// <param name="Section">The section without brackets: <c>KeyHat</c>, or <c>Constants</c> for a default.</param>
/// <param name="Name">The line's name: <c>key</c>, <c>back</c> or a <c>$variable</c>.</param>
/// <param name="Original">The author's value; for a default, <c>0</c> when the author gave none.</param>
/// <param name="Current">The value now; for a default, <c>0</c> when the line gives none.</param>
public sealed record IniChange(IniChangeKind Kind, string Section, string Name, string Original, string Current);

/// <summary>One of a mod's INIs that differs from the copy XXSM kept before its first edit.</summary>
/// <param name="File">The INI, relative to the mod folder, with <c>/</c> separators.</param>
/// <param name="Changes">The key and default lines that differ, in file order.</param>
/// <param name="HasOtherChanges">Whether anything else differs too: lines added, removed or edited by hand.</param>
public sealed record ChangedIni(string File, IReadOnlyList<IniChange> Changes, bool HasOtherChanges);

/// <summary>What differs from the kept originals in one mod, and what could not be read.</summary>
/// <param name="ModFolder">The mod folder.</param>
/// <param name="Files">Each INI that differs from its kept original, in name order.</param>
/// <param name="Problems">What could not be read, each as a sentence.</param>
public sealed record ModIniChanges(string ModFolder, IReadOnlyList<ChangedIni> Files, IReadOnlyList<string> Problems)
{
    /// <summary>Whether any INI differs from its kept original.</summary>
    public bool HasChanges => Files.Count > 0;

    /// <summary>Whether a key binding differs from the author's.</summary>
    public bool HasKeyChanges => Files.Any(file => file.Changes.Any(change => change.Kind == IniChangeKind.Key));

    /// <summary>Whether a saved setting's default differs from the author's.</summary>
    public bool HasDefaultChanges => Files.Any(file => file.Changes.Any(change => change.Kind == IniChangeKind.Default));

    /// <summary>How many key and default lines differ, in every INI.</summary>
    public int LineCount => Files.Sum(file => file.Changes.Count);
}

/// <summary>A mod in the Mods folder with an INI that differs from its kept original.</summary>
/// <param name="Mod">The mod as the scan found it.</param>
/// <param name="Changes">What differs.</param>
public sealed record ChangedMod(InstalledMod Mod, ModIniChanges Changes);

/// <summary>How much of an INI a revert puts back.</summary>
public enum IniRevertScope
{
    /// <summary>Only the saved settings' defaults.</summary>
    Defaults,

    /// <summary>Only the key bindings.</summary>
    Keys,

    /// <summary>The whole file, exactly as the author shipped it.</summary>
    Everything,
}

/// <summary>One INI a rewrite changed, with its contents before and after, for an undo.</summary>
/// <param name="Path">The INI's full path.</param>
/// <param name="File">The INI, relative to the mod folder, with <c>/</c> separators.</param>
/// <param name="Before">What it held before.</param>
/// <param name="After">What was written.</param>
/// <param name="Lines">How many key or default lines changed; a whole-file revert counts those it put back.</param>
public sealed record IniRewriteFile(string Path, string File, byte[] Before, byte[] After, int Lines);

/// <summary>What a revert, or a change of defaults, wrote to one mod's INIs.</summary>
/// <param name="ModFolder">The mod folder.</param>
/// <param name="Files">Each INI written, in name order; empty when there was nothing to change.</param>
/// <param name="Trashed">A copy of each INI as it was before a revert, in the trash; empty for a change of defaults.</param>
public sealed record IniRewrite(string ModFolder, IReadOnlyList<IniRewriteFile> Files, IReadOnlyList<TrashResult> Trashed)
{
    /// <summary>Whether nothing was written.</summary>
    public bool IsEmpty => Files.Count == 0;

    /// <summary>How many key and default lines changed, in every INI.</summary>
    public int LineCount => Files.Sum(file => file.Lines);
}

/// <summary>One of the user's changes, and what became of it in a new version of the mod.</summary>
/// <param name="File">The INI, relative to the mod folder.</param>
/// <param name="Change">The change: the old version's author value and the user's.</param>
/// <param name="NewAuthorValue">The new version's own value for the line; null when the line is not there.</param>
public sealed record IniCarried(string File, IniChange Change, string? NewAuthorValue);

/// <summary>What became of the user's key and default changes when a mod was replaced by a new version.</summary>
/// <param name="Applied">Carried to the new version, whose author had kept the old value.</param>
/// <param name="Clashed">Carried to the new version, over a value its author changed.</param>
/// <param name="Missing">Not carried: the new version has no such INI, section or setting.</param>
/// <param name="OtherChangesLeft">INIs with other changes, made by hand, which were not carried.</param>
/// <param name="Problems">What could not be read or written, each as a sentence.</param>
public sealed record IniCarryReport(
    IReadOnlyList<IniCarried> Applied,
    IReadOnlyList<IniCarried> Clashed,
    IReadOnlyList<IniCarried> Missing,
    IReadOnlyList<string> OtherChangesLeft,
    IReadOnlyList<string> Problems)
{
    /// <summary>A report of nothing: the old version had no changes.</summary>
    public static IniCarryReport None { get; } = new([], [], [], [], []);

    /// <summary>Whether there was nothing to carry and nothing went wrong.</summary>
    public bool IsEmpty => Applied.Count == 0 && Clashed.Count == 0 && Missing.Count == 0 && OtherChangesLeft.Count == 0 && Problems.Count == 0;
}

/// <summary>Compares a mod's INIs with the copies kept before XXSM first changed them, and puts them back.</summary>
/// <remarks>
/// The copies are at <c>.xxsm/originals/name.ini.original</c>. A revert first puts a copy of each INI as it is into the
/// trash, so what it replaces can always be found again; an undo writes the replaced contents back.
/// </remarks>
public interface IIniOriginalsService
{
    /// <summary>What differs from the kept originals in one mod.</summary>
    /// <param name="modFolder">The mod folder.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="ModOperationException">The mod folder does not exist.</exception>
    Task<ModIniChanges> ReadAsync(string modFolder, CancellationToken cancellationToken = default);

    /// <summary>Every mod in the Mods folder with an INI that differs from its kept original, in the scan's order.</summary>
    /// <param name="modsDirectory">The Mods folder.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <exception cref="ModOperationException">The Mods folder could not be read.</exception>
    Task<IReadOnlyList<ChangedMod>> FindAsync(string modsDirectory, CancellationToken cancellationToken = default);

    /// <summary>Puts back the author's defaults, keys or whole INIs; a copy of each INI it replaces goes to the trash first.</summary>
    /// <param name="modFolder">The mod folder.</param>
    /// <param name="scope">How much to put back.</param>
    /// <param name="cancellationToken">Cancels before anything is written.</param>
    /// <returns>What was written, for <see cref="UndoAsync"/>; empty when nothing differed.</returns>
    /// <exception cref="ModOperationException">The mod folder does not exist, an INI could not be read, copied to the
    /// trash or written; any INI already written is then put back as it was.</exception>
    Task<IniRewrite> RevertAsync(string modFolder, IniRevertScope scope, CancellationToken cancellationToken = default);

    /// <summary>Makes the values the game saved for the mod's settings their defaults.</summary>
    /// <param name="modFolder">The mod folder.</param>
    /// <param name="cancellationToken">Cancels before anything is written.</param>
    /// <returns>What was written, for <see cref="UndoAsync"/>; empty when every default already matched.</returns>
    /// <exception cref="ModOperationException">The mod folder does not exist, or an INI could not be written.</exception>
    Task<IniRewrite> UseInGameDefaultsAsync(string modFolder, CancellationToken cancellationToken = default);

    /// <summary>Writes back what a rewrite replaced. Refuses, changing nothing, if an INI changed since.</summary>
    /// <param name="rewrite">What <see cref="RevertAsync"/> or <see cref="UseInGameDefaultsAsync"/> returned.</param>
    /// <param name="cancellationToken">Cancels before anything is written.</param>
    /// <exception cref="ModOperationException">An INI is gone or changed since, or could not be written.</exception>
    Task UndoAsync(IniRewrite rewrite, CancellationToken cancellationToken = default);

    /// <summary>Makes the same key and default changes in a new version of a mod that the old version had.</summary>
    /// <param name="fromModFolder">The old version, with its kept originals.</param>
    /// <param name="toModFolder">The new version. Its own originals are kept before its first change.</param>
    /// <param name="cancellationToken">Cancels before anything is written.</param>
    /// <returns>What was carried, what clashed with the new author's values, and what was not there to carry to.</returns>
    /// <exception cref="ModOperationException">Either folder does not exist, or an INI could not be written.</exception>
    Task<IniCarryReport> CarryAsync(string fromModFolder, string toModFolder, CancellationToken cancellationToken = default);
}

/// <summary>The default <see cref="IIniOriginalsService"/>.</summary>
public sealed class IniOriginalsService(
    IIniFileService files,
    ISavedSettingsService settings,
    IModRepository repository,
    ITrashService trash,
    ILogger logger) : IIniOriginalsService
{
    private const string ConstantsSection = "Constants";
    private const string OriginalSuffix = ".original";
    private const int MaximumOriginals = 500;

    private readonly IIniFileService _files = files;
    private readonly ISavedSettingsService _settings = settings;
    private readonly IModRepository _repository = repository;
    private readonly ITrashService _trash = trash;
    private readonly ILogger _logger = logger.ForContext<IniOriginalsService>();

    /// <inheritdoc />
    public async Task<ModIniChanges> ReadAsync(string modFolder, CancellationToken cancellationToken = default)
    {
        var root = ModInis.Root(modFolder);
        var problems = new List<string>();
        var analyses = await AnalyseAsync(root, problems, cancellationToken).ConfigureAwait(false);

        return new ModIniChanges(root, [.. analyses.Select(analysis => analysis.Changed)], problems);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChangedMod>> FindAsync(string modsDirectory, CancellationToken cancellationToken = default)
    {
        var inventory = await _repository.ScanAsync(modsDirectory, cancellationToken).ConfigureAwait(false);
        var found = new List<ChangedMod>();

        foreach (var mod in inventory.AllMods)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(OriginalsFolder(mod.Path)))
            {
                continue;
            }

            try
            {
                var changes = await ReadAsync(mod.Path, cancellationToken).ConfigureAwait(false);

                if (changes.HasChanges)
                {
                    found.Add(new ChangedMod(mod, changes));
                }
            }
            catch (ModOperationException ex)
            {
                // The mod went between the scan and the read.
                _logger.Warning(ex, "Could not compare the INIs of {Mod} with their originals", mod.Path);
            }
        }

        return found;
    }

    /// <inheritdoc />
    public async Task<IniRewrite> RevertAsync(string modFolder, IniRevertScope scope, CancellationToken cancellationToken = default)
    {
        var root = ModInis.Root(modFolder);
        var problems = new List<string>();
        var analyses = await AnalyseAsync(root, problems, cancellationToken).ConfigureAwait(false);

        if (problems.Count > 0)
        {
            throw new ModOperationException(
                $"Nothing was put back, because part of this mod could not be read: {string.Join(" ", problems)}", root);
        }

        var planned = new List<IniRewriteFile>();

        foreach (var analysis in analyses)
        {
            var wanted = analysis.Lines
                .Where(line => scope == IniRevertScope.Everything || line.Change.Kind == Kind(scope))
                .ToList();

            if (scope != IniRevertScope.Everything && wanted.Count == 0)
            {
                continue;
            }

            var after = scope == IniRevertScope.Everything
                ? analysis.OriginalBytes
                : IniEditor.Apply(analysis.Current, [.. wanted.Select(line => new IniEdit(line.Line, line.Change.Original))]);

            planned.Add(new IniRewriteFile(analysis.Path, analysis.Changed.File, analysis.Current.ToBytes(), after, wanted.Count));
        }

        if (planned.Count == 0)
        {
            return new IniRewrite(root, [], []);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var trashed = await TrashCopiesAsync(root, planned, cancellationToken).ConfigureAwait(false);

        await WriteAllAsync(planned, file => file.After, file => file.Before, root).ConfigureAwait(false);

        foreach (var file in planned)
        {
            _logger.Information("Put back {Scope} of {Path} from its kept original {Original}", scope, file.Path, ModInis.OriginalOf(root, file.Path));
        }

        return new IniRewrite(root, planned, trashed);
    }

    /// <inheritdoc />
    public async Task<IniRewrite> UseInGameDefaultsAsync(string modFolder, CancellationToken cancellationToken = default)
    {
        var root = ModInis.Root(modFolder);
        var read = await _settings.ReadAsync(root, cancellationToken).ConfigureAwait(false);
        var changing = read.Settings.Where(setting => setting.DiffersFromGame).ToList();

        if (changing.Count == 0)
        {
            return new IniRewrite(root, [], []);
        }

        var before = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var file in changing.Select(setting => setting.File).Distinct(StringComparer.Ordinal))
        {
            before[file] = await ReadBytesAsync(ModInis.Resolve(root, file), cancellationToken).ConfigureAwait(false);
        }

        await _settings.WriteAsync(
            root,
            [.. changing.Select(setting => new SavedSettingEdit(setting.File, setting.Line, setting.Default, setting.InGame!))],
            cancellationToken).ConfigureAwait(false);

        var written = new List<IniRewriteFile>();

        foreach (var (file, bytes) in before.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var path = ModInis.Resolve(root, file);
            var after = await ReadBytesAsync(path, CancellationToken.None).ConfigureAwait(false);
            written.Add(new IniRewriteFile(path, file, bytes, after, changing.Count(setting => setting.File == file)));
        }

        return new IniRewrite(root, written, []);
    }

    /// <inheritdoc />
    public async Task UndoAsync(IniRewrite rewrite, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rewrite);

        foreach (var file in rewrite.Files)
        {
            byte[] now;

            try
            {
                now = await File.ReadAllBytesAsync(file.Path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ModOperationException(
                    $"{PathDisplay.Show(file.File)} could not be read ({ex.Message}), so nothing was undone.", file.Path, ex);
            }

            if (!now.AsSpan().SequenceEqual(file.After))
            {
                throw new ModOperationException(
                    $"{PathDisplay.Show(file.File)} has changed since, and undoing would lose that change. Nothing was undone.",
                    file.Path);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        await WriteAllAsync(rewrite.Files, file => file.Before, file => file.After, rewrite.ModFolder).ConfigureAwait(false);

        _logger.Information("Undid the change to {Count} INIs in {Mod}", rewrite.Files.Count, rewrite.ModFolder);
    }

    /// <inheritdoc />
    public async Task<IniCarryReport> CarryAsync(string fromModFolder, string toModFolder, CancellationToken cancellationToken = default)
    {
        var from = ModInis.Root(fromModFolder);
        var to = ModInis.Root(toModFolder);
        var problems = new List<string>();
        var analyses = await AnalyseAsync(from, problems, cancellationToken).ConfigureAwait(false);
        var applied = new List<IniCarried>();
        var clashed = new List<IniCarried>();
        var missing = new List<IniCarried>();
        var otherLeft = new List<string>();
        var planned = new List<(string Path, IniDocument Document, List<IniEdit> Edits)>();

        foreach (var analysis in analyses)
        {
            var file = analysis.Changed.File;

            if (analysis.Changed.HasOtherChanges)
            {
                otherLeft.Add(file);
            }

            var target = Path.Combine(to, file);

            if (!PathComparer.TryResolveExisting(target, out var resolved) || !File.Exists(resolved))
            {
                missing.AddRange(analysis.Lines.Select(line => new IniCarried(file, line.Change, null)));
                continue;
            }

            IniDocument document;

            try
            {
                document = await _files.ReadAsync(resolved, ModScanBounds.Default.MaxIniBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (ModOperationException ex)
            {
                problems.Add($"{PathDisplay.Show(file)} in the new version: {ex.Message}");
                continue;
            }

            if (document.Truncated)
            {
                problems.Add($"{PathDisplay.Show(file)} in the new version is larger than {ModScanBounds.Default.MaxIniBytes / (1024 * 1024)} MB and was not changed.");
                continue;
            }

            var lines = Index(document, file);
            var edits = new List<IniEdit>();

            foreach (var (change, identity, _) in analysis.Lines)
            {
                if (!lines.TryGetValue(identity, out var found))
                {
                    missing.Add(new IniCarried(file, change, null));
                    continue;
                }

                var carried = new IniCarried(file, change, found.Value);
                (Same(change.Kind, found.Value, change.Original) ? applied : clashed).Add(carried);

                if (!Same(change.Kind, found.Value, change.Current))
                {
                    edits.Add(new IniEdit(found.Line, change.Current));
                }
            }

            if (edits.Count > 0)
            {
                planned.Add((resolved, document, edits));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        foreach (var (path, document, edits) in planned)
        {
            var bytes = IniEditor.Apply(document, edits);
            ModInis.KeepOriginal(to, path, _logger);
            await _files.WriteAsync(path, bytes, CancellationToken.None).ConfigureAwait(false);
            _logger.Information("Carried {Count} key and default changes from {From} to {Path}", edits.Count, from, path);
        }

        return new IniCarryReport(applied, clashed, missing, otherLeft, problems);
    }

    /// <summary>The folder holding a mod's kept originals.</summary>
    private static string OriginalsFolder(string modFolder) =>
        Path.Combine(modFolder, ModConfigSchema.DirectoryName, "originals");

    private static IniChangeKind Kind(IniRevertScope scope) =>
        scope == IniRevertScope.Keys ? IniChangeKind.Key : IniChangeKind.Default;

    private static bool Same(IniChangeKind kind, string left, string right) =>
        kind == IniChangeKind.Default
            ? SavedSettingValues.SameNumber(left, right)
            : string.Equals(left, right, StringComparison.Ordinal);

    /// <summary>Each INI with a kept original that differs from it, in name order.</summary>
    private async Task<List<Analysis>> AnalyseAsync(string root, List<string> problems, CancellationToken cancellationToken)
    {
        var folder = OriginalsFolder(root);
        var analyses = new List<Analysis>();

        if (!Directory.Exists(folder) || new DirectoryInfo(folder).LinkTarget is not null)
        {
            return analyses;
        }

        List<string> originals;

        try
        {
            originals = [.. Directory.EnumerateFiles(folder, "*" + OriginalSuffix, SearchOption.AllDirectories)
                .Take(MaximumOriginals)
                .Order(StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"The kept originals could not be listed: {ex.Message}");
            return analyses;
        }

        foreach (var original in originals)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(folder, original)[..^OriginalSuffix.Length].Replace('\\', '/');
            string path;

            try
            {
                path = ModInis.Resolve(root, relative);
            }
            catch (ModOperationException)
            {
                // The mod no longer has this INI; its kept original has nothing to compare with.
                continue;
            }

            try
            {
                var originalBytes = await ReadBytesAsync(original, cancellationToken).ConfigureAwait(false);
                var current = await _files.ReadAsync(path, ModScanBounds.Default.MaxIniBytes, cancellationToken).ConfigureAwait(false);

                if (current.Bytes.SequenceEqual(originalBytes))
                {
                    continue;
                }

                if (current.Truncated)
                {
                    problems.Add($"{PathDisplay.Show(relative)} is larger than {ModScanBounds.Default.MaxIniBytes / (1024 * 1024)} MB and was not compared.");
                    continue;
                }

                var author = await _files.ReadAsync(original, ModScanBounds.Default.MaxIniBytes, cancellationToken).ConfigureAwait(false);
                analyses.Add(Compare(relative, path, author, current, originalBytes));
            }
            catch (ModOperationException ex)
            {
                problems.Add($"{PathDisplay.Show(relative)}: {ex.Message}");
            }
        }

        return analyses;
    }

    /// <summary>The key and default lines that differ, matched by section and name, and whether anything else does.</summary>
    private static Analysis Compare(string relative, string path, IniDocument author, IniDocument current, byte[] originalBytes)
    {
        var authorLines = Index(author, relative);
        var currentLines = Index(current, relative);
        var lines = new List<ChangedLine>();
        var accounted = new HashSet<int>();

        foreach (var (identity, now) in currentLines.OrderBy(pair => pair.Value.Line.Number))
        {
            if (!authorLines.TryGetValue(identity, out var was))
            {
                continue;
            }

            accounted.Add(now.Line.Number);

            if (!Same(identity.Kind, was.Value, now.Value))
            {
                lines.Add(new ChangedLine(
                    new IniChange(identity.Kind, now.Section, now.Name, was.Value, now.Value), identity, now.Line));
            }
        }

        var other = author.Lines.Length != current.Lines.Length ||
                    Enumerable.Range(0, current.Lines.Length).Any(index =>
                        !accounted.Contains(current.Lines[index].Number) &&
                        !string.Equals(current.Lines[index].Text, author.Lines[index].Text, StringComparison.Ordinal));

        return new Analysis(
            new ChangedIni(relative, [.. lines.Select(line => line.Change)], other),
            [.. lines],
            path,
            current,
            originalBytes);
    }

    /// <summary>Every key binding line and saved setting in a document, by where it is rather than by line number.</summary>
    private static Dictionary<LineIdentity, IndexedLine> Index(IniDocument document, string relative)
    {
        var index = new Dictionary<LineIdentity, IndexedLine>();
        var sectionSeen = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var section in KeySwapService.Bindings(document, relative))
        {
            var sectionName = section.Section.ToLowerInvariant();
            var sectionOccurrence = sectionSeen[sectionName] = sectionSeen.GetValueOrDefault(sectionName) + 1;
            var fieldSeen = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var field in section.Fields)
            {
                var name = field.Name.ToLowerInvariant();
                var occurrence = fieldSeen[name] = fieldSeen.GetValueOrDefault(name) + 1;
                var line = document.Lines.First(candidate => candidate.Number == field.Line);

                index[new LineIdentity(IniChangeKind.Key, sectionName, sectionOccurrence, name, occurrence)] =
                    new IndexedLine(line, section.Section, field.Name, field.Value);
            }
        }

        foreach (var declaration in SavedSettingsService.Declarations(document))
        {
            index[new LineIdentity(IniChangeKind.Default, ConstantsSection, 1, declaration.Name.ToLowerInvariant(), 1)] =
                new IndexedLine(declaration.Line, ConstantsSection, declaration.Name, declaration.Default ?? "0");
        }

        return index;
    }

    /// <summary>Puts a copy of each INI as it is now into the trash, by way of a scratch folder in <c>.xxsm/</c>.</summary>
    private async Task<List<TrashResult>> TrashCopiesAsync(string root, List<IniRewriteFile> planned, CancellationToken cancellationToken)
    {
        var state = Path.Combine(root, ModConfigSchema.DirectoryName);
        var scratch = Path.Combine(state, $"reverting-{Guid.NewGuid():n}");
        var trashed = new List<TrashResult>();

        try
        {
            foreach (var file in planned)
            {
                var copy = Path.Combine(scratch, file.File);
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                await File.WriteAllBytesAsync(copy, file.Before, cancellationToken).ConfigureAwait(false);

                var result = await _trash.TrashAsync(copy, state, cancellationToken).ConfigureAwait(false);
                trashed.Add(result);
                _logger.Information("Put a copy of {Path} as it was before the revert in the trash at {Trash}", file.Path, result.TrashedPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ModOperationException)
        {
            throw new ModOperationException(
                $"A copy of the INI as it is could not be kept before putting the original back ({ex.Message}), so nothing was put back.",
                root,
                ex);
        }
        finally
        {
            OwnScratch.TryDeleteFolder(scratch, _logger);
        }

        return trashed;
    }

    /// <summary>Writes each file; if one fails, those already written get their previous contents back.</summary>
    private async Task WriteAllAsync(
        IReadOnlyList<IniRewriteFile> planned, Func<IniRewriteFile, byte[]> contents, Func<IniRewriteFile, byte[]> previous, string root)
    {
        var done = new List<IniRewriteFile>();

        try
        {
            foreach (var file in planned)
            {
                await _files.WriteAsync(file.Path, contents(file), CancellationToken.None).ConfigureAwait(false);
                done.Add(file);
            }
        }
        catch (ModOperationException)
        {
            foreach (var file in done)
            {
                try
                {
                    await _files.WriteAsync(file.Path, previous(file), CancellationToken.None).ConfigureAwait(false);
                }
                catch (ModOperationException ex)
                {
                    _logger.Error(ex, "Could not put {Path} back as it was after a failed write in {Mod}", file.Path, root);
                }
            }

            throw;
        }
    }

    private static async Task<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException($"Could not read '{PathDisplay.Show(path)}': {ex.Message}", path, ex);
        }
    }

    /// <summary>Where a line is: its kind, its section and which of that name, its name and which of that name.</summary>
    private readonly record struct LineIdentity(IniChangeKind Kind, string Section, int SectionOccurrence, string Name, int Occurrence);

    private sealed record IndexedLine(IniLine Line, string Section, string Name, string Value);

    private sealed record ChangedLine(IniChange Change, LineIdentity Identity, IniLine Line);

    private sealed record Analysis(ChangedIni Changed, IReadOnlyList<ChangedLine> Lines, string Path, IniDocument Current, byte[] OriginalBytes);
}
