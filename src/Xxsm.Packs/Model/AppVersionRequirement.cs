namespace Xxsm.Packs.Model;

/// <summary>Compares a pack's <c>minAppVersion</c> with the running XXSM's version.</summary>
public static class AppVersionRequirement
{
    /// <summary>Whether <paramref name="running"/> is older than <paramref name="minimum"/>.</summary>
    /// <param name="minimum">The pack's <c>minAppVersion</c>; null, empty or unreadable asks for nothing.</param>
    /// <param name="running">The running version, such as <c>1.0.0</c>.</param>
    public static bool IsOlderThan(string running, string? minimum) =>
        minimum is { Length: > 0 } &&
        Version.TryParse(Core(minimum), out var required) &&
        Version.TryParse(Core(running), out var current) &&
        current < required;

    /// <summary>The number a version starts with, before any <c>-beta</c> or <c>+build</c>.</summary>
    private static string Core(string version) => version.Split('-', '+')[0].Trim();
}
