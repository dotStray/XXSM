using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;
using Xxsm.Core;
using Xxsm.Core.Io;
using Xxsm.Packs.Model;
using Xxsm.Packs.Registry;
using Xxsm.Packs.Serialization;

namespace Xxsm.Packs.Studio;

/// <summary>What adding a pack to a registry folder did.</summary>
/// <param name="IndexPath">The registry's <c>index.json</c>.</param>
/// <param name="PackPath">Where the pack zip now sits inside the registry folder.</param>
/// <param name="Url">The pack's address as the index gives it, relative to the index.</param>
/// <param name="ReplacedVersion">Whether the index already listed this version, and it was replaced.</param>
public sealed record RegistryPublishResult(string IndexPath, string PackPath, string Url, bool ReplacedVersion);

/// <summary>What installing a draft on this machine did.</summary>
/// <param name="PackVersion">The version it was installed as.</param>
/// <param name="Install">What the installer did, including anything an update withheld.</param>
/// <param name="Validation">The draft's problems, as the export saw them.</param>
/// <param name="SizeBytes">How big the pack was.</param>
public sealed record StudioInstallResult(
    string PackVersion,
    PackOperationResult Install,
    PackValidationResult Validation,
    long SizeBytes);

/// <summary>What writing a contribution folder did.</summary>
/// <param name="Directory">The folder.</param>
/// <param name="PackPath">The pack zip inside it.</param>
/// <param name="ReadmePath">The README that says what the pack is and how to offer it upstream.</param>
public sealed record ContributionResult(string Directory, string PackPath, string ReadmePath);

/// <summary>The publish helper: puts an exported pack where other people can get it.</summary>
public interface IPackPublisher
{
    /// <summary>Adds an exported pack to a registry folder, writing or updating its <c>index.json</c>.</summary>
    /// <param name="draft">The draft the pack was exported from, for the game's name.</param>
    /// <param name="exported">What the export wrote.</param>
    /// <param name="registryDirectory">The registry folder. Created when missing.</param>
    /// <param name="changelog">What changed in this version, or null.</param>
    /// <param name="cancellationToken">Cancels before the index is written.</param>
    /// <exception cref="ModOperationException">The exported zip is gone or has changed, or a file could not be
    /// written.</exception>
    /// <exception cref="PackLoadException">The folder has an <c>index.json</c> XXSM cannot read; it is left
    /// alone.</exception>
    Task<RegistryPublishResult> AddToRegistryAsync(
        PackDraft draft,
        PackExportResult exported,
        string registryDirectory,
        string? changelog = null,
        CancellationToken cancellationToken = default);

    /// <summary>Writes a folder with the pack, a one-pack <c>index.json</c> and a README on offering it.</summary>
    /// <param name="draft">The draft the pack was exported from.</param>
    /// <param name="exported">What the export wrote.</param>
    /// <param name="outputDirectory">Where to create the folder.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <exception cref="ModOperationException">The zip is gone or has changed, the folder is not empty, or a file
    /// could not be written.</exception>
    Task<ContributionResult> WriteContributionAsync(
        PackDraft draft,
        PackExportResult exported,
        string outputDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>Installs a draft into this XXSM in one step: export, install as any pack, delete the zip.</summary>
    /// <param name="draft">The draft to install.</param>
    /// <param name="packVersion">The version to install as, or null for the draft's own.</param>
    /// <param name="cancellationToken">Cancels before anything is installed.</param>
    /// <exception cref="ModOperationException">The draft has blocking problems, or the zip could not be
    /// written.</exception>
    /// <exception cref="PackRegistryException">The pack could not be installed.</exception>
    Task<StudioInstallResult> InstallHereAsync(
        PackDraft draft,
        string? packVersion = null,
        CancellationToken cancellationToken = default);
}

/// <summary>The default <see cref="IPackPublisher"/>; an existing index is read first, refused if unreadable.</summary>
public sealed class PackPublisher(
    IPackExporter exporter,
    IPackService packs,
    IAppPaths paths,
    TimeProvider time,
    ILogger logger) : IPackPublisher
{
    /// <summary>A registry's index file.</summary>
    public const string IndexFile = "index.json";

    /// <summary>The folder inside a registry that pack zips go in.</summary>
    public const string PacksFolder = "packs";

    /// <summary>The README in a contribution folder.</summary>
    public const string ReadmeFile = "README.md";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IPackExporter _exporter = exporter;
    private readonly IPackService _packs = packs;
    private readonly IAppPaths _paths = paths;
    private readonly TimeProvider _time = time;
    private readonly ILogger _logger = logger.ForContext<PackPublisher>();

    /// <inheritdoc />
    public async Task<StudioInstallResult> InstallHereAsync(
        PackDraft draft,
        string? packVersion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var staging = Path.Combine(_paths.CacheDirectory, "studio-install");

        var exported = await _exporter
            .ExportAsync(draft, staging, packVersion, overwrite: true, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            // Its own version, replaced without asking: reinstalling a draft is the ordinary way of working.
            var installed = await _packs
                .ImportAsync(exported.PackFile, overwrite: true, cancellationToken)
                .ConfigureAwait(false);

            _logger.Information(
                "Installed draft {GameId} {Version} from Studio into {Directory}",
                draft.GameId,
                exported.PackVersion,
                installed.Directory ?? "(unknown)");

            return new StudioInstallResult(
                exported.PackVersion, installed, exported.Validation, exported.SizeBytes);
        }
        finally
        {
            OwnScratch.TryDeleteFile(exported.PackFile, _logger);
        }
    }

    /// <inheritdoc />
    public async Task<RegistryPublishResult> AddToRegistryAsync(
        PackDraft draft,
        PackExportResult exported,
        string registryDirectory,
        string? changelog = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(exported);
        ArgumentException.ThrowIfNullOrWhiteSpace(registryDirectory);

        await VerifyAsync(exported, cancellationToken).ConfigureAwait(false);

        var root = PathComparer.Normalize(Path.GetFullPath(registryDirectory));
        var indexPath = Path.Combine(root, IndexFile);
        var existing = await ReadIndexAsync(indexPath, cancellationToken).ConfigureAwait(false);

        var fileName = Path.GetFileName(exported.PackFile);
        var packPath = Path.Combine(root, PacksFolder, fileName);
        var url = $"{PacksFolder}/{fileName}";

        await CopyAsync(exported.PackFile, packPath, cancellationToken).ConfigureAwait(false);

        var packs = (existing?.Packs ?? []).ToList();
        var index = packs.FindIndex(p => Same(p.GameId, exported.GameId));
        var previous = index >= 0 ? packs[index] : null;
        var replaced = previous?.Versions.Any(v => Same(v.PackVersion, exported.PackVersion)) == true;

        var versions = (previous?.Versions ?? [])
            .Where(v => !Same(v.PackVersion, exported.PackVersion))
            .Append(VersionEntry(draft, exported, url, changelog))
            .OrderByDescending(v => v.PackVersion, PackVersionOrder.Instance)
            .ToList();

        var entry = new RegistryPack
        {
            GameId = exported.GameId,
            DisplayName = draft.Game.DisplayName,
            ShortName = draft.Game.ShortName,
            Importer = draft.Game.Importer,
            Icon = previous?.Icon,
            Versions = versions,
        };

        if (index >= 0)
        {
            packs[index] = entry;
        }
        else
        {
            packs.Add(entry);
        }

        var updated = (existing ?? new RegistryIndex()) with
        {
            UpdatedAt = _time.GetUtcNow(),
            Packs = [.. packs.OrderBy(p => p.GameId, StringComparer.OrdinalIgnoreCase)],
        };

        await WriteAtomicallyAsync(
                indexPath,
                JsonSerializer.Serialize(updated, PackJsonContext.Default.RegistryIndex) + "\n",
                cancellationToken)
            .ConfigureAwait(false);

        _logger.Information(
            "Added {GameId} {PackVersion} to the registry in {Root} ({Action}); the pack is at {PackPath}",
            exported.GameId,
            exported.PackVersion,
            root,
            replaced ? "replaced the same version" : "new version",
            packPath);

        return new RegistryPublishResult(indexPath, packPath, url, replaced);
    }

    /// <inheritdoc />
    public async Task<ContributionResult> WriteContributionAsync(
        PackDraft draft,
        PackExportResult exported,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(exported);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        await VerifyAsync(exported, cancellationToken).ConfigureAwait(false);

        var directory = Path.Combine(
            PathComparer.Normalize(Path.GetFullPath(outputDirectory)),
            $"{exported.GameId}-{exported.PackVersion}-contribution");

        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
        {
            throw new ModOperationException(
                $"There is already a folder with things in it at '{PathDisplay.Show(directory)}'. Choose another place.", directory);
        }

        var fileName = Path.GetFileName(exported.PackFile);
        var packPath = Path.Combine(directory, fileName);
        var readmePath = Path.Combine(directory, ReadmeFile);

        await CopyAsync(exported.PackFile, packPath, cancellationToken).ConfigureAwait(false);

        var index = new RegistryIndex
        {
            UpdatedAt = _time.GetUtcNow(),
            Packs =
            [
                new RegistryPack
                {
                    GameId = exported.GameId,
                    DisplayName = draft.Game.DisplayName,
                    ShortName = draft.Game.ShortName,
                    Importer = draft.Game.Importer,
                    Versions = [VersionEntry(draft, exported, fileName, changelog: null)],
                },
            ],
        };

        await WriteAtomicallyAsync(
                Path.Combine(directory, IndexFile),
                JsonSerializer.Serialize(index, PackJsonContext.Default.RegistryIndex) + "\n",
                cancellationToken)
            .ConfigureAwait(false);

        await WriteAtomicallyAsync(readmePath, Readme(draft, exported, fileName), cancellationToken).ConfigureAwait(false);

        _logger.Information(
            "Wrote a contribution folder for {GameId} {PackVersion} at {Directory}", exported.GameId, exported.PackVersion, directory);

        return new ContributionResult(directory, packPath, readmePath);
    }

    private static RegistryPackVersion VersionEntry(PackDraft draft, PackExportResult exported, string url, string? changelog) => new()
    {
        PackVersion = exported.PackVersion,
        PackSchemaVersion = PackSchema.SupportedVersions.Max(),
        MinAppVersion = draft.Manifest.MinAppVersion,
        Url = url,
        Sha256 = exported.Sha256,
        SizeBytes = exported.SizeBytes,
        Changelog = string.IsNullOrWhiteSpace(changelog) ? null : changelog.Trim(),
    };

    /// <summary>Refuses a zip that is gone or no longer the one that was exported.</summary>
    private static async Task VerifyAsync(PackExportResult exported, CancellationToken cancellationToken)
    {
        if (!File.Exists(exported.PackFile))
        {
            throw new ModOperationException(
                $"The exported pack at '{PathDisplay.Show(exported.PackFile)}' is not there any more. Export it again.", exported.PackFile);
        }

        string actual;

        try
        {
            await using var stream = File.OpenRead(exported.PackFile);
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException($"Could not read '{PathDisplay.Show(exported.PackFile)}': {ex.Message}", exported.PackFile, ex);
        }

        if (!string.Equals(actual, exported.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ModOperationException(
                $"'{PathDisplay.Show(exported.PackFile)}' has changed since it was exported, so its checksum would be wrong. Export it again.",
                exported.PackFile);
        }
    }

    private static async Task<RegistryIndex?> ReadIndexAsync(string indexPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(indexPath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(indexPath);
            return await JsonSerializer.DeserializeAsync(stream, PackJsonContext.Default.RegistryIndex, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new PackLoadException(
                $"'{PathDisplay.Show(indexPath)}' is not a registry index XXSM can read: {ex.Message}. Nothing was changed.", indexPath, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PackLoadException($"Could not read '{PathDisplay.Show(indexPath)}': {ex.Message}", indexPath, ex);
        }
    }

    private static async Task CopyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        if (PathComparer.AreEqual(source, destination))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            await using var input = File.OpenRead(source);

            await AtomicFile.WriteAsync(destination, input.CopyToAsync, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException($"Could not copy the pack to '{PathDisplay.Show(destination)}': {ex.Message}", destination, ex);
        }
    }

    private static async Task WriteAtomicallyAsync(string path, string contents, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            await AtomicFile.WriteAllTextAsync(
                    path, contents, new AtomicWriteOptions { BackupPath = path + ".bak" }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException($"Could not write '{PathDisplay.Show(path)}': {ex.Message}. The previous file has not been changed.", path, ex);
        }
    }

    private static string Readme(PackDraft draft, PackExportResult exported, string fileName)
    {
        var outfits = draft.Variants.Count(v => !string.IsNullOrWhiteSpace(v.BaseCharacterId));
        var text = new StringBuilder();

        text.Append(CultureInfo.InvariantCulture, $"# {draft.Game.DisplayName} Game Pack {exported.PackVersion}\n\n");
        text.Append(CultureInfo.InvariantCulture, $"Made with {PackDrafts.Builder}.\n\n");
        text.Append("| | |\n|---|---|\n");
        text.Append(CultureInfo.InvariantCulture, $"| Game id | `{exported.GameId}` |\n");
        text.Append(CultureInfo.InvariantCulture, $"| Version | `{exported.PackVersion}` |\n");
        text.Append(CultureInfo.InvariantCulture, $"| Variants | {draft.Variants.Count} ({outfits} of them outfits) |\n");
        text.Append(CultureInfo.InvariantCulture, $"| Hash entries | {exported.HashEntryCount} |\n");
        text.Append(CultureInfo.InvariantCulture, $"| Pictures | {exported.ImageCount} |\n");
        text.Append(CultureInfo.InvariantCulture, $"| Warnings when exported | {exported.Validation.WarningCount} |\n");
        text.Append(CultureInfo.InvariantCulture, $"| SHA-256 | `{exported.Sha256}` |\n\n");

        text.Append("## Using it\n\n");
        text.Append(CultureInfo.InvariantCulture, $"Install `{fileName}` with `xxsm pack import {fileName}`, or add this folder as a pack ");
        text.Append("source on XXSM's Packs page: it holds an `index.json`, so it is a registry of its own.\n\n");

        text.Append("## Offering it upstream\n\n");
        text.Append("To have it listed in XXSM's default registry, open an issue at\n");
        text.Append(CultureInfo.InvariantCulture, $"<{AppInfo.PresetsRepositoryUrl}/issues> and attach `{fileName}`, ");
        text.Append("or open a pull request that adds it. Say where its data came from — the hash ");
        text.Append("repository, the portraits, anything you did not make yourself.\n");

        return text.ToString();
    }

    private static bool Same(string? one, string? two) => string.Equals(one, two, StringComparison.OrdinalIgnoreCase);
}
