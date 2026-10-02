using System.Globalization;
using System.Text.Json;
using Serilog;
using Xxsm.Core;
using Xxsm.Core.Diagnostics;
using Xxsm.Core.Io;
using Xxsm.Packs.Model;
using Xxsm.Packs.Serialization;

namespace Xxsm.Packs.Loading;

/// <summary>The one loader for every pack, official or Studio-authored.</summary>
/// <remarks>What makes a pack unreadable throws; what leaves it usable becomes a diagnostic.</remarks>
public sealed class GamePackLoader(ILogger logger) : IGamePackLoader
{
    private readonly ILogger _logger = logger.ForContext<GamePackLoader>();

    /// <inheritdoc />
    public async Task<GamePack> LoadAsync(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var root = PathComparer.TryResolveExisting(directory, out var resolved)
            ? resolved
            : PathComparer.Normalize(Path.GetFullPath(directory));

        if (!Directory.Exists(root))
        {
            throw new PackLoadException($"There is no pack directory at '{PathDisplay.Show(root)}'.", root);
        }

        var diagnostics = new List<PackDiagnostic>();

        var manifest = await ReadRequiredAsync(
            root, PackSchema.ManifestFile, PackJsonContext.Default.PackManifest, cancellationToken)
            .ConfigureAwait(false);

        ValidateManifest(manifest, root);

        var game = await ReadRequiredAsync(
            root, PackSchema.GameFile, PackJsonContext.Default.GameDefinition, cancellationToken)
            .ConfigureAwait(false);

        var entries = await ReadRequiredAsync(
            root, PackSchema.VariantsFile, PackJsonContext.Default.IReadOnlyListPackVariant, cancellationToken)
            .ConfigureAwait(false);

        var hashes = await ReadOptionalAsync(
            root, PackSchema.HashesFile, PackJsonContext.Default.HashIndexFile, cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(game.GameId) &&
            !string.Equals(game.GameId, manifest.GameId, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new PackDiagnostic(
                DiagnosticSeverity.Warning,
                PackDiagnosticCodes.GameIdMismatch,
                $"game.json says the game is '{game.GameId}' but manifest.json says '{manifest.GameId}'. " +
                "The manifest wins.",
                PackSchema.GameFile));
        }

        var variants = Validate([.. entries], hashes, diagnostics);

        _logger.Information(
            "Loaded pack {GameId} {PackVersion} from {Directory}: {Variants} variants, " +
            "{Hashes} hash entries, {Errors} errors, {Warnings} warnings",
            manifest.GameId,
            manifest.PackVersion,
            root,
            variants.Count,
            hashes?.Entries?.Count ?? 0,
            diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error),
            diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning));

        return new GamePack(
            root,
            manifest,
            game,
            variants,
            hashes,
            [.. diagnostics.OrderByDescending(d => d.Severity)]);
    }

    private static void ValidateManifest(PackManifest manifest, string root)
    {
        if (string.IsNullOrWhiteSpace(manifest.GameId))
        {
            throw new PackLoadException(
                $"The pack at '{PathDisplay.Show(root)}' has no gameId in its manifest, so XXSM cannot tell which game it is for.",
                root);
        }

        // Both name folders once installed, so anything that could climb out is refused here.
        if (!IsValidId(manifest.GameId))
        {
            throw new PackLoadException(
                $"The pack at '{PathDisplay.Show(root)}' has the gameId '{manifest.GameId}' in its manifest. A gameId may " +
                "hold only letters, digits, underscores and hyphens, because it names a folder.",
                root);
        }

        if (string.IsNullOrWhiteSpace(manifest.PackVersion))
        {
            throw new PackLoadException(
                $"The pack at '{PathDisplay.Show(root)}' has no packVersion in its manifest, so XXSM cannot tell which version it is.",
                root);
        }

        if (!IsValidVersion(manifest.PackVersion))
        {
            throw new PackLoadException(
                $"The pack at '{PathDisplay.Show(root)}' has the version '{manifest.PackVersion}' in its manifest. A pack " +
                "version may hold only letters, digits, dots, hyphens and underscores, because it names a folder.",
                root);
        }

        if (!PackSchema.IsSupported(manifest.PackSchemaVersion))
        {
            var supported = string.Join(
                ", ", PackSchema.SupportedVersions.Order().Select(v => v.ToString(CultureInfo.InvariantCulture)));

            throw new PackLoadException(
                $"The pack at '{PathDisplay.Show(root)}' uses pack format version {manifest.PackSchemaVersion}, " +
                $"and this version of XXSM understands {supported}. Update XXSM to use this pack.",
                root);
        }
    }

    /// <summary>Drops variants that cannot be used at all, reports everything else, and keeps the rest.</summary>
    private static List<PackVariant> Validate(
        List<PackVariant> variants,
        HashIndexFile? hashes,
        List<PackDiagnostic> diagnostics)
    {
        var kept = new List<PackVariant>(variants.Count);
        var byName = new Dictionary<string, PackVariant>(StringComparer.OrdinalIgnoreCase);

        foreach (var variant in variants)
        {
            if (variant is null || string.IsNullOrWhiteSpace(variant.InternalName))
            {
                diagnostics.Add(new PackDiagnostic(
                    DiagnosticSeverity.Error,
                    PackDiagnosticCodes.MissingInternalName,
                    "A character has no internal name, so nothing can refer to it. It was skipped.",
                    PackSchema.VariantsFile));
                continue;
            }

            if (!IsValidId(variant.InternalName))
            {
                diagnostics.Add(new PackDiagnostic(
                    DiagnosticSeverity.Error,
                    PackDiagnosticCodes.InvalidInternalName,
                    $"'{variant.InternalName}' is not a usable internal name — only letters, digits, " +
                    "underscores and hyphens are allowed, because it becomes a folder name. It was skipped.",
                    PackSchema.VariantsFile,
                    variant.InternalName));
                continue;
            }

            if (byName.TryGetValue(variant.InternalName, out var existing))
            {
                diagnostics.Add(new PackDiagnostic(
                    DiagnosticSeverity.Error,
                    PackDiagnosticCodes.DuplicateInternalName,
                    $"Two characters share the internal name '{variant.InternalName}'. " +
                    $"The first one ('{existing.DisplayName}') was kept and the second was skipped.",
                    PackSchema.VariantsFile,
                    variant.InternalName));
                continue;
            }

            byName[variant.InternalName] = variant;
            kept.Add(variant);
        }

        ValidateFamilies(kept, byName, diagnostics);
        ValidateHashes(kept, byName, hashes, diagnostics);

        return kept;
    }

    private static void ValidateFamilies(
        List<PackVariant> kept,
        Dictionary<string, PackVariant> byName,
        List<PackDiagnostic> diagnostics)
    {
        foreach (var variant in kept)
        {
            if (variant.BaseCharacterId is not { Length: > 0 } parent)
            {
                continue;
            }

            if (string.Equals(parent, variant.InternalName, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new PackDiagnostic(
                    DiagnosticSeverity.Warning,
                    PackDiagnosticCodes.SelfReferencingFamily,
                    $"'{variant.DisplayName}' is listed as an outfit of itself. " +
                    "It is treated as a base character instead.",
                    PackSchema.VariantsFile,
                    variant.InternalName));
                continue;
            }

            if (!byName.ContainsKey(parent))
            {
                diagnostics.Add(new PackDiagnostic(
                    DiagnosticSeverity.Warning,
                    PackDiagnosticCodes.DanglingBaseCharacter,
                    $"'{variant.DisplayName}' says it is an outfit of '{parent}', but there is no such " +
                    "character in this pack. It is treated as a character in its own right.",
                    PackSchema.VariantsFile,
                    variant.InternalName));
            }
        }

        var families = kept
            .GroupBy(v => FamilyOf(v, byName), StringComparer.OrdinalIgnoreCase);

        foreach (var family in families)
        {
            var defaults = family.Where(v => v.IsDefaultVariant == true).ToList();

            if (defaults.Count == 0)
            {
                diagnostics.Add(new PackDiagnostic(
                    DiagnosticSeverity.Warning,
                    PackDiagnosticCodes.FamilyWithoutDefault,
                    $"None of the outfits in the '{family.Key}' family is marked as the default, " +
                    "so XXSM will pick one when it cannot tell them apart.",
                    PackSchema.VariantsFile,
                    family.Key));
            }
            else if (defaults.Count > 1)
            {
                diagnostics.Add(new PackDiagnostic(
                    DiagnosticSeverity.Warning,
                    PackDiagnosticCodes.FamilyWithMultipleDefaults,
                    $"{defaults.Count} outfits in the '{family.Key}' family are all marked as the default: " +
                    string.Join(", ", defaults.Select(d => d.InternalName)) + ".",
                    PackSchema.VariantsFile,
                    family.Key));
            }
        }
    }

    private static void ValidateHashes(
        List<PackVariant> kept,
        Dictionary<string, PackVariant> byName,
        HashIndexFile? hashes,
        List<PackDiagnostic> diagnostics)
    {
        var withHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in hashes?.Entries ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.Hash))
            {
                // An empty string upstream means absent; indexing it would match every mod.
                diagnostics.Add(new PackDiagnostic(
                    DiagnosticSeverity.Warning,
                    PackDiagnosticCodes.EmptyHash,
                    $"A hash entry for '{entry.Variant}' has no value and was ignored.",
                    PackSchema.HashesFile,
                    entry.Variant));
                continue;
            }

            if (entry.Variant is null || !byName.ContainsKey(entry.Variant))
            {
                diagnostics.Add(new PackDiagnostic(
                    DiagnosticSeverity.Warning,
                    PackDiagnosticCodes.HashForUnknownVariant,
                    $"There are hashes for '{entry.Variant}', but no such character is in this pack. " +
                    "They are ignored.",
                    PackSchema.HashesFile,
                    entry.Variant));
                continue;
            }

            withHashes.Add(entry.Variant);
        }

        foreach (var variant in kept)
        {
            if (withHashes.Contains(variant.InternalName))
            {
                continue;
            }

            // Never an error: a hashless variant is an expected state.
            diagnostics.Add(new PackDiagnostic(
                DiagnosticSeverity.Info,
                PackDiagnosticCodes.VariantWithoutHashes,
                $"'{variant.DisplayName}' has no hashes yet, so mods will be filed to it by name " +
                "rather than by hash. Learn hashes from a mod to fix that.",
                PackSchema.HashesFile,
                variant.InternalName));
        }
    }

    private static string FamilyOf(PackVariant variant, Dictionary<string, PackVariant> byName) =>
        variant.BaseCharacterId is { Length: > 0 } parent && byName.ContainsKey(parent)
            ? parent
            : variant.InternalName;

    /// <summary>Whether a string can be a pack version, which names a folder: a safe name with no empty part.</summary>
    public static bool IsValidVersion(string? version) =>
        !string.IsNullOrWhiteSpace(version)
        && version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')

        // No empty part: Windows drops a folder name's trailing dots, so "1.0." and "1.0" would be one folder.
        && version.Split('.').All(part => part.Length > 0);

    internal static bool IsValidId(string value) =>
        value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static async Task<T> ReadRequiredAsync<T>(
        string root,
        string fileName,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        var result = await ReadOptionalAsync(root, fileName, typeInfo, cancellationToken).ConfigureAwait(false);

        if (result is null)
        {
            throw new PackLoadException(
                $"The pack at '{PathDisplay.Show(root)}' is missing '{fileName}', which every pack must have.",
                Path.Combine(root, fileName));
        }

        return result;
    }

    private static async Task<T?> ReadOptionalAsync<T>(
        string root,
        string fileName,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, fileName);

        if (!PathComparer.TryResolveExisting(path, out var resolved) || !File.Exists(resolved))
        {
            return default;
        }

        try
        {
            await using var stream = File.OpenRead(resolved);
            var value = await JsonSerializer
                .DeserializeAsync(stream, typeInfo, cancellationToken)
                .ConfigureAwait(false);

            if (value is null)
            {
                throw new PackLoadException(
                    $"'{PathDisplay.Show(resolved)}' contains only 'null', which XXSM cannot read as {fileName}.",
                    resolved);
            }

            return value;
        }
        catch (JsonException ex)
        {
            throw new PackLoadException(
                $"'{PathDisplay.Show(resolved)}' is not valid JSON: {ex.Message}",
                resolved,
                ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PackLoadException($"Could not read '{PathDisplay.Show(resolved)}': {ex.Message}", resolved, ex);
        }
    }
}
