namespace Xxsm.Packs.Registry;

/// <summary>Why a published pack version can or cannot be installed by this build.</summary>
public enum PackAvailability
{
    /// <summary>It can be installed.</summary>
    Installable,

    /// <summary>Its <c>packSchemaVersion</c> is outside the range this build understands; listed, not hidden.</summary>
    UnsupportedSchema,

    /// <summary>Its <c>minAppVersion</c> is newer than this build.</summary>
    NeedsNewerApp,
}

/// <summary>One version of a pack, as offered by a registry and judged by this build.</summary>
/// <param name="Version">The registry's entry, verbatim.</param>
/// <param name="Availability">Whether this build can install it.</param>
/// <param name="RefusalReason">Why not, in words a user can act on. Null when installable.</param>
public sealed record PackCatalogVersion(
    RegistryPackVersion Version,
    PackAvailability Availability,
    string? RefusalReason)
{
    /// <summary>The pack version string.</summary>
    public string PackVersion => Version.PackVersion;

    /// <summary>Whether this build can install it.</summary>
    public bool IsInstallable => Availability == PackAvailability.Installable;
}

/// <summary>One game's packs: what is published, what is installed, and what the user chose.</summary>
public sealed record PackCatalogEntry
{
    /// <summary>The game.</summary>
    public required string GameId { get; init; }

    /// <summary>The name to show.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The registry this entry came from, as the user configured it.</summary>
    public required string? Registry { get; init; }

    /// <summary>Every published version, newest first.</summary>
    public required IReadOnlyList<PackCatalogVersion> Versions { get; init; }

    /// <summary>The versions installed on this machine, newest first.</summary>
    public required IReadOnlyList<string> InstalledVersions { get; init; }

    /// <summary>The version XXSM would load, or null when none is installed.</summary>
    public required string? ActiveVersion { get; init; }

    /// <summary>The user's choices for this game.</summary>
    public required PackGamePreference Preference { get; init; }

    /// <summary>The newest version this build could install, or null when there is none.</summary>
    public PackCatalogVersion? LatestInstallable =>
        Versions.FirstOrDefault(version => version.IsInstallable);

    /// <summary>The version <c>update</c> would move to: the pin, else the newest installable; null for none.</summary>
    public PackCatalogVersion? TargetVersion =>
        Preference.PinnedVersion is { Length: > 0 } pinned
            ? Versions.FirstOrDefault(version =>
                string.Equals(version.PackVersion, pinned, StringComparison.OrdinalIgnoreCase))
            : LatestInstallable;

    /// <summary>Whether the target version is published, installable and not yet installed.</summary>
    public bool UpdateAvailable =>
        TargetVersion is { IsInstallable: true } target &&
        !InstalledVersions.Contains(target.PackVersion, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether anything is installed for this game.</summary>
    public bool IsInstalled => ActiveVersion is { Length: > 0 };

    /// <summary>The newer version a pin to the version in use holds the game back from; null when not held.</summary>
    public string? NewerThanHeld
    {
        get
        {
            if (Preference.PinnedVersion is not { Length: > 0 } pinned
                || !string.Equals(ActiveVersion, pinned, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string?[] candidates = [InstalledVersions.Count > 0 ? InstalledVersions[0] : null, LatestInstallable?.PackVersion];
            var newest = candidates.OfType<string>().Max(PackVersionOrder.Instance);

            return newest is not null && PackVersionOrder.Instance.Compare(newest, ActiveVersion) > 0 ? newest : null;
        }
    }
}

/// <summary>A registry that could not be read, and why.</summary>
/// <param name="Registry">The registry as the user configured it.</param>
/// <param name="Message">What went wrong, in the operating system's own words.</param>
public sealed record RegistryFailure(string Registry, string Message);

/// <summary>Everything on offer, plus every registry that could not be reached.</summary>
/// <param name="Entries">One entry per game, in game-id order.</param>
/// <param name="Failures">Registries that failed, as data: being offline is a normal state.</param>
public sealed record PackCatalogResult(
    IReadOnlyList<PackCatalogEntry> Entries,
    IReadOnlyList<RegistryFailure> Failures)
{
    /// <summary>How many days an old installed version stays after a newer one; zero or less keeps them all.</summary>
    public int RemoveOldVersionsAfterDays { get; init; } = PackPreferences.DefaultRemoveOldVersionsAfterDays;

    /// <summary>Whether the app installs a newer pack by itself when it finds one, from <c>packs.json</c>.</summary>
    public bool AutoUpdate { get; init; }

    /// <summary>Whether every configured registry failed.</summary>
    public bool IsOffline => Failures.Count > 0 && Entries.All(entry => entry.Registry is null);

    /// <summary>Finds one game's entry.</summary>
    /// <returns>The entry, or null when the catalogue has no such game.</returns>
    public PackCatalogEntry? Find(string gameId) =>
        Entries.FirstOrDefault(entry =>
            string.Equals(entry.GameId, gameId, StringComparison.OrdinalIgnoreCase));
}
