using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xxsm.Core.Archives;
using Xxsm.Core.Ini;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Core.Profiles;
using Xxsm.Core.Settings;
using Xxsm.Core.Text;

namespace Xxsm.Core.Hosting;

/// <summary>Registers every <c>Xxsm.Core</c> service, for the CLI and the desktop app alike.</summary>
public static class CoreServiceCollectionExtensions
{
    /// <summary>Adds the core services, with <c>TryAdd</c>, so a fake registered first wins.</summary>
    /// <param name="services">The container being built.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddXxsmCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAppPaths, AppPaths>();
        services.TryAddSingleton<IVolumeResolver, DriveInfoVolumeResolver>();
        services.TryAddSingleton<IUserIdProvider, ProcUserIdProvider>();
        services.TryAddSingleton<ITrashService, FreedesktopTrashService>();
        services.TryAddSingleton<IFolderLauncher, SystemFolderLauncher>();
        services.TryAddSingleton<IUrlLauncher, SystemUrlLauncher>();
        services.TryAddSingleton<IIniFileService, IniFileService>();
        services.TryAddSingleton<IKeySwapService, KeySwapService>();
        services.TryAddSingleton<ISavedSettingsService, SavedSettingsService>();
        services.TryAddSingleton<IIniOriginalsService, IniOriginalsService>();
        services.TryAddSingleton<IModArchiveReader, ModArchiveReader>();
        services.TryAddSingleton<IModSignalExtractor, ModSignalExtractor>();
        services.TryAddSingleton<IModConfigStore, ModConfigStore>();
        services.TryAddSingleton<IModPreviewSource, ModPreviewSource>();
        services.TryAddSingleton<IModPreviewEditor, ModPreviewEditor>();
        services.TryAddSingleton<IModRepository, ModRepository>();
        services.TryAddSingleton<IModFileOperations, ModFileOperations>();
        services.TryAddSingleton<IModsFolderLeftovers, ModsFolderLeftovers>();
        services.TryAddSingleton<IModFiling, ModFiling>();
        services.TryAddSingleton<IModSwitcher, ModSwitcher>();
        services.TryAddSingleton<IModExporter, ModExporter>();
        services.TryAddSingleton<IProfileService, ProfileService>();
        services.TryAddSingleton<IModsFolderWatcher, ModsFolderWatcher>();
        services.TryAddSingleton<IModsFolderProbe, ModsFolderProbe>();
        services.TryAddSingleton<IAppSettingsStore, AppSettingsStore>();
        services.TryAddSingleton(TextEditing.ForThisBuild);
        services.TryAddSingleton<ITextOverrideStore, TextOverrideStore>();
        services.TryAddSingleton<IFactoryReset, FactoryReset>();
        services.TryAddSingleton<IAppRelauncher, ProcessRelauncher>();

        return services;
    }
}
