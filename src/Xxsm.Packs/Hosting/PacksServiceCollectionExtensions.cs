using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;
using Xxsm.Core;
using Xxsm.Core.GameBanana;
using Xxsm.Packs.Characters;
using Xxsm.Packs.Downloads;
using Xxsm.Packs.GameBanana;
using Xxsm.Packs.Importing;
using Xxsm.Packs.Installation;
using Xxsm.Packs.Loading;
using Xxsm.Packs.Merge;
using Xxsm.Packs.Overlays;
using Xxsm.Packs.Pictures;
using Xxsm.Packs.Portraits;
using Xxsm.Packs.Randomising;
using Xxsm.Packs.Registry;
using Xxsm.Packs.Sorting;
using Xxsm.Packs.Studio;
using Xxsm.Packs.Updates;

namespace Xxsm.Packs.Hosting;

/// <summary>Registers the Game Pack services.</summary>
public static class PacksServiceCollectionExtensions
{
    /// <summary>Adds the pack services: loading, overlays, installation, registry, sorting and the editor.</summary>
    /// <remarks>Needs <c>AddXxsmCore</c> as well, called before or after.</remarks>
    public static IServiceCollection AddXxsmPacks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IGamePackLoader, GamePackLoader>();
        services.TryAddSingleton<IOverlayStore, OverlayStore>();
        services.TryAddSingleton<IPackInstaller, PackInstaller>();
        services.TryAddSingleton<IModInstaller, ModInstaller>();
        services.TryAddSingleton<IGameDataService, GameDataService>();
        services.TryAddSingleton<PackUpdatePlanner>();
        services.TryAddSingleton<ISkippedUpdatesStore, SkippedUpdatesStore>();
        services.TryAddSingleton<IPackChangesStore, PackChangesStore>();
        services.TryAddSingleton<IPackChangesComparer, PackChangesComparer>();
        services.TryAddSingleton<IModSorter, ModSorter>();
        services.TryAddSingleton<ISortJournal, SortJournal>();
        services.TryAddSingleton<ISortRunner, SortRunner>();
        services.TryAddSingleton<IModRandomiser, ModRandomiser>();
        services.TryAddSingleton<IModImporter, ModImporter>();
        services.TryAddSingleton<IPackPreferencesStore, PackPreferencesStore>();
        services.TryAddSingleton<IRegistryClient, RegistryClient>();
        services.TryAddSingleton<IPackService, PackService>();
        services.TryAddSingleton<IInstalledPacks, InstalledPacks>();
        services.TryAddSingleton<IPortraitSource, PortraitSource>();
        services.TryAddSingleton<IGameIconSource, GameIconSource>();
        services.TryAddSingleton<ICharacterPortraitStore, CharacterPortraitStore>();
        services.TryAddSingleton<ICharacterEditor, CharacterEditor>();
        services.TryAddSingleton<IModHashLearner, ModHashLearner>();
        services.TryAddSingleton<IStudioDraftStore, StudioDraftStore>();
        services.TryAddSingleton<IHashAssetsImporter, HashAssetsImporter>();
        services.TryAddSingleton<IModsFolderImporter, ModsFolderImporter>();
        services.TryAddSingleton<IPortraitFolderImporter, PortraitFolderImporter>();
        services.TryAddSingleton<ISpreadsheetImporter, SpreadsheetImporter>();
        services.TryAddSingleton<IPackExporter, PackExporter>();
        services.TryAddSingleton<IPackTrial, PackTrial>();
        services.TryAddSingleton<IHashIgnoreCost, HashIgnoreCost>();
        services.TryAddSingleton<IOverlayPromotion, OverlayPromotion>();
        services.TryAddSingleton<IOverlayRedundancy, OverlayRedundancy>();
        services.TryAddSingleton<IPackPublisher, PackPublisher>();
        services.TryAddSingleton<IGameBananaCache, GameBananaCache>();
        services.TryAddSingleton<IModEnrichment, ModEnrichmentService>();
        services.TryAddSingleton<IModUpdateChecker, ModUpdateChecker>();
        services.TryAddSingleton<IGameBananaInstallSource, GameBananaInstallSource>();
        services.TryAddSingleton<IDownloadStore, DownloadStore>();
        services.TryAddSingleton<IDownloadManager, DownloadManager>();
        services.TryAddSingleton<IModCopies, ModCopyFinder>();
        services.TryAddSingleton<IModUpdater, ModUpdater>();

        services.AddHttpClient(nameof(PackResourceFetcher), client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
            client.Timeout = TimeSpan.FromMinutes(10);
        })
            .ConfigurePrimaryHttpMessageHandler(FreshConnections);

        services.AddHttpClient(nameof(PictureDownloader), client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
        })
            .ConfigurePrimaryHttpMessageHandler(FreshConnections);

        // Infinite on purpose: GameBananaClient sets its own timeout per request.
        services.AddHttpClient(nameof(GameBananaClient), client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
            client.Timeout = Timeout.InfiniteTimeSpan;
        })
            .ConfigurePrimaryHttpMessageHandler(FreshConnections);

        services.AddHttpClient(nameof(AppUpdateChecker), client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
            client.Timeout = TimeSpan.FromSeconds(30);
        })
            .ConfigurePrimaryHttpMessageHandler(FreshConnections);

        services.TryAddSingleton<IAppUpdateChecker>(provider => new AppUpdateChecker(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(AppUpdateChecker))));

        services.TryAddSingleton<IPictureDownloader>(provider => new PictureDownloader(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(PictureDownloader)),
            provider.GetRequiredService<ILogger>(),
            cancellationToken => provider.GetRequiredService<IGameBananaClient>().WaitForTurnAsync(cancellationToken)));

        services.TryAddSingleton<IGameBananaClient>(provider => new GameBananaClient(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(GameBananaClient)),
            provider.GetRequiredService<IGameBananaCache>(),
            provider.GetRequiredService<Xxsm.Core.Settings.IAppSettingsStore>(),
            provider.GetRequiredService<ILogger>(),
            provider.GetRequiredService<TimeProvider>()));

        services.TryAddSingleton<IPackResourceFetcher>(provider => new PackResourceFetcher(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(PackResourceFetcher)),
            provider.GetRequiredService<ILogger>()));

        return services;
    }

    /// <summary>A handler whose connections last five minutes, so a moved site is found without a restart.</summary>
    private static HttpMessageHandler FreshConnections() =>
        new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
}
