using System.Globalization;
using Serilog;
using Xxsm.Packs.Loading;
using Xxsm.Packs.Model;

namespace Xxsm.Packs.Merge;

/// <summary>Works out what installing a newer pack would change, and keeps locked values as they were.</summary>
/// <remarks>A locked change pins the old values in the overlay and comes back as a skipped update.</remarks>
public sealed class PackUpdatePlanner(ILogger logger)
{
    /// <summary>The pseudo-field name covering a variant's whole hash set.</summary>
    public const string HashesField = "hashes";

    private readonly ILogger _logger = logger.ForContext<PackUpdatePlanner>();

    /// <summary>Plans an update from <paramref name="currentPack"/> to <paramref name="newPack"/>.</summary>
    /// <param name="currentPack">The pack currently installed, or null on a first install.</param>
    /// <param name="newPack">The pack about to be installed.</param>
    /// <param name="overlay">The user's current overlay.</param>
    public PackUpdatePlan Plan(GamePack? currentPack, GamePack newPack, PackOverlay overlay)
    {
        ArgumentNullException.ThrowIfNull(newPack);
        ArgumentNullException.ThrowIfNull(overlay);

        var edits = new Dictionary<string, OverlayVariant>(
            overlay.Variants ?? new Dictionary<string, OverlayVariant>(),
            StringComparer.OrdinalIgnoreCase);

        var oldVariants = (currentPack?.Variants ?? [])
            .ToDictionary(v => v.InternalName, StringComparer.OrdinalIgnoreCase);

        var oldHashes = GroupHashes(currentPack);
        var newHashes = GroupHashes(newPack);

        var skipped = new List<SkippedUpdate>();
        var adoptions = new List<string>();

        foreach (var incoming in newPack.Variants)
        {
            if (!edits.TryGetValue(incoming.InternalName, out var edit))
            {
                continue;
            }

            if (edit.Origin == VariantOrigin.Custom && !oldVariants.ContainsKey(incoming.InternalName))
            {
                // The pack caught up with a custom character: offer adoption, not a duplicate.
                adoptions.Add(incoming.InternalName);
            }

            var protectedFields = GetProtectedFields(edit);
            if (protectedFields.Count == 0)
            {
                continue;
            }

            var previous = oldVariants.GetValueOrDefault(incoming.InternalName);
            var changes = new List<FieldChange>();

            foreach (var (field, read) in VariantFields)
            {
                if (!protectedFields.Contains(field) && !protectedFields.Contains(AllFields))
                {
                    continue;
                }

                var before = read(previous);
                var after = read(incoming);

                if (SameValue(before, after))
                {
                    continue;
                }

                // Pin the old value unless the user already set this field themselves.
                if (!edit.IsSpecified(field, OverlayFields.ValueOf(edit, field)))
                {
                    edit = WithField(edit, field, before);
                }

                changes.Add(new FieldChange(field, Text(before), Text(after), Text(OverlayFields.ValueOf(edit, field))));
            }

            if (protectedFields.Contains(HashesField) || protectedFields.Contains(AllFields))
            {
                var before = oldHashes.GetValueOrDefault(incoming.InternalName) ?? [];
                var after = newHashes.GetValueOrDefault(incoming.InternalName) ?? [];

                if (!SameHashes(before, after))
                {
                    edit = PinHashes(edit, incoming.InternalName, before, ref overlay);
                    changes.Add(new FieldChange(
                        HashesField,
                        Describe(before.Count),
                        Describe(after.Count),
                        Describe(before.Count)));
                }
            }

            if (changes.Count == 0)
            {
                continue;
            }

            edits[incoming.InternalName] = edit;
            skipped.Add(new SkippedUpdate(
                incoming.InternalName,
                incoming.DisplayName,
                changes));
        }

        if (skipped.Count > 0)
        {
            _logger.Information(
                "Pack update to {PackVersion} withheld changes to {Count} locked variants: {Variants}",
                newPack.PackVersion,
                skipped.Count,
                string.Join(", ", skipped.Select(s => s.InternalName)));
        }

        var updated = overlay with
        {
            Variants = edits,
            BasePackVersion = newPack.PackVersion,
        };

        return new PackUpdatePlan(updated, skipped, adoptions);
    }

    /// <summary>Marker meaning every field is protected.</summary>
    private const string AllFields = "*";

    /// <summary>The fields a lock protects: everything, or only those a per-field unlock names.</summary>
    private static HashSet<string> GetProtectedFields(OverlayVariant edit)
    {
        if (edit.Locked ?? edit.Origin != VariantOrigin.Pack)
        {
            return [AllFields];
        }

        return edit.LockedFields is { Count: > 0 } fields
            ? [.. fields]
            : [];
    }

    /// <summary>Every variant property a lock pins, read from a pack as the merge would see it.</summary>
    private static readonly (string Field, Func<PackVariant?, object?> Read)[] VariantFields =
    [
        (OverlayFields.DisplayName, v => v?.DisplayName),
        (OverlayFields.BaseCharacterId, v => v?.BaseCharacterId),
        (OverlayFields.IsDefaultVariant, v => v?.IsDefaultVariant),
        (OverlayFields.Aliases, v => v?.Aliases),
        (OverlayFields.ModFilesName, v => v?.ModFilesName),
        (OverlayFields.Image, v => v?.Image),
        (OverlayFields.ReleaseDate, v => v?.ReleaseDate),
        (OverlayFields.Attributes, v => v?.Attributes),
        (OverlayFields.Hidden, v => v?.Hidden ?? false),
        (OverlayFields.Notes, v => v?.Notes),
    ];

    private static OverlayVariant WithField(OverlayVariant edit, string field, object? value)
    {
        var specified = new HashSet<string>(
            edit.SpecifiedFields ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        if (edit.SpecifiedFields is null)
        {
            // An entry built in code counts non-null properties as specified: make that explicit first.
            foreach (var (name, current) in OverlayFields.ValuesOf(edit))
            {
                if (current is not null)
                {
                    specified.Add(name);
                }
            }
        }

        specified.Add(field);

        var updated = field switch
        {
            OverlayFields.DisplayName => edit with { DisplayName = value as string },
            OverlayFields.BaseCharacterId => edit with { BaseCharacterId = value as string },
            OverlayFields.IsDefaultVariant => edit with { IsDefaultVariant = value as bool? },
            OverlayFields.Aliases => edit with { Aliases = value as IReadOnlyList<string> },
            OverlayFields.ModFilesName => edit with { ModFilesName = value as string },
            OverlayFields.Image => edit with { Image = value as string },
            OverlayFields.ReleaseDate => edit with { ReleaseDate = value as string },
            OverlayFields.Attributes => edit with { Attributes = value as IReadOnlyDictionary<string, AttributeValue> },
            OverlayFields.Hidden => edit with { Hidden = value as bool? },
            OverlayFields.Notes => edit with { Notes = value as string },
            _ => edit,
        };

        return updated with
        {
            SpecifiedFields = specified,
            Origin = updated.Origin ?? VariantOrigin.Modified,
            Locked = updated.Locked ?? true,
        };
    }

    /// <summary>Whether two values of one field mean the same thing to the merge.</summary>
    private static bool SameValue(object? left, object? right)
    {
        if (left is IReadOnlyList<string> || right is IReadOnlyList<string>)
        {
            return ((left as IReadOnlyList<string>) ?? [])
                .SequenceEqual((right as IReadOnlyList<string>) ?? [], StringComparer.Ordinal);
        }

        if (left is IReadOnlyDictionary<string, AttributeValue> || right is IReadOnlyDictionary<string, AttributeValue>)
        {
            return SameAttributes(
                left as IReadOnlyDictionary<string, AttributeValue>,
                right as IReadOnlyDictionary<string, AttributeValue>);
        }

        return Equals(left, right);
    }

    /// <summary>Whether two attribute sets hold the same values, ids compared ignoring case.</summary>
    private static bool SameAttributes(
        IReadOnlyDictionary<string, AttributeValue>? left,
        IReadOnlyDictionary<string, AttributeValue>? right)
    {
        left ??= new Dictionary<string, AttributeValue>();
        right ??= new Dictionary<string, AttributeValue>();

        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var (key, value) in left)
        {
            var match = right.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase));

            if (match.Value is null
                || value.Number != match.Value.Number
                || !value.Ids.SequenceEqual(match.Value.Ids, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>How one field's value reads in the review of withheld changes.</summary>
    private static string? Text(object? value) => value switch
    {
        null => null,
        string text => text,
        bool flag => flag.ToString(CultureInfo.InvariantCulture),
        IReadOnlyList<string> list => list.Count == 0 ? null : string.Join(", ", list),
        IReadOnlyDictionary<string, AttributeValue> attributes => attributes.Count == 0
            ? null
            : string.Join("; ", attributes
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}: {pair.Value}")),
        _ => value.ToString(),
    };

    /// <summary>Pins a locked variant's hashes to the old pack's set in replace mode, keeping the user's own.</summary>
    private static OverlayVariant PinHashes(
        OverlayVariant edit,
        string internalName,
        List<PackHashEntry> previous,
        ref PackOverlay overlay)
    {
        var hashes = overlay.Hashes ?? new OverlayHashes();

        var locked = edit with
        {
            Origin = edit.Origin ?? VariantOrigin.Modified,
            Locked = edit.Locked ?? true,
        };

        if (hashes.ModeFor(internalName) == HashMergeMode.Replace)
        {
            return locked;
        }

        var modes = new Dictionary<string, HashMergeMode>(
            hashes.VariantModes ?? new Dictionary<string, HashMergeMode>(),
            StringComparer.OrdinalIgnoreCase)
        {
            [internalName] = HashMergeMode.Replace,
        };

        var add = new List<PackHashEntry>(hashes.Add ?? []);
        var present = add
            .Where(e => string.Equals(e.Variant, internalName, StringComparison.OrdinalIgnoreCase))
            .Select(HashKey)
            .ToHashSet();

        add.AddRange(previous.Where(e => present.Add(HashKey(e))));

        overlay = overlay with { Hashes = hashes with { VariantModes = modes, Add = add } };

        return locked;
    }

    /// <summary>The identity the merge deduplicates on, with the hash normalised as it does.</summary>
    private static (string, HashKind, string, string?, int?) HashKey(PackHashEntry entry) =>
        (entry.Hash.Trim().ToLowerInvariant(), entry.Kind, entry.Component ?? string.Empty, entry.TextureKind, entry.Slot);

    /// <summary>A pack's hash entries by variant, blank ones left out.</summary>
    internal static Dictionary<string, List<PackHashEntry>> GroupHashes(GamePack? pack)
    {
        var grouped = new Dictionary<string, List<PackHashEntry>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in pack?.Hashes?.Entries ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.Hash) || string.IsNullOrWhiteSpace(entry.Variant))
            {
                continue;
            }

            if (!grouped.TryGetValue(entry.Variant, out var list))
            {
                list = [];
                grouped[entry.Variant] = list;
            }

            list.Add(entry);
        }

        return grouped;
    }

    private static bool SameHashes(List<PackHashEntry> left, List<PackHashEntry> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var a = left.Select(HashIdentity).OrderBy(x => x, StringComparer.Ordinal);
        var b = right.Select(HashIdentity).OrderBy(x => x, StringComparer.Ordinal);

        return a.SequenceEqual(b, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>What makes two hash entries the same one when two versions of a variant are compared.</summary>
    internal static string HashIdentity(PackHashEntry entry) => $"{entry.Kind}:{entry.Hash}";

    private static string Describe(int count) =>
        count == 1 ? "1 hash" : $"{count.ToString(CultureInfo.InvariantCulture)} hashes";
}

/// <summary>What a pack update would do, and what was withheld.</summary>
/// <param name="Overlay">The overlay to write before the new pack takes effect.</param>
/// <param name="SkippedUpdates">Changes withheld because the variant is locked.</param>
/// <param name="AdoptionCandidates">Custom variants the new pack has caught up with, to offer for
/// adoption.</param>
public sealed record PackUpdatePlan(
    PackOverlay Overlay,
    IReadOnlyList<SkippedUpdate> SkippedUpdates,
    IReadOnlyList<string> AdoptionCandidates);

/// <summary>One locked variant a pack update would have changed.</summary>
/// <param name="InternalName">The variant.</param>
/// <param name="DisplayName">Its name, for the review list.</param>
/// <param name="Changes">What the pack would have changed, field by field.</param>
public sealed record SkippedUpdate(
    string InternalName,
    string DisplayName,
    IReadOnlyList<FieldChange> Changes);

/// <summary>One field a pack update would have changed on a locked variant.</summary>
/// <param name="Field">The field name, as it appears in the overlay.</param>
/// <param name="PackOld">What the previously installed pack said.</param>
/// <param name="PackNew">What the new pack says.</param>
/// <param name="UserValue">What the user's overlay produces instead.</param>
public sealed record FieldChange(string Field, string? PackOld, string? PackNew, string? UserValue);
