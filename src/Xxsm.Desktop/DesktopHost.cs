using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;
using Serilog.Events;
using Xxsm.Core.GameBanana;
using Xxsm.Core.Hosting;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Core.Settings;
using Xxsm.Core.Text;
using Xxsm.Desktop.Services;
using Xxsm.Desktop.ViewModels;
using Xxsm.Desktop.ViewModels.FirstRun;
using Xxsm.Desktop.ViewModels.Pages;
using Xxsm.Packs.Characters;
using Xxsm.Packs.GameBanana;
using Xxsm.Packs.Hosting;
using Xxsm.Packs.Installation;
using Xxsm.Packs.Loading;
using Xxsm.Packs.Merge;
using Xxsm.Packs.Registry;
using Xxsm.Packs.Sorting;
using Xxsm.Packs.Studio;

namespace Xxsm.Desktop;

/// <summary>Builds the desktop application's service container, from the same services as the CLI.</summary>
public static class DesktopHost
{
    /// <summary>Builds the provider. The caller owns it and must dispose it.</summary>
    /// <param name="application">The running application, for the theme and the file picker.</param>
    /// <param name="topLevel">Resolves the window to parent dialogs on.</param>
    /// <param name="restart">Closes the window and opens XXSM again, to finish a reset.</param>
    public static ServiceProvider Build(Application application, Func<TopLevel?> topLevel, Action restart)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(topLevel);
        ArgumentNullException.ThrowIfNull(restart);

        var paths = new AppPaths();
        paths.EnsureCreated();

        var logger = XxsmLogging.Create(
            paths,
            minimumLevel: LogEventLevel.Information,
            consoleLevel: LogEventLevel.Warning);

        var crashes = XxsmLogging.RecordCrashes(logger);

        // Written down while the window that caused it is still there; the process then ends as before.
        Dispatcher.UIThread.UnhandledException += (_, e) => crashes.RecordCrash(e.Exception);

        var services = new ServiceCollection();

        services.AddSingleton<IAppPaths>(paths);
        services.AddSingleton<ILogger>(logger);
        services.AddSingleton(logger);
        services.AddXxsmCore();
        services.AddXxsmPacks();
        services.AddXxsmDesktopPlatform(application, topLevel, restart);
        services.AddXxsmDesktop();

        return services.BuildServiceProvider(validateScopes: true);
    }

    /// <summary>Registers the services that need a real window: theme, pickers, clipboards, UI thread.</summary>
    /// <remarks>Separate, so a headless test can register its own and still build the real view models.</remarks>
    /// <param name="services">The container being built.</param>
    /// <param name="application">The running application.</param>
    /// <param name="topLevel">Resolves the window to parent dialogs on.</param>
    /// <param name="restart">Closes the window and opens XXSM again.</param>
    public static IServiceCollection AddXxsmDesktopPlatform(
        this IServiceCollection services, Application application, Func<TopLevel?> topLevel, Action restart)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(topLevel);
        ArgumentNullException.ThrowIfNull(restart);

        services.TryAddSingleton<IThemeApplier>(new ThemeApplier(application));
        services.TryAddSingleton<IStoragePicker>(new StorageProviderPicker(topLevel));
        services.TryAddSingleton<IClipboardImageReader>(new AvaloniaClipboardImageReader(topLevel));
        services.TryAddSingleton<IClipboardTextReader>(new AvaloniaClipboardTextReader(topLevel));
        services.TryAddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.TryAddSingleton<IApplicationRestarter>(new ApplicationRestarter(restart));

        return services;
    }

    /// <summary>Registers the desktop's own services and view models; pages are singletons that deactivate.</summary>
    public static IServiceCollection AddXxsmDesktop(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Read once, here: headings are read when a view model is built.
        services.AddSingleton<TextCatalogue>();
        services.AddSingleton<ITextCatalogue>(p => p.GetRequiredService<TextCatalogue>());

        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<ViewModelWorkRunner>();
        services.AddSingleton<GameContext>();
        services.AddSingleton<StudioVisibility>();
        services.AddSingleton<ColumnWidthMemory>();
        services.AddSingleton<IPortraitCache, PortraitCache>();
        services.AddSingleton<IStudioThumbnailCache, StudioThumbnailCache>();
        services.AddSingleton<IGameIconProvider, GameIconProvider>();
        services.AddSingleton<IModPreviewCache, ModPreviewCache>();
        services.AddSingleton<IModThumbnailCache, ModThumbnailCache>();

        services.AddSingleton(provider => new UpdateCheckRun(
            provider.GetRequiredService<IModUpdateChecker>(),
            provider.GetRequiredService<INotificationService>(),
            provider.GetRequiredService<ViewModelWorkRunner>(),
            provider.GetRequiredService<ITextCatalogue>(),
            ct => provider.GetRequiredService<MainWindowViewModel>().RescanModsAsync(ct),
            () => provider.GetRequiredService<MainWindowViewModel>().ShowMods()));

        services.AddSingleton(provider => new SwitchRunNotices(
            provider.GetRequiredService<IModSwitcher>(),
            provider.GetRequiredService<INotificationService>(),
            provider.GetRequiredService<ViewModelWorkRunner>(),
            provider.GetRequiredService<ITextCatalogue>(),
            ct => provider.GetRequiredService<MainWindowViewModel>().RescanModsAsync(ct)));

        services.AddSingleton(provider => new RandomiserViewModel(
            provider.GetRequiredService<GameContext>(),
            provider.GetRequiredService<Xxsm.Packs.Randomising.IModRandomiser>(),
            provider.GetRequiredService<SwitchRunNotices>(),
            provider.GetRequiredService<ViewModelWorkRunner>(),
            provider.GetRequiredService<ITextCatalogue>(),
            ct => provider.GetRequiredService<MainWindowViewModel>().RescanModsAsync(ct)));

        services.AddSingleton(provider => new ModExportViewModel(
            provider.GetRequiredService<GameContext>(),
            provider.GetRequiredService<IModExporter>(),
            provider.GetRequiredService<IStoragePicker>(),
            provider.GetRequiredService<IFolderLauncher>(),
            provider.GetRequiredService<INotificationService>(),
            provider.GetRequiredService<IUiDispatcher>(),
            provider.GetRequiredService<ViewModelWorkRunner>(),
            provider.GetRequiredService<ITextCatalogue>()));

        services.AddSingleton(provider => new ProfilesPageViewModel(
            provider.GetRequiredService<GameContext>(),
            provider.GetRequiredService<Xxsm.Core.Profiles.IProfileService>(),
            provider.GetRequiredService<SwitchRunNotices>(),
            provider.GetRequiredService<INotificationService>(),
            provider.GetRequiredService<ViewModelWorkRunner>(),
            provider.GetRequiredService<ITextCatalogue>(),
            ct => provider.GetRequiredService<MainWindowViewModel>().RescanModsAsync(ct),
            provider.GetRequiredService<IModThumbnailCache>(),
            modFolder => provider.GetRequiredService<MainWindowViewModel>().GoToMod(modFolder)));

        services.AddSingleton(provider => new NotificationsPageViewModel(
            provider.GetRequiredService<INotificationService>(),
            provider.GetRequiredService<GameContext>(),
            provider.GetRequiredService<IModFileOperations>(),
            provider.GetRequiredService<IModsFolderLeftovers>(),
            provider.GetRequiredService<IFolderLauncher>(),
            provider.GetRequiredService<ViewModelWorkRunner>(),
            provider.GetRequiredService<ITextCatalogue>(),
            ct => provider.GetRequiredService<MainWindowViewModel>().RescanModsAsync(ct),
            () => provider.GetRequiredService<MainWindowViewModel>().ShowMods()));

        services.AddSingleton(provider => new SortReviewViewModel(
            provider.GetRequiredService<GameContext>(),
            provider.GetRequiredService<ISortRunner>(),
            provider.GetRequiredService<IAppSettingsStore>(),
            provider.GetRequiredService<INotificationService>(),
            provider.GetRequiredService<ViewModelWorkRunner>(),
            provider.GetRequiredService<ITextCatalogue>(),
            ct => provider.GetRequiredService<MainWindowViewModel>().RescanModsAsync(ct)));

        services.AddSingleton(provider => new SkippedUpdatesViewModel(
            provider.GetRequiredService<GameContext>(),
            provider.GetRequiredService<ICharacterEditor>(),
            provider.GetRequiredService<ISkippedUpdatesStore>(),
            provider.GetRequiredService<ViewModelWorkRunner>(),
            provider.GetRequiredService<ITextCatalogue>(),
            ct => provider.GetRequiredService<MainWindowViewModel>().ReloadGameAsync(ct)));

        services.AddSingleton(provider => new GameBananaFillViewModel(
            provider.GetRequiredService<IGameBananaClient>(),
            provider.GetRequiredService<IModEnrichment>(),
            provider.GetRequiredService<IAppSettingsStore>(),
            provider.GetRequiredService<INotificationService>(),
            provider.GetRequiredService<ViewModelWorkRunner>(),
            provider.GetRequiredService<ITextCatalogue>(),
            provider.GetRequiredService<IUrlLauncher>(),
            ct => provider.GetRequiredService<MainWindowViewModel>().RescanModsAsync(ct)));

        services.AddSingleton(provider => new ModUpdateViewModel(
            provider.GetRequiredService<Xxsm.Packs.GameBanana.IModUpdater>(),
            provider.GetRequiredService<Xxsm.Packs.Downloads.IDownloadManager>(),
            provider.GetRequiredService<GameContext>(),
            provider.GetRequiredService<INotificationService>(),
            provider.GetRequiredService<ViewModelWorkRunner>(),
            provider.GetRequiredService<IUrlLauncher>(),
            provider.GetRequiredService<IUiDispatcher>(),
            provider.GetRequiredService<ITextCatalogue>(),
            ct => provider.GetRequiredService<MainWindowViewModel>().RescanModsAsync(ct)));

        services.AddSingleton(provider => new ModInstallViewModel(
            provider.GetRequiredService<GameContext>(),
            provider.GetRequiredService<IModInstaller>(),
            provider.GetRequiredService<IAppSettingsStore>(),
            provider.GetRequiredService<INotificationService>(),
            provider.GetRequiredService<ViewModelWorkRunner>(),
            provider.GetRequiredService<ITextCatalogue>(),
            provider.GetRequiredService<IClipboardImageReader>(),
            provider.GetRequiredService<IClipboardTextReader>(),
            provider.GetRequiredService<IStoragePicker>(),
            provider.GetRequiredService<Xxsm.Packs.Pictures.IPictureDownloader>(),
            provider.GetRequiredService<IGameBananaClient>(),
            provider.GetRequiredService<IGameBananaInstallSource>(),
            provider.GetRequiredService<IUrlLauncher>(),
            provider.GetRequiredService<Xxsm.Packs.Downloads.IDownloadManager>(),
            provider.GetRequiredService<Xxsm.Packs.GameBanana.IModCopies>(),
            provider.GetRequiredService<IUiDispatcher>(),
            provider.GetRequiredService<ILogger>(),
            ct => provider.GetRequiredService<MainWindowViewModel>().RescanModsAsync(ct),
            modFolder => provider.GetRequiredService<MainWindowViewModel>().GoToMod(modFolder)));

        services.AddSingleton(provider => new CharacterManagerViewModel(
            provider.GetRequiredService<GameContext>(),
            provider.GetRequiredService<ICharacterEditor>(),
            provider.GetRequiredService<IModHashLearner>(),
            provider.GetRequiredService<IStoragePicker>(),
            provider.GetRequiredService<IClipboardImageReader>(),
            provider.GetRequiredService<Xxsm.Packs.Portraits.ICharacterPortraitStore>(),
            provider.GetRequiredService<Xxsm.Packs.Pictures.IPictureDownloader>(),
            provider.GetRequiredService<IPortraitCache>(),
            provider.GetRequiredService<INotificationService>(),
            provider.GetRequiredService<ViewModelWorkRunner>(),
            provider.GetRequiredService<ITextCatalogue>(),
            provider.GetRequiredService<ILogger>(),
            ct => provider.GetRequiredService<MainWindowViewModel>().RescanModsAsync(ct),
            ct => provider.GetRequiredService<MainWindowViewModel>().ReloadGameAsync(ct),
            provider.GetRequiredService<IModThumbnailCache>()));

        // Pages take a delegate for the shell, not the shell itself, which would be a cycle.
        services.AddSingleton(provider => new CharactersPageViewModel(
            provider.GetRequiredService<GameContext>(),
            provider.GetRequiredService<IFolderLauncher>(),
            provider.GetRequiredService<IModFileOperations>(),
            provider.GetRequiredService<IAppSettingsStore>(),
            provider.GetRequiredService<IPortraitCache>(),
            provider.GetRequiredService<INotificationService>(),
            provider.GetRequiredService<ViewModelWorkRunner>(),
            provider.GetRequiredService<ICharacterEditor>(),
            provider.GetRequiredService<SortReviewViewModel>(),
            provider.GetRequiredService<ModInstallViewModel>(),
            provider.GetRequiredService<CharacterManagerViewModel>(),
            provider.GetRequiredService<PackInstallFollowUp>(),
            provider.GetRequiredService<ITextCatalogue>(),
            provider.GetRequiredService<IGameIconProvider>(),
            tile => provider.GetRequiredService<MainWindowViewModel>().OpenCharacterDetail(tile),
            ct => provider.GetRequiredService<MainWindowViewModel>().RescanModsAsync(ct),
            ct => provider.GetRequiredService<MainWindowViewModel>().ReloadGameAsync(ct)));

        services.AddSingleton(provider => new CharacterDetailPageViewModel(
            provider.GetRequiredService<GameContext>(),
            provider.GetRequiredService<IModFileOperations>(),
            provider.GetRequiredService<IModConfigStore>(),
            provider.GetRequiredService<IModFiling>(),
            provider.GetRequiredService<IStoragePicker>(),
            provider.GetRequiredService<IFolderLauncher>(),
            provider.GetRequiredService<IUrlLauncher>(),
            provider.GetRequiredService<IPortraitCache>(),
            provider.GetRequiredService<IModPreviewCache>(),
            provider.GetRequiredService<IModPreviewSource>(),
            provider.GetRequiredService<IModPreviewEditor>(),
            provider.GetRequiredService<IClipboardImageReader>(),
            provider.GetRequiredService<Xxsm.Packs.Pictures.IPictureDownloader>(),
            provider.GetRequiredService<IAppSettingsStore>(),
            provider.GetRequiredService<IModUpdateChecker>(),
            provider.GetRequiredService<INotificationService>(),
            provider.GetRequiredService<ViewModelWorkRunner>(),
            provider.GetRequiredService<ModInstallViewModel>(),
            provider.GetRequiredService<SortReviewViewModel>(),
            provider.GetRequiredService<CharacterManagerViewModel>(),
            provider.GetRequiredService<GameBananaFillViewModel>(),
            provider.GetRequiredService<ModUpdateViewModel>(),
            provider.GetRequiredService<ITextCatalogue>(),
            provider.GetRequiredService<ColumnWidthMemory>(),
            ct => provider.GetRequiredService<MainWindowViewModel>().RescanModsAsync(ct),
            () => provider.GetRequiredService<MainWindowViewModel>().CloseCharacterDetail(),
            provider.GetRequiredService<Xxsm.Core.Ini.IKeySwapService>(),
            provider.GetRequiredService<Xxsm.Core.Profiles.IProfileService>(),
            provider.GetRequiredService<Xxsm.Core.Ini.ISavedSettingsService>()));

        services.AddSingleton(provider => new DownloadsViewModel(
            provider.GetRequiredService<Xxsm.Packs.Downloads.IDownloadManager>(),
            provider.GetRequiredService<ModInstallViewModel>(),
            provider.GetRequiredService<INotificationService>(),
            provider.GetRequiredService<ViewModelWorkRunner>(),
            provider.GetRequiredService<IUrlLauncher>(),
            provider.GetRequiredService<IUiDispatcher>(),
            provider.GetRequiredService<ITextCatalogue>(),
            provider.GetRequiredService<GameContext>(),
            provider.GetRequiredService<ILogger>()));

        services.AddSingleton(provider => new ModsPageViewModel(
            provider.GetRequiredService<GameContext>(),
            provider.GetRequiredService<IModsFolderWatcher>(),
            provider.GetRequiredService<IUiDispatcher>(),
            provider.GetRequiredService<ViewModelWorkRunner>(),
            provider.GetRequiredService<SortReviewViewModel>(),
            provider.GetRequiredService<ModInstallViewModel>(),
            provider.GetRequiredService<DownloadsViewModel>(),
            provider.GetRequiredService<IAppSettingsStore>(),
            provider.GetRequiredService<IModUpdateChecker>(),
            provider.GetRequiredService<IUrlLauncher>(),
            provider.GetRequiredService<INotificationService>(),
            provider.GetRequiredService<ITextCatalogue>(),
            ct => provider.GetRequiredService<MainWindowViewModel>().RescanModsAsync(ct),
            modFolder => provider.GetRequiredService<MainWindowViewModel>().GoToMod(modFolder),
            provider.GetRequiredService<ModUpdateViewModel>(),
            provider.GetRequiredService<RandomiserViewModel>(),
            provider.GetRequiredService<ModExportViewModel>(),
            provider.GetRequiredService<ModImportViewModel>(),
            provider.GetRequiredService<CharacterManagerViewModel>(),
            provider.GetRequiredService<Xxsm.Core.Profiles.IProfileService>(),
            provider.GetRequiredService<SwitchRunNotices>(),
            provider.GetRequiredService<UpdateCheckRun>()));

        services.AddSingleton(provider => new ModImportViewModel(
            provider.GetRequiredService<GameContext>(),
            provider.GetRequiredService<Xxsm.Packs.Importing.IModImporter>(),
            provider.GetRequiredService<SortReviewViewModel>(),
            provider.GetRequiredService<CharacterManagerViewModel>(),
            provider.GetRequiredService<INotificationService>(),
            provider.GetRequiredService<ViewModelWorkRunner>(),
            provider.GetRequiredService<ITextCatalogue>(),
            ct => provider.GetRequiredService<MainWindowViewModel>().RescanModsAsync(ct)));

        services.AddSingleton(provider => new CaughtUpCorrectionsViewModel(
            provider.GetRequiredService<Xxsm.Packs.Overlays.IOverlayRedundancy>(),
            provider.GetRequiredService<INotificationService>(),
            provider.GetRequiredService<ViewModelWorkRunner>(),
            provider.GetRequiredService<ITextCatalogue>(),
            ct => provider.GetRequiredService<MainWindowViewModel>().ReloadGameAsync(ct)));

        services.AddSingleton<WhatsNewViewModel>();

        services.TryAddSingleton(UpdateSchedule.Daily);
        services.AddSingleton(provider => new PackUpdateWatcher(
            provider.GetRequiredService<IPackService>(),
            provider.GetRequiredService<PackInstallFollowUp>(),
            provider.GetRequiredService<INotificationService>(),
            provider.GetRequiredService<ITextCatalogue>(),
            provider.GetRequiredService<UpdateSchedule>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger>(),
            ct => provider.GetRequiredService<MainWindowViewModel>().ReloadGamesAsync(ct),
            () => provider.GetRequiredService<MainWindowViewModel>().ShowPacks()));

        services.AddSingleton<PackInstallFollowUp>();

        services.AddSingleton(provider => new AppUpdateWatcher(
            provider.GetRequiredService<Xxsm.Packs.Updates.IAppUpdateChecker>(),
            provider.GetRequiredService<IAppSettingsStore>(),
            provider.GetRequiredService<INotificationService>(),
            provider.GetRequiredService<ViewModelWorkRunner>(),
            provider.GetRequiredService<IUrlLauncher>(),
            provider.GetRequiredService<ITextCatalogue>(),
            provider.GetRequiredService<UpdateSchedule>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger>()));

        services.AddSingleton(provider => new PacksPageViewModel(
            provider.GetRequiredService<IPackService>(),
            provider.GetRequiredService<Xxsm.Packs.Installation.IInstalledPacks>(),
            provider.GetRequiredService<INotificationService>(),
            provider.GetRequiredService<ViewModelWorkRunner>(),
            provider.GetRequiredService<PackInstallFollowUp>(),
            provider.GetRequiredService<ISkippedUpdatesStore>(),
            provider.GetRequiredService<IPackChangesStore>(),
            provider.GetRequiredService<PackUpdateWatcher>(),
            provider.GetRequiredService<IStoragePicker>(),
            provider.GetRequiredService<ITextCatalogue>(),
            provider.GetRequiredService<IGameIconProvider>(),
            provider.GetRequiredService<StudioVisibility>(),
            provider.GetRequiredService<IUiDispatcher>(),
            ct => provider.GetRequiredService<MainWindowViewModel>().ReloadGamesAsync(ct),
            gameId => provider.GetRequiredService<MainWindowViewModel>().EditInStudioAsync(gameId)));

        services.AddSingleton(provider => new SettingsPageViewModel(
            provider.GetRequiredService<GameContext>(),
            provider.GetRequiredService<IAppSettingsStore>(),
            provider.GetRequiredService<IPackPreferencesStore>(),
            provider.GetRequiredService<IPackService>(),
            provider.GetRequiredService<IModsFolderProbe>(),
            provider.GetRequiredService<IStoragePicker>(),
            provider.GetRequiredService<IThemeApplier>(),
            provider.GetRequiredService<INotificationService>(),
            provider.GetRequiredService<ViewModelWorkRunner>(),
            provider.GetRequiredService<IAppPaths>(),
            provider.GetRequiredService<ITextCatalogue>(),
            provider.GetRequiredService<ITextOverrideStore>(),
            provider.GetRequiredService<IFolderLauncher>(),
            provider.GetRequiredService<IGameIconProvider>(),
            provider.GetRequiredService<StudioVisibility>(),
            provider.GetRequiredService<UpdateCheckRun>(),
            provider.GetRequiredService<Xxsm.Packs.Downloads.IDownloadManager>(),
            provider.GetRequiredService<IFactoryReset>(),
            provider.GetRequiredService<IApplicationRestarter>(),
            ct => provider.GetRequiredService<MainWindowViewModel>().ReloadGameAsync(ct),
            ct => provider.GetRequiredService<MainWindowViewModel>().RescanModsAsync(ct)));

        services.AddSingleton(provider => new FirstRunViewModel(
            provider.GetRequiredService<IPackService>(),
            provider.GetRequiredService<IAppSettingsStore>(),
            provider.GetRequiredService<IModsFolderProbe>(),
            provider.GetRequiredService<IStoragePicker>(),
            provider.GetRequiredService<INotificationService>(),
            provider.GetRequiredService<ViewModelWorkRunner>(),
            provider.GetRequiredService<ITextCatalogue>(),
            provider.GetRequiredService<IGameIconProvider>(),
            provider.GetRequiredService<IUiDispatcher>(),
            async () =>
            {
                var shell = provider.GetRequiredService<MainWindowViewModel>();
                shell.IsFirstRun = false;
                await shell.InitializeAsync().ConfigureAwait(true);
            }));

        services.AddSingleton<StudioImportServices>();
        // Written out: the page needs the shell's reload callback, which the container cannot make.
        services.AddSingleton(provider => new StudioPageViewModel(
            provider.GetRequiredService<IStudioDraftStore>(),
            provider.GetRequiredService<IPackInstaller>(),
            provider.GetRequiredService<IGamePackLoader>(),
            provider.GetRequiredService<IPackTrial>(),
            provider.GetRequiredService<IHashIgnoreCost>(),
            provider.GetRequiredService<IPackExporter>(),
            provider.GetRequiredService<IPackPublisher>(),
            provider.GetRequiredService<IFolderLauncher>(),
            provider.GetRequiredService<CharacterManagerViewModel>(),
            provider.GetRequiredService<StudioImportServices>(),
            provider.GetRequiredService<IStoragePicker>(),
            provider.GetRequiredService<IClipboardImageReader>(),
            provider.GetRequiredService<INotificationService>(),
            provider.GetRequiredService<ViewModelWorkRunner>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IUiDispatcher>(),
            provider.GetRequiredService<ITextCatalogue>(),
            provider.GetRequiredService<IAppSettingsStore>(),
            provider.GetRequiredService<IStudioThumbnailCache>(),
            provider.GetRequiredService<IGameIconProvider>(),
            provider.GetRequiredService<PackInstallFollowUp>(),
            provider.GetRequiredService<ColumnWidthMemory>(),
            ct => provider.GetRequiredService<MainWindowViewModel>().ReloadGamesAsync(ct),
            provider.GetRequiredService<ILogger>()));
        services.AddSingleton<ShellPages>();
        services.AddSingleton<MainWindowViewModel>();

        return services;
    }
}
