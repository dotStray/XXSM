using System.Security.Cryptography;
using Serilog;
using Xxsm.Core.Io;
using Xxsm.Packs.Loading;
using Xxsm.Packs.Model;

namespace Xxsm.Packs.Merge;

/// <summary>What one pack version changed from the one it replaced: the <em>What's new</em> list.</summary>
public sealed record PackChanges
{
    /// <summary>The game.</summary>
    public required string GameId { get; init; }

    /// <summary>The version that was replaced.</summary>
    public required string FromVersion { get; init; }

    /// <summary>The version installed in its place.</summary>
    public required string ToVersion { get; init; }

    /// <summary>When the update was installed.</summary>
    public required DateTimeOffset RecordedAt { get; init; }

    /// <summary>Characters the new version has and the old did not, by display name.</summary>
    public IReadOnlyList<string> Added { get; init => field = value ?? []; } = [];

    /// <summary>Outfits (variants with a base character) the new version adds, by display name.</summary>
    public IReadOnlyList<string> AddedOutfits { get; init => field = value ?? []; } = [];

    /// <summary>Characters and outfits the old version had and the new does not, by the old display name.</summary>
    public IReadOnlyList<string> Removed { get; init => field = value ?? []; } = [];

    /// <summary>Characters whose display name changed.</summary>
    public IReadOnlyList<PackRename> Renamed { get; init => field = value ?? []; } = [];

    /// <summary>Characters that had no hashes and now have some — mods for them can be recognised.</summary>
    public IReadOnlyList<string> FirstHashes { get; init => field = value ?? []; } = [];

    /// <summary>Characters whose hashes changed.</summary>
    public IReadOnlyList<PackHashChange> HashesChanged { get; init => field = value ?? []; } = [];

    /// <summary>Characters that had no portrait and now have one.</summary>
    public IReadOnlyList<string> NewPortraits { get; init => field = value ?? []; } = [];

    /// <summary>Characters whose portrait is a different picture.</summary>
    public IReadOnlyList<string> ChangedPortraits { get; init => field = value ?? []; } = [];

    /// <summary>Whether none of the above changed; a version can differ only in details not listed.</summary>
    public bool IsEmpty =>
        Added.Count == 0 && AddedOutfits.Count == 0 && Removed.Count == 0 && Renamed.Count == 0 &&
        FirstHashes.Count == 0 && HashesChanged.Count == 0 && NewPortraits.Count == 0 && ChangedPortraits.Count == 0;
}

/// <summary>A character whose display name changed.</summary>
public sealed record PackRename(string From, string To);

/// <summary>A character whose hashes changed, and by how many each way.</summary>
/// <param name="Name">The character's display name.</param>
/// <param name="AddedCount">Hashes the new version has that the old did not.</param>
/// <param name="RemovedCount">Hashes the old version had that the new does not.</param>
public sealed record PackHashChange(string Name, int AddedCount, int RemovedCount);

/// <summary>Compares two installed versions of one game's pack.</summary>
public interface IPackChangesComparer
{
    /// <summary>What <paramref name="newPack"/> changed from <paramref name="oldPack"/>.</summary>
    /// <remarks>Portraits are compared by content; one that cannot be read counts as unchanged.</remarks>
    Task<PackChanges> CompareAsync(GamePack oldPack, GamePack newPack, CancellationToken cancellationToken = default);
}

/// <summary>The default <see cref="IPackChangesComparer"/>.</summary>
public sealed class PackChangesComparer(ILogger logger) : IPackChangesComparer
{
    private readonly ILogger _logger = logger.ForContext<PackChangesComparer>();

    /// <inheritdoc />
    public async Task<PackChanges> CompareAsync(
        GamePack oldPack, GamePack newPack, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(oldPack);
        ArgumentNullException.ThrowIfNull(newPack);

        var before = ByName(oldPack);
        var after = ByName(newPack);
        var oldHashes = PackUpdatePlanner.GroupHashes(oldPack);
        var newHashes = PackUpdatePlanner.GroupHashes(newPack);

        List<string> added = [], addedOutfits = [], removed = [], firstHashes = [], newPortraits = [], changedPortraits = [];
        List<PackRename> renamed = [];
        List<PackHashChange> hashesChanged = [];

        foreach (var (name, variant) in after)
        {
            if (!before.TryGetValue(name, out var old))
            {
                (variant.BaseCharacterId is { Length: > 0 } ? addedOutfits : added).Add(Name(variant));
                continue;
            }

            if (!string.Equals(Name(old), Name(variant), StringComparison.Ordinal))
            {
                renamed.Add(new PackRename(Name(old), Name(variant)));
            }

            var had = Keys(oldHashes, name);
            var has = Keys(newHashes, name);

            if (had.Count == 0 && has.Count > 0)
            {
                firstHashes.Add(Name(variant));
            }
            else if (has.Count > 0 && !had.SetEquals(has))
            {
                hashesChanged.Add(new PackHashChange(Name(variant), has.Except(had).Count(), had.Except(has).Count()));
            }

            var oldPicture = await DigestAsync(oldPack, old.Image, cancellationToken).ConfigureAwait(false);
            var newPicture = await DigestAsync(newPack, variant.Image, cancellationToken).ConfigureAwait(false);

            if (oldPicture is null && newPicture is not null)
            {
                newPortraits.Add(Name(variant));
            }
            else if (oldPicture is not null && newPicture is not null && !oldPicture.SequenceEqual(newPicture))
            {
                changedPortraits.Add(Name(variant));
            }
        }

        removed.AddRange(before.Where(pair => !after.ContainsKey(pair.Key)).Select(pair => Name(pair.Value)));

        return new PackChanges
        {
            GameId = newPack.GameId,
            FromVersion = oldPack.PackVersion,
            ToVersion = newPack.PackVersion,
            RecordedAt = DateTimeOffset.UtcNow,
            Added = Sorted(added),
            AddedOutfits = Sorted(addedOutfits),
            Removed = Sorted(removed),
            Renamed = [.. renamed.OrderBy(r => r.To, StringComparer.CurrentCultureIgnoreCase)],
            FirstHashes = Sorted(firstHashes),
            HashesChanged = [.. hashesChanged.OrderBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase)],
            NewPortraits = Sorted(newPortraits),
            ChangedPortraits = Sorted(changedPortraits),
        };
    }

    private static Dictionary<string, PackVariant> ByName(GamePack pack)
    {
        var variants = new Dictionary<string, PackVariant>(StringComparer.OrdinalIgnoreCase);

        foreach (var variant in pack.Variants)
        {
            if (variant.InternalName is { Length: > 0 } name)
            {
                variants.TryAdd(name, variant);
            }
        }

        return variants;
    }

    private static string Name(PackVariant variant) =>
        variant.DisplayName is { Length: > 0 } display ? display : variant.InternalName;

    private static HashSet<string> Keys(Dictionary<string, List<PackHashEntry>> hashes, string name) =>
        hashes.TryGetValue(name, out var entries)
            ? entries.Select(PackUpdatePlanner.HashIdentity).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

    private static List<string> Sorted(List<string> names) =>
        [.. names.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)];

    private async Task<byte[]?> DigestAsync(GamePack pack, string? image, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(image) || Path.IsPathRooted(image) || image.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }

        var path = Path.Combine(pack.Directory, image);

        if (!PathComparer.TryResolveExisting(path, out var resolved) || !File.Exists(resolved))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(resolved);
            return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(ex, "Could not read the portrait {Path} to compare it; counted as unchanged", resolved);
            return null;
        }
    }
}
