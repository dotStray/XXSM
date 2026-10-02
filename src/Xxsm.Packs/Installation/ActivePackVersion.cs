namespace Xxsm.Packs.Installation;

/// <summary>Which installed version of a game's pack is in use: the pinned one if installed, else the newest.</summary>
public static class ActivePackVersion
{
    /// <summary>Chooses the version in use.</summary>
    /// <param name="installedNewestFirst">The game's installed versions, newest first.</param>
    /// <param name="pinnedVersion">The version the user pinned, or null.</param>
    /// <returns>The version in use, or null when none is installed.</returns>
    public static string? Choose(IReadOnlyList<string> installedNewestFirst, string? pinnedVersion)
    {
        ArgumentNullException.ThrowIfNull(installedNewestFirst);

        if (pinnedVersion is { Length: > 0 }
            && installedNewestFirst.FirstOrDefault(version =>
                string.Equals(version, pinnedVersion, StringComparison.OrdinalIgnoreCase)) is { } held)
        {
            return held;
        }

        return installedNewestFirst.Count > 0 ? installedNewestFirst[0] : null;
    }

    /// <summary>The pin that makes one installed version the one in use: none for the newest, else that one.</summary>
    /// <param name="installedNewestFirst">The game's installed versions, newest first.</param>
    /// <param name="version">The version to use. Must be installed.</param>
    /// <returns>The pin to save, or null for none.</returns>
    public static string? PinToUse(IReadOnlyList<string> installedNewestFirst, string version)
    {
        ArgumentNullException.ThrowIfNull(installedNewestFirst);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        return installedNewestFirst.Count > 0
               && string.Equals(installedNewestFirst[0], version, StringComparison.OrdinalIgnoreCase)
            ? null
            : version;
    }
}
