using System.Security.Cryptography;
using Xxsm.Core.Io;
using Xxsm.Packs.Loading;
using Xxsm.Packs.Merge;
using Xxsm.Packs.Model;

namespace Xxsm.Packs.Studio;

/// <summary>Which fields of a character differ between two merged views of it.</summary>
internal static class MergedVariantDifferences
{
    /// <summary>The overlay field names on which two merged characters differ.</summary>
    /// <remarks>Hashes compare by value and kind; portraits by the bytes of their files.</remarks>
    /// <returns>Field names from <see cref="OverlayFields"/>, <c>hashes</c> included; empty when the same.</returns>
    public static List<string> Between(MergedVariant one, MergedVariant two)
    {
        var fields = new List<string>();

        void Check(string field, bool same)
        {
            if (!same)
            {
                fields.Add(field);
            }
        }

        Check(OverlayFields.DisplayName, string.Equals(one.DisplayName, two.DisplayName, StringComparison.Ordinal));
        Check(OverlayFields.BaseCharacterId, string.Equals(one.BaseCharacterId, two.BaseCharacterId, StringComparison.OrdinalIgnoreCase));
        Check(OverlayFields.IsDefaultVariant, one.IsDefaultVariant == two.IsDefaultVariant);
        Check(OverlayFields.Aliases, one.Aliases.SequenceEqual(two.Aliases, StringComparer.Ordinal));
        Check(OverlayFields.ModFilesName, string.Equals(one.ModFilesName, two.ModFilesName, StringComparison.Ordinal));
        Check(OverlayFields.Image, SameImage(one.Image, two.Image));
        Check(OverlayFields.ReleaseDate, string.Equals(one.ReleaseDate, two.ReleaseDate, StringComparison.Ordinal));
        Check(OverlayFields.Attributes, SameAttributes(one.Attributes, two.Attributes));
        Check(OverlayFields.Hidden, one.Hidden == two.Hidden);
        Check(OverlayFields.Notes, string.Equals(one.Notes, two.Notes, StringComparison.Ordinal));
        Check(OverlayFields.Hashes, Hashes(one).SetEquals(Hashes(two)));

        return fields;
    }

    /// <summary>A draft as a pack the merge can take: only the rows the loader would keep.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="directory">The draft's folder, against which its pictures resolve.</param>
    public static GamePack UsablePack(PackDraft draft, string directory)
    {
        var usable = draft.Variants
            .Where(v => PackDrafts.IsValidId(v.InternalName))
            .DistinctBy(v => v.InternalName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var names = usable.Select(v => v.InternalName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hashes = draft.Hashes with
        {
            Entries = [.. (draft.Hashes.Entries ?? []).Where(e => names.Contains(e.Variant ?? string.Empty))],
        };

        return PackDrafts.ToGamePack(draft with { Variants = usable, Hashes = hashes }, directory);
    }

    /// <summary>A picture reference as a file on this computer, when it is one and it exists.</summary>
    public static string? LocalFile(string? image)
    {
        if (string.IsNullOrWhiteSpace(image))
        {
            return null;
        }

        string? path = null;

        if (image.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(image, UriKind.Absolute, out var uri)
            && uri.IsFile)
        {
            path = uri.LocalPath;
        }
        else if (Path.IsPathRooted(image))
        {
            path = image;
        }

        return path is not null && PathComparer.TryResolveExisting(path, out var found) && File.Exists(found) ? found : null;
    }

    private static bool SameImage(string? one, string? two)
    {
        if (string.Equals(one, two, StringComparison.Ordinal))
        {
            return true;
        }

        if (LocalFile(one) is not { } first || LocalFile(two) is not { } second)
        {
            return false;
        }

        try
        {
            if (new FileInfo(first).Length != new FileInfo(second).Length)
            {
                return false;
            }

            using var a = File.OpenRead(first);
            using var b = File.OpenRead(second);
            return SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable is not provably the same, and "the same" would clear a correction.
            return false;
        }
    }

    private static HashSet<(string Hash, HashKind Kind)> Hashes(MergedVariant variant) =>
        variant.Hashes.Select(h => (h.Hash.Trim().ToLowerInvariant(), h.Kind)).ToHashSet();

    private static bool SameAttributes(
        IReadOnlyDictionary<string, AttributeValue> one,
        IReadOnlyDictionary<string, AttributeValue> two) =>
        one.Count == two.Count
        && one.All(pair => two.Any(other =>
            string.Equals(other.Key, pair.Key, StringComparison.OrdinalIgnoreCase)
            && other.Value.Ids.SequenceEqual(pair.Value.Ids, StringComparer.OrdinalIgnoreCase)));
}
