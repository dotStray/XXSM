using System.Text.Json;
using Serilog;
using Xxsm.Core;
using Xxsm.Core.Archives;
using Xxsm.Core.Diagnostics;
using Xxsm.Core.Io;
using Xxsm.Packs.Loading;
using Xxsm.Packs.Model;
using Xxsm.Packs.Registry;
using Xxsm.Packs.Serialization;

namespace Xxsm.Packs.Installation;

/// <summary>Installs packs into <c>packs/&lt;gameId&gt;/&lt;packVersion&gt;/</c>, loading each first.</summary>
public sealed class PackInstaller(
    IAppPaths paths,
    IGamePackLoader loader,
    IPackPreferencesStore preferences,
    ILogger logger) : IPackInstaller
{
    private readonly IAppPaths _paths = paths;
    private readonly IGamePackLoader _loader = loader;
    private readonly IPackPreferencesStore _preferences = preferences;
    private readonly ILogger _logger = logger.ForContext<PackInstaller>();

    /// <inheritdoc />
    public async Task<PackInstallResult> InstallAsync(
        string source,
        bool overwrite = false,
        ExpectedPack? expected = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        cancellationToken.ThrowIfCancellationRequested();

        var resolvedSource = PathComparer.TryResolveExisting(source, out var found)
            ? found
            : PathComparer.Normalize(Path.GetFullPath(source));

        var isDirectory = Directory.Exists(resolvedSource);
        if (!isDirectory && !File.Exists(resolvedSource))
        {
            throw new PackLoadException($"There is nothing to import at '{PathDisplay.Show(resolvedSource)}'.", resolvedSource);
        }

        string? staging = null;
        try
        {
            var packRoot = isDirectory
                ? resolvedSource
                : FindPackRoot(staging = ExtractToStaging(resolvedSource, cancellationToken), resolvedSource);

            var pack = await _loader.LoadAsync(packRoot, cancellationToken).ConfigureAwait(false);

            if (AppVersionRequirement.IsOlderThan(AppInfo.ShortVersion, pack.Manifest.MinAppVersion))
            {
                throw new PackLoadException(
                    $"'{PathDisplay.Show(resolvedSource)}' is {pack.GameId} {pack.PackVersion}, which needs XXSM " +
                    $"{pack.Manifest.MinAppVersion} or newer, and this is {AppInfo.ShortVersion}. Nothing was installed.",
                    resolvedSource);
            }

            if (expected is not null &&
                (!string.Equals(pack.GameId, expected.GameId, StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(pack.PackVersion, expected.PackVersion, StringComparison.Ordinal)))
            {
                throw new PackLoadException(
                    $"'{PathDisplay.Show(resolvedSource)}' was meant to be {expected.GameId} {expected.PackVersion}, but it holds " +
                    $"{pack.GameId} {pack.PackVersion}. Nothing was installed.",
                    resolvedSource);
            }

            var packsDirectory = PathComparer.Normalize(Path.GetFullPath(_paths.PacksDirectory));
            var destination = PathComparer.Normalize(Path.GetFullPath(
                Path.Combine(packsDirectory, pack.GameId, pack.PackVersion)));

            // The last word before a recursive delete, so it does not rely on the loader's check.
            if (!UntrustedLocation.IsSameOrUnderExactly(packsDirectory, destination) ||
                PathComparer.AreEqual(packsDirectory, destination))
            {
                throw new PackLoadException(
                    $"{pack.GameId} {pack.PackVersion} would be installed at '{PathDisplay.Show(destination)}', outside " +
                    $"'{PathDisplay.Show(packsDirectory)}'. Nothing was installed.",
                    resolvedSource);
            }

            if (Directory.Exists(destination) && !overwrite)
            {
                throw new PackLoadException(
                    $"{pack.GameId} {pack.PackVersion} is already installed at '{PathDisplay.Show(destination)}'. " +
                    "Re-import with overwrite to replace it.",
                    destination);
            }

            PutInPlace(packRoot, destination, $"{pack.GameId} {pack.PackVersion}", cancellationToken);

            _logger.Information(
                "Installed pack {GameId} {PackVersion} from {Source} to {Destination}: " +
                "{Variants} variants, {Hashes} hash entries",
                pack.GameId,
                pack.PackVersion,
                resolvedSource,
                destination,
                pack.Variants.Count,
                pack.Hashes?.Entries?.Count ?? 0);

            return new PackInstallResult(
                pack.GameId,
                pack.PackVersion,
                destination,
                pack.Variants.Count,
                pack.Hashes?.Entries?.Count ?? 0,
                pack.Diagnostics);
        }
        finally
        {
            if (staging is not null)
            {
                OwnScratch.TryDeleteFolder(staging, _logger);
            }
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<InstalledPack>> ListInstalledAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var installed = new List<InstalledPack>();

        if (!Directory.Exists(_paths.PacksDirectory))
        {
            return Task.FromResult<IReadOnlyList<InstalledPack>>(installed);
        }

        foreach (var gameDirectory in Directory.EnumerateDirectories(_paths.PacksDirectory))
        {
            var gameId = Path.GetFileName(gameDirectory);

            foreach (var versionDirectory in Directory.EnumerateDirectories(gameDirectory))
            {
                if (IsWorkingFolder(Path.GetFileName(versionDirectory)) ||
                    !File.Exists(Path.Combine(versionDirectory, PackSchema.ManifestFile)))
                {
                    continue;
                }

                installed.Add(new InstalledPack(
                    gameId,
                    Path.GetFileName(versionDirectory),
                    PathComparer.Normalize(versionDirectory))
                {
                    DisplayName = TryReadDisplayName(versionDirectory),
                });
            }
        }

        // Newest first by PackVersionOrder, which handles dated and numbered versions.
        return Task.FromResult<IReadOnlyList<InstalledPack>>(
        [
            .. installed
                .OrderBy(p => p.GameId, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(p => p.PackVersion, PackVersionOrder.Instance),
        ]);
    }

    /// <summary>Reads a game's display name from an installed pack's <c>game.json</c>, or null, quietly.</summary>
    private string? TryReadDisplayName(string packDirectory)
    {
        var path = Path.Combine(packDirectory, PackSchema.GameFile);

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var game = JsonSerializer.Deserialize(
                File.ReadAllText(path), PackJsonContext.Default.GameDefinition);

            return game?.DisplayName is { Length: > 0 } name ? name : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.Debug(ex, "Could not read the display name from {Path}", path);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<string?> FindActivePackDirectoryAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        var installed = (await ListInstalledAsync(cancellationToken).ConfigureAwait(false))
            .Where(p => string.Equals(p.GameId, gameId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (installed.Count == 0)
        {
            return null;
        }

        var preferences = await _preferences.ReadAsync(cancellationToken).ConfigureAwait(false);
        var active = ActivePackVersion.Choose(
            [.. installed.Select(p => p.PackVersion)], preferences.ForGame(gameId).PinnedVersion);

        return installed.First(p => string.Equals(p.PackVersion, active, StringComparison.OrdinalIgnoreCase)).Directory;
    }

    /// <summary>Extracts an archive into a fresh temporary directory, refusing any entry that escapes it.</summary>
    private string ExtractToStaging(string archivePath, CancellationToken cancellationToken)
    {
        var staging = PathComparer.Normalize(Path.Combine(
            _paths.CacheDirectory,
            ImportStagingFolder,
            Guid.NewGuid().ToString("N")));

        Directory.CreateDirectory(staging);

        try
        {
            var diagnostics = new List<Diagnostic>();
            var (_, _, reached) = ArchiveUnpacker.Unpack(archivePath, staging, PackBounds, diagnostics, cancellationToken);

            if (reached != ArchiveLimit.None)
            {
                throw new PackLoadException(
                    $"'{PathDisplay.Show(archivePath)}' is larger than any Game Pack should be " +
                    $"({PackBounds.MaxEntries} files, {PackBounds.MaxTotalBytes / (1024 * 1024)} MB). It was not imported.",
                    archivePath);
            }

            foreach (var diagnostic in diagnostics)
            {
                _logger.Warning("While unpacking {Archive}: {Message}", archivePath, diagnostic.Message);
            }

            return staging;
        }
        catch (ModOperationException ex)
        {
            OwnScratch.TryDeleteFolder(staging, _logger);
            throw new PackLoadException(ex.Message, archivePath, ex);
        }
        catch
        {
            OwnScratch.TryDeleteFolder(staging, _logger);
            throw;
        }
    }

    /// <summary>A pack's limits: the real ones are a few megabytes and a few hundred files.</summary>
    private static readonly ArchiveBounds PackBounds = new()
    {
        MaxEntries = 20_000,
        MaxEntryBytes = 64L * 1024 * 1024,
        MaxTotalBytes = 512L * 1024 * 1024,
    };

    /// <summary>Finds the directory holding <c>manifest.json</c>, often nested one level down.</summary>
    private static string FindPackRoot(string staging, string archivePath)
    {
        if (File.Exists(Path.Combine(staging, PackSchema.ManifestFile)))
        {
            return staging;
        }

        var candidates = Directory
            .EnumerateFiles(staging, PackSchema.ManifestFile, FileTree.WithoutLinks)
            .OrderBy(p => p.Count(c => c is '/' or '\\'))
            .ToList();

        if (candidates.Count == 0)
        {
            throw new PackLoadException(
                $"'{PathDisplay.Show(archivePath)}' does not contain a {PackSchema.ManifestFile}, so it is not a Game Pack.",
                archivePath);
        }

        return PathComparer.Normalize(Path.GetDirectoryName(candidates[0])!);
    }

    /// <summary>Copies a verified pack into place, replacing an installed one only once the copy is whole.</summary>
    private void PutInPlace(string source, string destination, string description, CancellationToken cancellationToken)
    {
        var gameDirectory = Path.GetDirectoryName(destination)!;
        var incoming = Path.Combine(gameDirectory, $"{IncomingPrefix}{Guid.NewGuid():n}");
        var created = false;

        try
        {
            Directory.CreateDirectory(gameDirectory);

            if (Path.Exists(incoming))
            {
                throw new IOException($"'{incoming}' already exists.");
            }

            created = true;
            CopyTree(source, incoming, cancellationToken);

            if (!Directory.Exists(destination))
            {
                Directory.Move(incoming, destination);
                return;
            }

            var replaced = Path.Combine(gameDirectory, $"{ReplacedPrefix}{Guid.NewGuid():n}");
            Directory.Move(destination, replaced);

            try
            {
                Directory.Move(incoming, destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try
                {
                    Directory.Move(replaced, destination);
                }
                catch (Exception restore) when (restore is IOException or UnauthorizedAccessException)
                {
                    _logger.Error(restore, "Could not put the installed {Pack} back from {Replaced}", description, replaced);

                    throw new PackLoadException(
                        $"Could not install {description}: {ex.Message} The copy that was installed is " +
                        $"now at '{PathDisplay.Show(replaced)}', and putting it back at '{PathDisplay.Show(destination)}' failed: {restore.Message}",
                        replaced,
                        ex);
                }

                throw;
            }

            _logger.Information("Replaced the installed {Pack} at {Destination}", description, destination);

            // Directory delete on purpose: XXSM's own copy of a pack, never user content.
            OwnScratch.TryDeleteFolder(replaced, _logger);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (created)
            {
                OwnScratch.TryDeleteFolder(incoming, _logger);
            }

            throw new PackLoadException(
                $"Could not install {description}: {ex.Message} Anything already installed at " +
                $"'{PathDisplay.Show(destination)}' was left as it was.",
                destination,
                ex);
        }
        catch (OperationCanceledException)
        {
            if (created)
            {
                OwnScratch.TryDeleteFolder(incoming, _logger);
            }

            throw;
        }
    }

    /// <summary>Whether a folder is an install's working folder; its name has a space, which no version may.</summary>
    internal static bool IsWorkingFolder(string name) =>
        name.StartsWith(IncomingPrefix, StringComparison.Ordinal) ||
        name.StartsWith(ReplacedPrefix, StringComparison.Ordinal);

    private const string IncomingPrefix = ".xxsm-install ";

    /// <summary>The folder under the cache a pack archive is unpacked into before it is checked.</summary>
    internal const string ImportStagingFolder = "import";

    private const string ReplacedPrefix = ".xxsm-replaced ";

    private static void CopyTree(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);

        foreach (var directory in FileTree.Directories(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = PathComparer.TryGetRelativePath(source, directory);
            if (relative is not null)
            {
                Directory.CreateDirectory(Path.Combine(destination, relative));
            }
        }

        foreach (var file in FileTree.Files(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = PathComparer.TryGetRelativePath(source, file);
            if (relative is not null)
            {
                File.Copy(file, Path.Combine(destination, relative), overwrite: true);
            }
        }
    }
}
