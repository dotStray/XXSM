using System.Reflection;

namespace Xxsm.Core;

/// <summary>Facts about the running application: its version, its network identity and its non-game defaults.</summary>
public static class AppInfo
{
    /// <summary>The short name for XXSM's folders, <c>~/.config/xxsm</c> and each mod's <c>.xxsm/</c>.</summary>
    public const string Slug = "xxsm";

    /// <summary>The name shown to users.</summary>
    public const string DisplayName = "XXSM";

    /// <summary>The registry <c>index.json</c> used when the user has configured none of their own.</summary>
    public const string DefaultRegistryUrl =
        "https://raw.githubusercontent.com/dotStray/xxsm-presets/main/index.json";

    /// <summary>The repository the default registry is published from, where a Studio pack can be offered.</summary>
    public const string PresetsRepositoryUrl = "https://github.com/dotStray/xxsm-presets";

    /// <summary>The page a user downloads XXSM from: its newest release.</summary>
    public const string LatestReleaseUrl = "https://github.com/dotStray/XXSM/releases/latest";

    /// <summary>GitHub's description of the newest release, which the update check reads.</summary>
    public const string LatestReleaseApiUrl = "https://api.github.com/repos/dotStray/XXSM/releases/latest";

    /// <summary>The informational version, with any source-control suffix: <c>1.0.0+3f2a1c9</c>.</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    /// <summary>The version without build metadata, <c>1.0.0</c>, as a pack's <c>minAppVersion</c> is.</summary>
    public static string ShortVersion { get; } = Version.Split('+')[0];

    /// <summary>The HTTP <c>User-Agent</c> XXSM sends: its name and version.</summary>
    public static string UserAgent { get; } = $"{DisplayName}/{ShortVersion}";
}
