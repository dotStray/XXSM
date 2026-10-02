namespace Xxsm.Core.Io;

/// <summary>The application's directories: everything XXSM writes outside a Mods folder is under one.</summary>
public interface IAppPaths
{
    /// <summary>Configuration root. Linux: <c>$XDG_CONFIG_HOME/xxsm</c>.</summary>
    string ConfigDirectory { get; }

    /// <summary>Data root — packs and overlays. Linux: <c>$XDG_DATA_HOME/xxsm</c>.</summary>
    string DataDirectory { get; }

    /// <summary>Cache root — API responses, thumbnails, downloads. Linux: <c>$XDG_CACHE_HOME/xxsm</c>.</summary>
    string CacheDirectory { get; }

    /// <summary>State root. Linux: <c>$XDG_STATE_HOME/xxsm</c>.</summary>
    string StateDirectory { get; }

    /// <summary>Rolling log files. <c>&lt;state&gt;/logs</c>.</summary>
    string LogsDirectory { get; }

    /// <summary>Installed Game Packs, one directory per game then per version. <c>&lt;data&gt;/packs</c>.</summary>
    string PacksDirectory { get; }

    /// <summary>User overlays, one JSON file per game: the only irreplaceable files.</summary>
    string OverlaysDirectory { get; }

    /// <summary>Pack Studio drafts, one directory per game in progress. <c>&lt;data&gt;/studio</c>.</summary>
    string StudioDirectory { get; }

    /// <summary>Downloaded pack archives awaiting install. <c>&lt;cache&gt;/downloads</c>.</summary>
    string DownloadsDirectory { get; }

    /// <summary>What has been downloaded, and what became of each one. <c>&lt;state&gt;/downloads.json</c>.</summary>
    string DownloadHistoryFile { get; }

    /// <summary>The application settings file. <c>&lt;config&gt;/settings.json</c>.</summary>
    string SettingsFile { get; }

    /// <summary>Hand-edited replacements for the wording, <c>text.json</c>. Never written by XXSM.</summary>
    string TextFile { get; }

    /// <summary>Every text with its built-in wording, <c>text.reference.json</c>, rewritten each launch.</summary>
    string TextReferenceFile { get; }

    /// <summary>The shared freedesktop trash for the home volume, normally <c>~/.local/share/Trash</c>.</summary>
    string HomeTrashDirectory { get; }

    /// <summary>Creates every directory XXSM writes to, if missing, but not the shared trash.</summary>
    void EnsureCreated();
}
