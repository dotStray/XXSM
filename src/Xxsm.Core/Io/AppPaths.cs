namespace Xxsm.Core.Io;

/// <summary>The default <see cref="IAppPaths"/>: XDG on Unix, the platform's own folders elsewhere.</summary>
/// <remarks>An <c>XXSM_*_HOME</c> variable replaces its root verbatim, with no <c>xxsm</c> appended.</remarks>
public sealed class AppPaths : IAppPaths
{
    /// <summary>Environment variable that overrides <see cref="ConfigDirectory"/> outright.</summary>
    public const string ConfigOverrideVariable = "XXSM_CONFIG_HOME";

    /// <summary>Environment variable that overrides <see cref="DataDirectory"/> outright.</summary>
    public const string DataOverrideVariable = "XXSM_DATA_HOME";

    /// <summary>Environment variable that overrides <see cref="CacheDirectory"/> outright.</summary>
    public const string CacheOverrideVariable = "XXSM_CACHE_HOME";

    /// <summary>Environment variable that overrides <see cref="StateDirectory"/> outright.</summary>
    public const string StateOverrideVariable = "XXSM_STATE_HOME";

    private readonly string _home;

    /// <summary>Creates an instance bound to the real process environment.</summary>
    public AppPaths()
        : this(Environment.GetEnvironmentVariable, ResolveHomeDirectory(Environment.GetEnvironmentVariable))
    {
    }

    /// <summary>Creates an instance over a supplied environment and home directory.</summary>
    /// <param name="environment">Looks up an environment variable by name; returns null when unset.</param>
    /// <param name="homeDirectory">The user's home directory.</param>
    public AppPaths(Func<string, string?> environment, string homeDirectory)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);

        _home = PathComparer.Normalize(homeDirectory);

        ConfigDirectory = Resolve(environment, ConfigOverrideVariable, "XDG_CONFIG_HOME", ".config", DefaultConfigBase);
        DataDirectory = Resolve(environment, DataOverrideVariable, "XDG_DATA_HOME", ".local/share", DefaultDataBase);
        CacheDirectory = Resolve(environment, CacheOverrideVariable, "XDG_CACHE_HOME", ".cache", DefaultCacheBase);
        StateDirectory = Resolve(environment, StateOverrideVariable, "XDG_STATE_HOME", ".local/state", DefaultStateBase);

        var xdgDataHome = FirstNonBlank(environment("XDG_DATA_HOME"), Combine(_home, ".local/share"));
        HomeTrashDirectory = Combine(xdgDataHome, "Trash");
    }

    /// <inheritdoc />
    public string ConfigDirectory { get; }

    /// <inheritdoc />
    public string DataDirectory { get; }

    /// <inheritdoc />
    public string CacheDirectory { get; }

    /// <inheritdoc />
    public string StateDirectory { get; }

    /// <inheritdoc />
    public string LogsDirectory => Combine(StateDirectory, "logs");

    /// <inheritdoc />
    public string PacksDirectory => Combine(DataDirectory, "packs");

    /// <inheritdoc />
    public string OverlaysDirectory => Combine(DataDirectory, "overlays");

    /// <inheritdoc />
    public string StudioDirectory => Combine(DataDirectory, "studio");

    /// <inheritdoc />
    public string DownloadsDirectory => Combine(CacheDirectory, "downloads");

    /// <inheritdoc />
    public string DownloadHistoryFile => Combine(StateDirectory, "downloads.json");

    /// <inheritdoc />
    public string SettingsFile => Combine(ConfigDirectory, "settings.json");

    /// <inheritdoc />
    public string TextFile => Combine(ConfigDirectory, "text.json");

    /// <inheritdoc />
    public string TextReferenceFile => Combine(ConfigDirectory, "text.reference.json");

    /// <inheritdoc />
    public string HomeTrashDirectory { get; }

    /// <inheritdoc />
    public void EnsureCreated()
    {
        foreach (var root in new[] { ConfigDirectory, DataDirectory, CacheDirectory, StateDirectory })
        {
            OwnFolder.Create(root);
        }

        foreach (var directory in new[]
                 {
                     ConfigDirectory,
                     DataDirectory,
                     CacheDirectory,
                     StateDirectory,
                     LogsDirectory,
                     PacksDirectory,
                     OverlaysDirectory,
                     StudioDirectory,
                     DownloadsDirectory,
                 })
        {
            Directory.CreateDirectory(directory);
        }
    }

    private string Resolve(
        Func<string, string?> environment,
        string overrideVariable,
        string xdgVariable,
        string unixRelativeDefault,
        Func<string> platformDefault)
    {
        var direct = environment(overrideVariable);
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return PathComparer.Normalize(Path.GetFullPath(direct));
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
        {
            var xdg = FirstNonBlank(environment(xdgVariable), Combine(_home, unixRelativeDefault));
            return Combine(xdg, AppInfo.Slug);
        }

        return Combine(platformDefault(), AppInfo.Slug);
    }

    private Func<string> DefaultConfigBase => () => OperatingSystem.IsMacOS()
        ? Combine(_home, "Library/Application Support")
        : SpecialFolder(Environment.SpecialFolder.ApplicationData);

    private Func<string> DefaultDataBase => () => OperatingSystem.IsMacOS()
        ? Combine(_home, "Library/Application Support")
        : SpecialFolder(Environment.SpecialFolder.ApplicationData);

    private Func<string> DefaultCacheBase => () => OperatingSystem.IsMacOS()
        ? Combine(_home, "Library/Caches")
        : SpecialFolder(Environment.SpecialFolder.LocalApplicationData);

    private Func<string> DefaultStateBase => () => OperatingSystem.IsMacOS()
        ? Combine(_home, "Library/Application Support")
        : SpecialFolder(Environment.SpecialFolder.LocalApplicationData);

    private string SpecialFolder(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
        return string.IsNullOrWhiteSpace(path) ? _home : PathComparer.Normalize(path);
    }

    private static string ResolveHomeDirectory(Func<string, string?> environment)
    {
        var home = environment("HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            return home;
        }

        var profile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.DoNotVerify);

        if (!string.IsNullOrWhiteSpace(profile))
        {
            return profile;
        }

        throw new InvalidOperationException(
            "Could not determine the user's home directory: neither HOME nor the user profile " +
            "folder is set. Set HOME, or set the XXSM_CONFIG_HOME, XXSM_DATA_HOME, " +
            "XXSM_CACHE_HOME and XXSM_STATE_HOME environment variables explicitly.");
    }

    private static string FirstNonBlank(string? first, string fallback) =>
        string.IsNullOrWhiteSpace(first) ? fallback : PathComparer.Normalize(first);

    private static string Combine(string left, string right) =>
        PathComparer.Normalize(Path.Combine(left, right));
}
