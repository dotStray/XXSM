using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Serilog;
using Xxsm.Core;
using Xxsm.Core.Diagnostics;
using Xxsm.Core.Io;
using Xxsm.Core.Text;
using Xxsm.Packs.Model;

namespace Xxsm.Packs.Studio;

/// <summary>What an export wrote.</summary>
/// <param name="PackFile">The pack zip.</param>
/// <param name="GameId">The game.</param>
/// <param name="PackVersion">The version it was exported as.</param>
/// <param name="Sha256">The zip's SHA-256, lower-case hex — what a registry publishes beside it.</param>
/// <param name="SizeBytes">The zip's size.</param>
/// <param name="VariantCount">How many variants it holds.</param>
/// <param name="HashEntryCount">How many hash entries it holds.</param>
/// <param name="ImageCount">How many pictures it carries.</param>
/// <param name="Validation">The checks it passed, warnings included.</param>
public sealed record PackExportResult(
    string PackFile,
    string GameId,
    string PackVersion,
    string Sha256,
    long SizeBytes,
    int VariantCount,
    int HashEntryCount,
    int ImageCount,
    PackValidationResult Validation);

/// <summary>Writes a draft out as a pack zip, the one file anyone needs to install it.</summary>
public interface IPackExporter
{
    /// <summary>Exports a draft.</summary>
    /// <param name="draft">The draft. Its pictures are read from its folder.</param>
    /// <param name="outputDirectory">Where to write the zip. Created when missing.</param>
    /// <param name="packVersion">The version to export as, or null for the draft's own.</param>
    /// <param name="overwrite">Whether to replace a file of the same name already there.</param>
    /// <param name="cancellationToken">Cancels the export. Nothing is left behind.</param>
    /// <exception cref="ModOperationException">The draft has blocking problems, the version is invalid, the file
    /// exists and may not be replaced, or the zip could not be written.</exception>
    Task<PackExportResult> ExportAsync(
        PackDraft draft,
        string outputDirectory,
        string? packVersion = null,
        bool overwrite = false,
        CancellationToken cancellationToken = default);
}

/// <summary>The default <see cref="IPackExporter"/>: byte-identical for an unchanged draft.</summary>
public sealed class PackExporter(IStudioDraftStore store, ILogger logger) : IPackExporter
{
    /// <summary>The fixed timestamp every zip entry is given, the earliest a zip can record.</summary>
    public static DateTimeOffset EntryTimestamp { get; } = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IStudioDraftStore _store = store;
    private readonly ILogger _logger = logger.ForContext<PackExporter>();

    /// <summary>The file name a pack is exported under.</summary>
    public static string FileNameFor(string gameId, string packVersion) => $"{gameId}-{packVersion}.zip";

    /// <inheritdoc />
    public async Task<PackExportResult> ExportAsync(
        PackDraft draft,
        string outputDirectory,
        string? packVersion = null,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var version = string.IsNullOrWhiteSpace(packVersion) ? draft.Manifest.PackVersion : packVersion.Trim();

        if (!PackDrafts.IsValidVersion(version))
        {
            throw new ModOperationException(
                $"'{version}' cannot be a pack version — only letters, digits, dots, hyphens and underscores are " +
                "allowed, because it becomes a folder name when the pack is installed.");
        }

        var directory = _store.GetDraftDirectory(draft.GameId);
        var validation = PackValidator.Validate(draft, imageBytes: path => SizeInside(directory, path));

        if (!validation.CanExport)
        {
            var first = validation.Problems.First(p => p.Severity == DiagnosticSeverity.Error);

            throw new ModOperationException(
                $"This pack cannot be exported until {EnglishCount.Plural(validation.ErrorCount, "problem is", "problems are")} " +
                $"fixed. The first: {first.Message}",
                directory);
        }

        var images = ReferencedImages(draft)
            .Select(relative => (Relative: relative, Full: ResolveInside(directory, relative)))
            .Where(image => image.Full is not null)
            .OrderBy(image => image.Relative, StringComparer.Ordinal)
            .ToList();

        var exported = Prepare(draft, version, images.Count);
        var files = PackFiles.Render(exported);

        var output = PathComparer.Normalize(Path.GetFullPath(outputDirectory));
        var target = Path.Combine(output, FileNameFor(draft.GameId, version));

        if (File.Exists(target) && !overwrite)
        {
            throw new ModOperationException(
                $"There is already a file at '{PathDisplay.Show(target)}'. Choose another folder, or allow it to be replaced.",
                target);
        }

        try
        {
            Directory.CreateDirectory(output);

            await AtomicFile.WriteAsync(
                    target,
                    async (stream, token) =>
                    {
                        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);

                        foreach (var (name, text) in files)
                        {
                            token.ThrowIfCancellationRequested();

                            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                            entry.LastWriteTime = EntryTimestamp;

                            await using var writer = entry.Open();
                            await writer.WriteAsync(Utf8NoBom.GetBytes(text), token).ConfigureAwait(false);
                        }

                        foreach (var (relative, full) in images)
                        {
                            token.ThrowIfCancellationRequested();

                            var entry = zip.CreateEntry(relative, CompressionLevel.Optimal);
                            entry.LastWriteTime = EntryTimestamp;

                            await using var writer = entry.Open();
                            await using var source = File.OpenRead(full!);
                            await source.CopyToAsync(writer, token).ConfigureAwait(false);
                        }
                    },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException($"Could not write the pack to '{PathDisplay.Show(target)}': {ex.Message}", target, ex);
        }

        string sha256;
        long size;

        await using (var written = File.OpenRead(target))
        {
            sha256 = Convert.ToHexStringLower(
                await SHA256.HashDataAsync(written, cancellationToken).ConfigureAwait(false));
            size = written.Length;
        }

        _logger.Information(
            "Exported {GameId} {PackVersion} to {Path}: {Variants} variants, {Hashes} hash entries, {Images} pictures, " +
            "sha256 {Sha256}",
            draft.GameId,
            version,
            target,
            exported.Variants.Count,
            exported.Hashes.Entries?.Count ?? 0,
            images.Count,
            sha256);

        return new PackExportResult(
            target,
            draft.GameId,
            version,
            sha256,
            size,
            exported.Variants.Count,
            exported.Hashes.Entries?.Count ?? 0,
            images.Count,
            validation);
    }

    /// <summary>The draft as it goes into the zip: stamped as Studio's, with its counts.</summary>
    private static PackDraft Prepare(PackDraft draft, string version, int imageCount)
    {
        var counts = new Dictionary<string, int>
        {
            ["variants"] = draft.Variants.Count,
            ["skins"] = draft.Variants.Count(v => !string.IsNullOrWhiteSpace(v.BaseCharacterId)),
            ["images"] = imageCount,
        };

        return draft with
        {
            Manifest = draft.Manifest with
            {
                PackSchemaVersion = PackSchema.SupportedVersions.Max(),
                GameId = draft.GameId,
                PackVersion = version,
                Builder = $"{PackDrafts.Builder} {AppInfo.ShortVersion}",
                AuthoredBy = PackDrafts.AuthoredByUser,
                Counts = counts,
            },
            Game = draft.Game with { GameId = draft.GameId },
        };
    }

    /// <summary>Every picture path the pack refers to that lives inside the pack.</summary>
    private static IEnumerable<string> ReferencedImages(PackDraft draft)
    {
        var attributeIcons = (draft.Game.Attributes ?? new Dictionary<string, AttributeDefinition>())
            .Values
            .SelectMany(a => (a.Values ?? []).Select(v => v.Icon));

        return draft.Variants
            .Select(v => v.Image)
            .Append(draft.Game.Icon)
            .Concat(attributeIcons)
            .Where(IsPackRelative)
            .Select(path => path!.Trim())
            .Distinct(StringComparer.Ordinal);
    }

    /// <summary>Whether a picture path points inside the pack: a colon marks a web, file or drive path.</summary>
    private static bool IsPackRelative(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && !path.Contains(':', StringComparison.Ordinal)
        && !Path.IsPathRooted(path);

    private static string? ResolveInside(string directory, string relative)
    {
        if (!IsPackRelative(relative))
        {
            return null;
        }

        var full = PathComparer.Normalize(Path.GetFullPath(Path.Combine(directory, relative)));

        // A path that climbs out of the draft folder is not the pack's to carry.
        if (!UntrustedLocation.IsSameOrUnderExactly(directory, full))
        {
            return null;
        }

        return PathComparer.TryResolveExisting(full, out var found) && File.Exists(found) ? found : null;
    }

    private static long? SizeInside(string directory, string relative) =>
        ResolveInside(directory, relative) is { } found ? new FileInfo(found).Length : null;
}
