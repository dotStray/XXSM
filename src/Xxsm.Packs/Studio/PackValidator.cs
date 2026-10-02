using System.Globalization;
using Xxsm.Core.Diagnostics;
using Xxsm.Core.Hashes;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Core.Text;
using Xxsm.Packs.Loading;
using Xxsm.Packs.Model;
using Xxsm.Packs.Sorting;

namespace Xxsm.Packs.Studio;

/// <summary>Stable codes for the checks <see cref="PackValidator"/> adds to the loader's own.</summary>
public static class PackValidationCodes
{
    /// <summary>The game id cannot name a folder.</summary>
    public const string GameIdInvalid = "pack.game-id.invalid";

    /// <summary>The game has no name.</summary>
    public const string GameWithoutName = "pack.game.no-name";

    /// <summary>The manifest has no pack version.</summary>
    public const string PackVersionMissing = "pack.manifest.no-version";

    /// <summary>An attribute id, or one of its value ids, cannot be an id.</summary>
    public const string AttributeIdInvalid = "pack.attribute.invalid-id";

    /// <summary>An enumerated attribute lists the same value twice.</summary>
    public const string AttributeValueDuplicate = "pack.attribute.duplicate-value";

    /// <summary>A variant has no display name.</summary>
    public const string VariantWithoutDisplayName = "pack.variant.no-display-name";

    /// <summary>The folder a variant's mods would be filed in cannot exist.</summary>
    public const string FolderNameUnusable = "pack.variant.folder-name-unusable";

    /// <summary>Two variants would file their mods in the same folder.</summary>
    public const string FolderNameShared = "pack.variant.folder-name-shared";

    /// <summary>A variant's folder is the one unsorted mods go to.</summary>
    public const string FolderIsOthers = "pack.variant.folder-is-others";

    /// <summary>Two variants look the same to name matching.</summary>
    public const string NamesIndistinct = "pack.variant.names-indistinct";

    /// <summary>A variant sets an attribute the game does not declare.</summary>
    public const string AttributeUndeclared = "pack.variant.attribute-undeclared";

    /// <summary>A variant sets an attribute to a value the game does not declare.</summary>
    public const string AttributeValueUndeclared = "pack.variant.attribute-value-undeclared";

    /// <summary>A numeric attribute holds something other than a number.</summary>
    public const string AttributeNotNumber = "pack.variant.attribute-not-number";

    /// <summary>A variant has no portrait. A warning, never an error.</summary>
    public const string PortraitMissing = "pack.variant.no-portrait";

    /// <summary>A variant names a picture that is not in the pack.</summary>
    public const string PortraitFileMissing = "pack.variant.portrait-file-missing";

    /// <summary>A variant's picture is larger than a pack should carry.</summary>
    public const string PortraitTooLarge = "pack.variant.portrait-too-large";

    /// <summary>A variant's picture is a file on this computer, which a pack cannot carry.</summary>
    public const string PortraitOutsidePack = "pack.variant.portrait-outside-pack";

    /// <summary>A variant is an outfit of something that is itself an outfit.</summary>
    public const string OutfitOfOutfit = "pack.family.outfit-of-outfit";

    /// <summary>A hash entry is not 8 or 16 hex digits.</summary>
    public const string HashMalformed = "pack.hashes.malformed";

    /// <summary>A deny-listed hash is not 8 or 16 hex digits.</summary>
    public const string IgnoredHashMalformed = "pack.hashes.ignored-malformed";

    /// <summary>A hash is on so many characters the sorter will ignore it.</summary>
    public const string HashFansOut = "pack.hashes.fan-out";

    /// <summary>The pack ignores a hash no character carries, so the entry does nothing.</summary>
    public const string IgnoredHashUnused = "pack.hashes.ignored-unused";

    /// <summary>Hashes are claimed by more than one family.</summary>
    public const string HashSharedBetweenFamilies = "pack.hashes.shared-between-families";
}

/// <summary>Everything wrong with a draft, worst first.</summary>
/// <param name="Problems">Errors before warnings, then by subject; each names the variant, hash or
/// file.</param>
public sealed record PackValidationResult(IReadOnlyList<PackDiagnostic> Problems)
{
    /// <summary>How many problems block export.</summary>
    public int ErrorCount => Problems.Count(p => p.Severity == DiagnosticSeverity.Error);

    /// <summary>How many problems are worth knowing about but block nothing.</summary>
    public int WarningCount => Problems.Count(p => p.Severity == DiagnosticSeverity.Warning);

    /// <summary>Whether the draft may be exported: no errors. Warnings never block it.</summary>
    public bool CanExport => ErrorCount == 0;
}

/// <summary>The checks a pack must pass before export, the same the presets builder runs. Pure.</summary>
/// <remarks>An error means the pack would work wrongly and blocks export; a warning blocks nothing.</remarks>
public static class PackValidator
{
    /// <summary>A stand-in pack folder: whether a path stays inside a pack does not depend on where it is.</summary>
    private static readonly string PortableRoot = Path.Combine(Path.GetTempPath(), "xxsm-pack");

    /// <summary>The size above which a picture is warned about: everyone installing downloads it.</summary>
    public const long LargePictureBytes = 200L * 1024;

    /// <summary>Checks a draft.</summary>
    /// <param name="draft">The draft to check.</param>
    /// <param name="settings">The sorter's thresholds, for the fan-out check. Defaults to
    /// <see cref="SortSettings.Default"/>.</param>
    /// <param name="imageBytes">A pack-relative picture path's size in bytes, or null when missing; itself null
    /// to skip the picture-file checks.</param>
    /// <returns>Every problem found.</returns>
    public static PackValidationResult Validate(
        PackDraft draft,
        SortSettings? settings = null,
        Func<string, long?>? imageBytes = null)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var problems = new List<PackDiagnostic>();

        ValidateGame(draft, problems);

        var kept = ValidateIds(draft, problems);
        ValidateVariants(draft, kept, imageBytes, problems);
        ValidateFamilies(kept, problems);
        ValidateHashes(draft, kept, settings ?? SortSettings.Default, problems);

        return new PackValidationResult(
        [
            .. problems
                .OrderByDescending(p => p.Severity)
                .ThenBy(p => p.Subject ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Code, StringComparer.Ordinal),
        ]);
    }

    private static void ValidateGame(PackDraft draft, List<PackDiagnostic> problems)
    {
        var game = draft.Game;

        if (!PackDrafts.IsValidId(draft.GameId))
        {
            problems.Add(Error(
                PackValidationCodes.GameIdInvalid,
                $"'{draft.GameId}' cannot be a game id — only letters, digits, underscores and hyphens are allowed.",
                PackSchema.ManifestFile,
                target: On(PackDiagnosticFields.GameId)));
        }

        if (!string.IsNullOrWhiteSpace(game.GameId)
            && !string.Equals(game.GameId, draft.GameId, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(Error(
                PackDiagnosticCodes.GameIdMismatch,
                $"The game is called '{game.GameId}' in one place and '{draft.GameId}' in another. " +
                "They have to match.",
                PackSchema.GameFile,
                target: On(PackDiagnosticFields.GameId)));
        }

        if (string.IsNullOrWhiteSpace(game.DisplayName))
        {
            problems.Add(Error(
                PackValidationCodes.GameWithoutName, "The game has no name.", PackSchema.GameFile, target: On(PackDiagnosticFields.GameName)));
        }

        if (string.IsNullOrWhiteSpace(draft.Manifest.PackVersion))
        {
            problems.Add(Error(
                PackValidationCodes.PackVersionMissing,
                "The pack has no version, so nobody could tell an update from the copy they have.",
                PackSchema.ManifestFile,
                target: On(PackDiagnosticFields.PackVersion)));
        }

        foreach (var (id, attribute) in game.Attributes ?? new Dictionary<string, AttributeDefinition>())
        {
            if (!PackDrafts.IsValidId(id))
            {
                problems.Add(Error(
                    PackValidationCodes.AttributeIdInvalid,
                    $"'{id}' cannot be an attribute id — only letters, digits, underscores and hyphens are allowed.",
                    PackSchema.GameFile,
                    id,
                    new PackDiagnosticTarget { Field = PackDiagnosticFields.AttributeDefinition, Attribute = id }));
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var value in attribute.Values ?? [])
            {
                if (!PackDrafts.IsValidId(value.Id))
                {
                    problems.Add(Error(
                        PackValidationCodes.AttributeIdInvalid,
                        $"'{value.Id}' cannot be a value of {Label(attribute.DisplayName, id)} — only letters, " +
                        "digits, underscores and hyphens are allowed.",
                        PackSchema.GameFile,
                        id,
                        new PackDiagnosticTarget { Field = PackDiagnosticFields.AttributeDefinition, Attribute = id, Value = value.Id }));
                }
                else if (!seen.Add(value.Id))
                {
                    problems.Add(Error(
                        PackValidationCodes.AttributeValueDuplicate,
                        $"{Label(attribute.DisplayName, id)} lists the value '{value.Id}' twice.",
                        PackSchema.GameFile,
                        id,
                        new PackDiagnosticTarget { Field = PackDiagnosticFields.AttributeDefinition, Attribute = id, Value = value.Id }));
                }
            }
        }
    }

    /// <summary>Reports unusable ids; returns one valid variant per internal name for later checks.</summary>
    private static Dictionary<string, PackVariant> ValidateIds(PackDraft draft, List<PackDiagnostic> problems)
    {
        var kept = new Dictionary<string, PackVariant>(StringComparer.OrdinalIgnoreCase);

        foreach (var variant in draft.Variants)
        {
            if (string.IsNullOrWhiteSpace(variant.InternalName))
            {
                problems.Add(Error(
                    PackDiagnosticCodes.MissingInternalName,
                    $"{(string.IsNullOrWhiteSpace(variant.DisplayName) ? "A character" : $"'{variant.DisplayName}'")} " +
                    "has no internal name, so nothing can refer to it.",
                    PackSchema.VariantsFile));
                continue;
            }

            if (!PackDrafts.IsValidId(variant.InternalName))
            {
                problems.Add(Error(
                    PackDiagnosticCodes.InvalidInternalName,
                    $"'{variant.InternalName}' cannot be an internal name — only letters, digits, underscores and " +
                    "hyphens are allowed, because it becomes a folder name.",
                    PackSchema.VariantsFile,
                    variant.InternalName,
                    On(PackDiagnosticFields.InternalName, variant.InternalName)));
                continue;
            }

            if (kept.TryGetValue(variant.InternalName, out var existing))
            {
                problems.Add(Error(
                    PackDiagnosticCodes.DuplicateInternalName,
                    $"'{existing.InternalName}' and '{variant.InternalName}' are the same internal name — ids ignore " +
                    "capitals. Rename one of them.",
                    PackSchema.VariantsFile,
                    variant.InternalName,
                    On(PackDiagnosticFields.InternalName, variant.InternalName, existing.InternalName)));
                continue;
            }

            kept[variant.InternalName] = variant;
        }

        return kept;
    }

    private static void ValidateVariants(
        PackDraft draft,
        Dictionary<string, PackVariant> kept,
        Func<string, long?>? imageBytes,
        List<PackDiagnostic> problems)
    {
        var game = draft.Game;
        var attributes = game.Attributes ?? new Dictionary<string, AttributeDefinition>();

        var byFolder = new Dictionary<string, List<PackVariant>>(StringComparer.OrdinalIgnoreCase);
        var byMatchKey = new Dictionary<string, List<PackVariant>>(StringComparer.Ordinal);

        foreach (var variant in kept.Values)
        {
            var id = variant.InternalName;

            if (string.IsNullOrWhiteSpace(variant.DisplayName))
            {
                problems.Add(Warning(
                    PackValidationCodes.VariantWithoutDisplayName,
                    $"'{id}' has no display name, so its internal name is shown instead.",
                    PackSchema.VariantsFile,
                    id,
                    On(PackDiagnosticFields.DisplayName, id)));
            }

            var folder = string.IsNullOrWhiteSpace(variant.ModFilesName) ? id : variant.ModFilesName;

            if (ModsFolderLayout.DescribeUnusableFolderName(folder) is { } why)
            {
                problems.Add(Error(
                    PackValidationCodes.FolderNameUnusable,
                    $"{Name(variant)} would have its mods filed in a folder called '{PathDisplay.Show(folder)}', which cannot work. {why}",
                    PackSchema.VariantsFile,
                    id,
                    On(PackDiagnosticFields.ModFilesName, id)));
            }
            else
            {
                Group(byFolder, folder, variant);

                if (string.Equals(folder, ModsFolderLayout.UnsortedFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add(Warning(
                        PackValidationCodes.FolderIsOthers,
                        $"{Name(variant)} would have its mods filed in '{PathDisplay.Show(folder)}', the folder mods nobody can " +
                        "identify go to. They would be mixed up with those.",
                        PackSchema.VariantsFile,
                        id,
                        On(PackDiagnosticFields.ModFilesName, id)));
                }
            }

            var matchKey = SortName.Normalize(id);
            if (matchKey.Length > 0)
            {
                Group(byMatchKey, matchKey, variant);
            }

            ValidateAttributeValues(variant, attributes, problems);
            ValidatePortrait(variant, imageBytes, problems);
        }

        foreach (var (folder, sharing) in byFolder)
        {
            if (sharing.Count > 1)
            {
                problems.Add(Error(
                    PackValidationCodes.FolderNameShared,
                    $"{Names(sharing)} would all file their mods in the same folder, '{PathDisplay.Show(folder)}'. " +
                    "Give each its own mod folder name.",
                    PackSchema.VariantsFile,
                    sharing[0].InternalName,
                    On(PackDiagnosticFields.ModFilesName, sharing.Select(v => v.InternalName))));
            }
        }

        foreach (var (_, alike) in byMatchKey)
        {
            if (alike.Count > 1)
            {
                problems.Add(Warning(
                    PackValidationCodes.NamesIndistinct,
                    $"{Names(alike)} look the same when a mod is matched by name, so a mod found only by its " +
                    "name could be filed under either.",
                    PackSchema.VariantsFile,
                    alike[0].InternalName,
                    On(PackDiagnosticFields.DisplayName, alike.Select(v => v.InternalName))));
            }
        }
    }

    private static void ValidateAttributeValues(
        PackVariant variant,
        IReadOnlyDictionary<string, AttributeDefinition> attributes,
        List<PackDiagnostic> problems)
    {
        foreach (var (key, value) in variant.Attributes ?? new Dictionary<string, AttributeValue>())
        {
            var declared = attributes.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase));

            if (declared.Value is null)
            {
                problems.Add(Warning(
                    PackValidationCodes.AttributeUndeclared,
                    $"{Name(variant)} sets '{key}', which is not one of the game's attributes, so no filter will use it.",
                    PackSchema.VariantsFile,
                    variant.InternalName,
                    new PackDiagnosticTarget
                    {
                        Field = PackDiagnosticFields.AttributeDefinition,
                        Attribute = key,
                        Characters = [variant.InternalName],
                    }));
                continue;
            }

            var label = Label(declared.Value.DisplayName, declared.Key);

            if (declared.Value.IsNumeric)
            {
                if (value.Number is null)
                {
                    problems.Add(Warning(
                        PackValidationCodes.AttributeNotNumber,
                        $"{Name(variant)} has {label} set to '{value}', but {label} is a number.",
                        PackSchema.VariantsFile,
                        variant.InternalName,
                        new PackDiagnosticTarget
                        {
                            Field = PackDiagnosticFields.AttributeValue,
                            Attribute = declared.Key,
                            Value = value.ToString(),
                            Characters = [variant.InternalName],
                        }));
                }

                continue;
            }

            var allowed = (declared.Value.Values ?? []).Select(v => v.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var id in value.Ids.Where(id => !allowed.Contains(id)))
            {
                problems.Add(Warning(
                    PackValidationCodes.AttributeValueUndeclared,
                    $"{Name(variant)} has {label} set to '{id}', which is not one of the values {label} lists.",
                    PackSchema.VariantsFile,
                    variant.InternalName,
                    new PackDiagnosticTarget
                    {
                        Field = PackDiagnosticFields.AttributeValue,
                        Attribute = declared.Key,
                        Value = id,
                        Characters = [variant.InternalName],
                    }));
            }
        }
    }

    private static void ValidatePortrait(
        PackVariant variant,
        Func<string, long?>? imageBytes,
        List<PackDiagnostic> problems)
    {
        var image = variant.Image;

        if (string.IsNullOrWhiteSpace(image))
        {
            problems.Add(Warning(
                PackValidationCodes.PortraitMissing,
                $"{Name(variant)} has no portrait, so its tile shows initials.",
                PackSchema.VariantsFile,
                variant.InternalName,
                On(PackDiagnosticFields.Image, variant.InternalName)));
            return;
        }

        // The one rule the application opens pictures by, so Studio never passes what the grid would not show.
        if (UntrustedLocation.IsWebAddress(image, out _))
        {
            // A web address: the application fetches and caches it.
            return;
        }

        if (Uri.TryCreate(image, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp)
        {
            problems.Add(Error(
                PackValidationCodes.PortraitOutsidePack,
                $"{Name(variant)}'s portrait is on a plain http address ('{image}'), which XXSM does not fetch: " +
                "anyone on the way could change it. Use the https address.",
                PackSchema.VariantsFile,
                variant.InternalName,
                On(PackDiagnosticFields.Image, variant.InternalName)));
            return;
        }

        if (image.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(image))
        {
            problems.Add(Error(
                PackValidationCodes.PortraitOutsidePack,
                $"{Name(variant)}'s portrait is a file on this computer ('{image}'), which nobody else has. " +
                "Add the picture to the pack instead.",
                PackSchema.VariantsFile,
                variant.InternalName,
                On(PackDiagnosticFields.Image, variant.InternalName)));
            return;
        }

        if (!UntrustedLocation.TryResolveInside(PortableRoot, image, out _))
        {
            problems.Add(Error(
                PackValidationCodes.PortraitOutsidePack,
                $"{Name(variant)}'s portrait ('{image}') is neither a file in the pack nor an https address, " +
                "so XXSM would not open it. Put the picture in the pack's images folder.",
                PackSchema.VariantsFile,
                variant.InternalName,
                On(PackDiagnosticFields.Image, variant.InternalName)));
            return;
        }

        if (imageBytes is null)
        {
            return;
        }

        var size = imageBytes(image);

        if (size is null)
        {
            problems.Add(Warning(
                PackValidationCodes.PortraitFileMissing,
                $"{Name(variant)}'s portrait is '{image}', but there is no such picture in the pack, so its tile " +
                "shows initials.",
                PackSchema.VariantsFile,
                variant.InternalName,
                On(PackDiagnosticFields.Image, variant.InternalName)));
        }
        else if (size > LargePictureBytes)
        {
            problems.Add(Warning(
                PackValidationCodes.PortraitTooLarge,
                $"{Name(variant)}'s portrait is {Kilobytes(size.Value)} KB. Everyone who installs the pack downloads " +
                $"it, so a picture about 512 pixels across and under {Kilobytes(LargePictureBytes)} KB is plenty.",
                PackSchema.VariantsFile,
                variant.InternalName,
                On(PackDiagnosticFields.Image, variant.InternalName)));
        }
    }

    private static void ValidateFamilies(Dictionary<string, PackVariant> kept, List<PackDiagnostic> problems)
    {
        var families = new Dictionary<string, List<PackVariant>>(StringComparer.OrdinalIgnoreCase);

        foreach (var variant in kept.Values)
        {
            var family = variant.InternalName;

            if (variant.BaseCharacterId is { Length: > 0 } parentId)
            {
                if (string.Equals(parentId, variant.InternalName, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add(Error(
                        PackDiagnosticCodes.SelfReferencingFamily,
                        $"{Name(variant)} is listed as an outfit of itself.",
                        PackSchema.VariantsFile,
                        variant.InternalName,
                        On(PackDiagnosticFields.BaseCharacterId, variant.InternalName)));
                }
                else if (!kept.TryGetValue(parentId, out var parent))
                {
                    problems.Add(Error(
                        PackDiagnosticCodes.DanglingBaseCharacter,
                        $"{Name(variant)} is an outfit of '{parentId}', but there is no character with that internal name.",
                        PackSchema.VariantsFile,
                        variant.InternalName,
                        On(PackDiagnosticFields.BaseCharacterId, variant.InternalName)));
                }
                else
                {
                    family = parent.InternalName;

                    if (parent.BaseCharacterId is { Length: > 0 } grandparent
                        && !string.Equals(grandparent, parent.InternalName, StringComparison.OrdinalIgnoreCase))
                    {
                        problems.Add(Error(
                            PackValidationCodes.OutfitOfOutfit,
                            $"{Name(variant)} is an outfit of {Name(parent)}, which is itself an outfit of " +
                            $"'{grandparent}'. Make it an outfit of '{grandparent}' instead.",
                            PackSchema.VariantsFile,
                            variant.InternalName,
                            On(PackDiagnosticFields.BaseCharacterId, variant.InternalName, parent.InternalName)));
                    }
                }
            }

            Group(families, family, variant);
        }

        foreach (var (familyId, members) in families)
        {
            // As the merge does: a base with no flag is its family's default, a skin with none is not.
            var defaults = members
                .Where(m => m.IsDefaultVariant ?? string.Equals(m.InternalName, familyId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var head = kept[familyId];

            if (defaults.Count == 0)
            {
                problems.Add(Warning(
                    PackDiagnosticCodes.FamilyWithoutDefault,
                    $"None of the outfits in {Name(head)}'s family is marked as the default, so {Name(head)} " +
                    "itself will be used when a mod cannot be told apart.",
                    PackSchema.VariantsFile,
                    head.InternalName,
                    On(PackDiagnosticFields.DefaultOutfit, FamilyOrder(head, members))));
            }
            else if (defaults.Count > 1)
            {
                problems.Add(Error(
                    PackDiagnosticCodes.FamilyWithMultipleDefaults,
                    $"{Names(defaults)} are all marked as the default outfit of {Name(head)}'s family. " +
                    "Only one can be.",
                    PackSchema.VariantsFile,
                    head.InternalName,
                    On(PackDiagnosticFields.DefaultOutfit, FamilyOrder(head, members))));
            }
        }
    }

    /// <summary>A family's members, its head first.</summary>
    private static IEnumerable<string> FamilyOrder(PackVariant head, List<PackVariant> members) =>
        members
            .OrderBy(m => ReferenceEquals(m, head) ? 0 : 1)
            .ThenBy(m => m.InternalName, StringComparer.OrdinalIgnoreCase)
            .Select(m => m.InternalName);

    private static void ValidateHashes(
        PackDraft draft,
        Dictionary<string, PackVariant> kept,
        SortSettings settings,
        List<PackDiagnostic> problems)
    {
        var sharing = ClassifyHashes(draft, kept, settings, problems);

        foreach (var (hash, variants) in sharing.FanOuts)
        {
            problems.Add(Warning(
                PackValidationCodes.HashFansOut,
                $"The hash {hash} appears on {EnglishCount.Plural(variants.Count, "character", "characters")}, so it " +
                "can't identify anyone; it will be ignored.",
                PackSchema.HashesFile,
                hash,
                new PackDiagnosticTarget { Field = PackDiagnosticFields.Hashes, Hash = hash, Characters = variants }));
        }

        foreach (var group in sharing.Shared)
        {
            var names = group.Families.Select(f => Name(kept[f])).ToList();
            var count = group.Hashes.Count;

            problems.Add(Warning(
                PackValidationCodes.HashSharedBetweenFamilies,
                $"{JoinNames(names)} share {EnglishCount.Plural(count, "hash", "hashes")}, so a mod carrying only " +
                (count == 1 ? "that one" : "those") + " can't be told apart by hash.",
                PackSchema.HashesFile,
                group.Families[0],
                On(PackDiagnosticFields.Hashes, group.Families)));
        }
    }

    /// <summary>Which hashes too many characters carry, and which exactly one set of families shares.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="kept">The characters later checks can rely on, from <see cref="ValidateIds"/>.</param>
    /// <param name="settings">The sorter's thresholds.</param>
    /// <param name="problems">Where to report malformed hashes and characters without any, or null not to.</param>
    internal static HashClassification ClassifyHashes(
        PackDraft draft,
        Dictionary<string, PackVariant> kept,
        SortSettings settings,
        List<PackDiagnostic>? problems)
    {
        var ignored = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in draft.Hashes.IgnoredHashes ?? [])
        {
            if (HashText.Normalize(raw) is { } hash)
            {
                ignored.Add(hash);
            }
            else
            {
                problems?.Add(Warning(
                    PackValidationCodes.IgnoredHashMalformed,
                    $"'{raw}' is on the list of hashes to ignore, but it is not a hash — a hash is 8 or 16 of the " +
                    "characters 0–9 and a–f.",
                    PackSchema.HashesFile,
                    raw,
                    new PackDiagnosticTarget { Field = PackDiagnosticFields.IgnoredHashes, Hash = raw }));
            }
        }

        var withHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var claims = new Dictionary<string, (HashSet<string> Variants, HashSet<string> Families, bool OnlyShader)>(
            StringComparer.Ordinal);

        foreach (var entry in draft.Hashes.Entries ?? [])
        {
            if (!kept.TryGetValue(entry.Variant ?? string.Empty, out var variant))
            {
                if (unknown.Add(entry.Variant ?? string.Empty))
                {
                    problems?.Add(Warning(
                        PackDiagnosticCodes.HashForUnknownVariant,
                        $"There are hashes for '{entry.Variant}', but no character has that internal name. " +
                        "They will be ignored.",
                        PackSchema.HashesFile,
                        entry.Variant,
                        On(PackDiagnosticFields.Hashes, entry.Variant ?? string.Empty)));
                }

                continue;
            }

            if (HashText.Normalize(entry.Hash) is not { } hash)
            {
                // A warning, not an error: nothing indexes it, and real upstream data has a few.
                problems?.Add(Warning(
                    PackValidationCodes.HashMalformed,
                    $"{Name(variant)} has '{entry.Hash}' as a hash, but a hash is 8 or 16 of the characters " +
                    "0–9 and a–f. It will be ignored.",
                    PackSchema.HashesFile,
                    variant.InternalName,
                    new PackDiagnosticTarget
                    {
                        Field = PackDiagnosticFields.Hashes,
                        Hash = entry.Hash,
                        Characters = [variant.InternalName],
                    }));
                continue;
            }

            withHashes.Add(variant.InternalName);

            if (!claims.TryGetValue(hash, out var claim))
            {
                claim = (new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), true);
            }

            claim.Variants.Add(variant.InternalName);
            claim.Families.Add(FamilyOf(variant, kept));
            claims[hash] = claim with { OnlyShader = claim.OnlyShader && entry.Kind == HashKind.RootVs };
        }

        foreach (var variant in kept.Values.Where(v => !withHashes.Contains(v.InternalName)))
        {
            problems?.Add(Warning(
                PackDiagnosticCodes.VariantWithoutHashes,
                $"{Name(variant)} has no hashes yet, so mods will be filed to it by name rather than by hash.",
                PackSchema.HashesFile,
                variant.InternalName,
                On(PackDiagnosticFields.Hashes, variant.InternalName)));
        }

        var fanOuts = new List<(string Hash, IReadOnlyList<string> Variants)>();
        var shared = new Dictionary<string, (List<string> Families, List<string> Hashes)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (hash, claim) in claims.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            // A shader hash identifies nobody, and one the pack ignores is dealt with.
            if (claim.OnlyShader || ignored.Contains(hash))
            {
                continue;
            }

            if (claim.Variants.Count > settings.AmbiguityThreshold)
            {
                fanOuts.Add((hash, [.. claim.Variants.Order(StringComparer.OrdinalIgnoreCase)]));
                continue;
            }

            if (claim.Families.Count > 1)
            {
                var families = claim.Families.Order(StringComparer.OrdinalIgnoreCase).ToList();
                var key = string.Join('\u0001', families);

                if (!shared.TryGetValue(key, out var group))
                {
                    group = (families, []);
                    shared[key] = group;
                }

                group.Hashes.Add(hash);
            }
        }

        var carriers = claims.ToDictionary(
            c => c.Key,
            c => (IReadOnlyList<string>)[.. c.Value.Variants.Order(StringComparer.OrdinalIgnoreCase)],
            StringComparer.Ordinal);

        // An ignore entry no character's hash matches is dead weight.
        foreach (var hash in ignored.Where(hash => !carriers.ContainsKey(hash)).Order(StringComparer.Ordinal))
        {
            problems?.Add(Warning(
                PackValidationCodes.IgnoredHashUnused,
                $"The pack ignores '{hash}', but no character carries it, so the entry does nothing.",
                PackSchema.HashesFile,
                hash,
                new PackDiagnosticTarget { Field = PackDiagnosticFields.IgnoredHashes, Hash = hash }));
        }

        return new HashClassification(
            fanOuts,
            [.. shared.Values.Select(g => new SharedHashGroup(g.Families, g.Hashes))],
            carriers,
            ignored);
    }

    /// <summary>The characters later checks can rely on: one per internal name, each a valid id.</summary>
    internal static Dictionary<string, PackVariant> KeptVariants(PackDraft draft) => ValidateIds(draft, []);

    /// <summary>The family a character belongs to: its base's internal name, or its own.</summary>
    internal static string FamilyOf(PackVariant variant, Dictionary<string, PackVariant> kept) =>
        variant.BaseCharacterId is { Length: > 0 } parent
        && kept.TryGetValue(parent, out var found)
        && !string.Equals(found.InternalName, variant.InternalName, StringComparison.OrdinalIgnoreCase)
            ? found.InternalName
            : variant.InternalName;

    private static void Group(Dictionary<string, List<PackVariant>> groups, string key, PackVariant variant)
    {
        if (!groups.TryGetValue(key, out var list))
        {
            list = [];
            groups[key] = list;
        }

        list.Add(variant);
    }

    /// <summary>A variant as a sentence names it: its display name, and its id when that differs.</summary>
    private static string Name(PackVariant variant) =>
        string.IsNullOrWhiteSpace(variant.DisplayName)
        || string.Equals(variant.DisplayName, variant.InternalName, StringComparison.Ordinal)
            ? $"'{variant.InternalName}'"
            : $"'{variant.DisplayName}' ({variant.InternalName})";

    private static string Names(IEnumerable<PackVariant> variants) => JoinNames([.. variants.Select(Name)]);

    private static string JoinNames(List<string> names) =>
        names.Count switch
        {
            0 => string.Empty,
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
        };

    private static string Label(string? displayName, string id) =>
        string.IsNullOrWhiteSpace(displayName) ? $"'{id}'" : displayName;

    private static string Kilobytes(long bytes) =>
        ((bytes + 1023) / 1024).ToString(CultureInfo.InvariantCulture);

    private static PackDiagnostic Error(
        string code, string message, string file, string? subject = null, PackDiagnosticTarget? target = null) =>
        new(DiagnosticSeverity.Error, code, message, file, subject) { Target = target };

    private static PackDiagnostic Warning(
        string code, string message, string file, string? subject = null, PackDiagnosticTarget? target = null) =>
        new(DiagnosticSeverity.Warning, code, message, file, subject) { Target = target };

    /// <summary>What a problem about a field of one or more characters points at.</summary>
    private static PackDiagnosticTarget On(string field, params IEnumerable<string> characters) =>
        new() { Field = field, Characters = [.. characters] };
}

/// <summary>What <see cref="PackValidator.ClassifyHashes"/> found.</summary>
/// <param name="FanOuts">Hashes too many characters carry to identify anyone, with every carrier.</param>
/// <param name="Shared">Hashes shared by exactly one set of families, grouped by that set.</param>
/// <param name="Carriers">Every well-formed hash, with the characters that carry it.</param>
/// <param name="Ignored">The hashes the pack already ignores.</param>
internal sealed record HashClassification(
    IReadOnlyList<(string Hash, IReadOnlyList<string> Variants)> FanOuts,
    IReadOnlyList<SharedHashGroup> Shared,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Carriers,
    IReadOnlySet<string> Ignored);
