namespace Xxsm.Cli.Output;

/// <summary>The machine-readable payload of <c>xxsm doctor</c>.</summary>
/// <param name="Version">The running application version.</param>
/// <param name="Runtime">The .NET runtime description.</param>
/// <param name="OperatingSystem">The OS description.</param>
/// <param name="RuntimeIdentifier">The .NET runtime identifier, for example <c>linux-x64</c>.</param>
/// <param name="ConfigDirectory">Resolved configuration root.</param>
/// <param name="DataDirectory">Resolved data root.</param>
/// <param name="CacheDirectory">Resolved cache root.</param>
/// <param name="StateDirectory">Resolved state root.</param>
/// <param name="LogsDirectory">Resolved logs directory.</param>
/// <param name="PacksDirectory">Where installed Game Packs live.</param>
/// <param name="OverlaysDirectory">Where user overlays live.</param>
/// <param name="StudioDirectory">Where Pack Studio drafts live.</param>
/// <param name="SettingsFile">The settings file path.</param>
/// <param name="HomeTrashDirectory">The freedesktop home trash directory.</param>
/// <param name="HomeVolumeRoot">The mount point the home trash is on.</param>
/// <param name="UserId">The Unix user id, or null when it could not be read.</param>
/// <param name="DefaultRegistryUrl">The registry consulted when the user configures none.</param>
public sealed record DiagnosticsReport(
    string Version,
    string Runtime,
    string OperatingSystem,
    string RuntimeIdentifier,
    string ConfigDirectory,
    string DataDirectory,
    string CacheDirectory,
    string StateDirectory,
    string LogsDirectory,
    string PacksDirectory,
    string OverlaysDirectory,
    string StudioDirectory,
    string SettingsFile,
    string HomeTrashDirectory,
    string HomeVolumeRoot,
    int? UserId,
    string DefaultRegistryUrl);
