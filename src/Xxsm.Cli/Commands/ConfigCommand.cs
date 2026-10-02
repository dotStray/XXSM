using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Xxsm.Cli.Output;
using Xxsm.Core;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Core.Settings;
using Xxsm.Core.Text;
using Xxsm.Packs.Registry;
using Xxsm.Packs.Sorting;

namespace Xxsm.Cli.Commands;

/// <summary><c>xxsm config</c>: everything the desktop settings page can change.</summary>
internal static class ConfigCommand
{
    /// <summary>Builds the command.</summary>
    public static Command Create()
    {
        var command = new Command("config", "Show and change XXSM's settings.")
        {
            CreateShow(),
            CreateSet(),
            CreateRegistry(),
            CreateReset(),
        };

        return command;
    }

    /// <summary><c>xxsm config reset</c>: the settings page's <em>Reset to factory settings</em>, at once.</summary>
    private static Command CreateReset()
    {
        var dryRun = new Option<bool>("--dry-run")
        {
            Description = "List what would go to the trash, and move nothing.",
        };

        var command = new Command(
            "reset",
            "Put XXSM back to how it was first installed: move its settings, installed packs, your added and " +
            "edited characters, drafts, downloads and caches to the trash. Mods folders and logs are left alone. " +
            "Close the app first.")
        {
            dryRun,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var reset = provider.GetRequiredService<IFactoryReset>();
            var plan = await reset.PlanAsync(cancellationToken).ConfigureAwait(false);
            var dry = parse.GetValue(dryRun);

            IReadOnlyList<ConfigResetItemReport> items;

            if (dry)
            {
                items = [.. plan.Items.Select(item => new ConfigResetItemReport(item, "wouldMove", null, null))];
            }
            else
            {
                var result = await reset.RunAsync(cancellationToken).ConfigureAwait(false);

                items =
                [
                    .. result.Moved.Select(moved => new ConfigResetItemReport(moved.OriginalPath, "moved", moved.TrashedPath, null)),
                    .. result.Failed.Select(failed => new ConfigResetItemReport(failed.Path, "failed", null, failed.Message)),
                ];
            }

            var report = new ConfigResetReport(dry, plan.Directories, plan.Kept, items);
            var failures = items.Count(item => item.Status == "failed");

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report, failures == 0 ? 0 : 1);
            }

            foreach (var item in items)
            {
                Console.Out.WriteLine(item.Status switch
                {
                    "wouldMove" => $"  would move  {PathDisplay.Show(item.Path)}",
                    "moved" => $"  moved       {PathDisplay.Show(item.Path)}  ->  {PathDisplay.Show(item.TrashedPath)}",
                    _ => $"  NOT MOVED   {PathDisplay.Show(item.Path)}: {item.Message}",
                });
            }

            foreach (var kept in plan.Kept)
            {
                Console.Out.WriteLine($"  kept        {kept}");
            }

            var moved = items.Count - failures;

            Console.Out.WriteLine(dry
                ? $"{EnglishCount.Plural(items.Count, "item", "items")} would go to the trash. Nothing was changed."
                : failures == 0
                    ? $"XXSM has been reset: {EnglishCount.Plural(moved, "item", "items")} moved to the trash."
                    : $"XXSM was partly reset: {EnglishCount.Plural(moved, "item", "items")} moved to the trash, " +
                      $"{EnglishCount.Plural(failures, "item", "items")} could not be moved.");

            return failures == 0 ? 0 : 1;
        });

        return command;
    }

    private static Command CreateShow()
    {
        var command = new Command("show", "Show every setting and where it is stored.");

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var settingsStore = provider.GetRequiredService<IAppSettingsStore>();
            var packPreferences = provider.GetRequiredService<IPackPreferencesStore>();
            var paths = provider.GetRequiredService<IAppPaths>();

            var settings = await settingsStore.ReadAsync(cancellationToken).ConfigureAwait(false);
            var packs = await packPreferences.ReadAsync(cancellationToken).ConfigureAwait(false);

            var sort = settings.Sort.ToSortSettings();

            var report = new ConfigReport(
                settingsStore.SettingsPath,
                packPreferences.PreferencesPath,
                CliOutput.Camel(settings.Theme.ToString()),
                settings.FirstRunCompleted,
                settings.LastGameId,
                settings.ShowPackStudio,
                !settings.UpdateCheckOff,
                settings.ColumnWidths,
                packs.Registries.Count > 0 ? packs.Registries : [AppInfo.DefaultRegistryUrl],
                new ConfigSortReport(
                    sort.AmbiguityThreshold,
                    sort.MinScore,
                    sort.MarginRatio,
                    sort.DefaultVariantConfidenceCeiling,
                    settings.Sort.IsDefault),
                new ConfigGameBananaReport(
                    settings.GameBanana.Enabled,
                    settings.GameBanana.FetchOnInstallOrDefault,
                    settings.GameBanana.AddFromUrlOrDefault,
                    settings.GameBanana.PasteStartsLookupOrDefault,
                    CliOutput.Camel(settings.GameBanana.PasteIntoExisting.ToString()),
                    settings.GameBanana.DownloadPicturesOrDefault,
                    settings.GameBanana.CheckForUpdates,
                    settings.GameBanana.CheckInterval.TotalHours,
                    settings.GameBanana.CacheLifetime.TotalHours,
                    settings.GameBanana.KeepDownloads.TotalDays,
                    settings.GameBanana.DownloadHistoryCount,
                    settings.GameBanana.RequestGap.TotalSeconds,
                    settings.GameBanana.ApiBase.AbsoluteUri),
                [
                    .. settings.Games
                        .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(entry => new ConfigGameReport(
                            entry.Key,
                            entry.Value.ModsDirectory,
                            entry.Value.ModsDirectory is { Length: > 0 } directory
                            && Directory.Exists(directory),
                            CliOutput.Camel(entry.Value.SkinDisplayMode.ToString()),
                            entry.Value.PinnedCharacters)),
                ],
                [
                    new ConfigDirectoryReport("config", paths.ConfigDirectory),
                    new ConfigDirectoryReport("data", paths.DataDirectory),
                    new ConfigDirectoryReport("cache", paths.CacheDirectory),
                    new ConfigDirectoryReport("packs", paths.PacksDirectory),
                    new ConfigDirectoryReport("overlays", paths.OverlaysDirectory),
                    new ConfigDirectoryReport("logs", paths.LogsDirectory),
                ]);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report);
            }

            WriteHuman(report);
            return 0;
        });

        return command;
    }

    /// <summary><c>xxsm config registry</c>: the registry list in <c>packs.json</c>.</summary>
    private static Command CreateRegistry()
    {
        var command = new Command(
            "registry", "Show and change where Game Packs are downloaded from.")
        {
            CreateRegistryList(),
            CreateRegistryChange("add", adding: true),
            CreateRegistryChange("remove", adding: false),
        };

        return command;
    }

    private static Command CreateRegistryList()
    {
        var command = new Command("list", "List the registries XXSM will consult, in order.");

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var store = provider.GetRequiredService<IPackPreferencesStore>();
            var preferences = await store.ReadAsync(cancellationToken).ConfigureAwait(false);

            if (preferences.Registries.Count == 0)
            {
                Console.Out.WriteLine(
                    $"No registries configured; using the built-in default:{Environment.NewLine}" +
                    $"  {AppInfo.DefaultRegistryUrl}");

                return 0;
            }

            foreach (var registry in preferences.Registries)
            {
                Console.Out.WriteLine(registry);
            }

            return 0;
        });

        return command;
    }

    private static Command CreateRegistryChange(string name, bool adding)
    {
        var registry = new Argument<string>("registry")
        {
            Description = "A URL, or a path to a folder or an index.json.",
        };

        var command = new Command(
            name,
            adding
                ? "Add a registry. Later entries win on ties, so yours can shadow the default."
                : "Remove a registry.")
        {
            registry,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var store = provider.GetRequiredService<IPackPreferencesStore>();
            var current = await store.ReadAsync(cancellationToken).ConfigureAwait(false);
            var value = parse.GetValue(registry)!;

            var registries = current.Registries.ToList();

            if (adding)
            {
                if (registries.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    Console.Out.WriteLine($"'{value}' is already configured.");
                    return 0;
                }

                registries.Add(value);
            }
            else if (registries.RemoveAll(
                         entry => string.Equals(entry, value, StringComparison.OrdinalIgnoreCase)) == 0)
            {
                throw new SettingsLoadException(
                    $"'{value}' is not one of the configured registries. " +
                    "Run 'xxsm config registry list' to see them.");
            }

            await store.WriteAsync(current with { Registries = registries }, cancellationToken)
                .ConfigureAwait(false);

            Console.Out.WriteLine($"Saved to {PathDisplay.Show(store.PreferencesPath)}");
            return 0;
        });

        return command;
    }

    private static Command CreateSet()
    {
        var game = new Option<string?>("--game")
        {
            Description = "The game to change. Required for --mods and --skin-display.",
        };

        var mods = new Option<string?>("--mods")
        {
            Description = "The game's Mods folder. Other commands fall back to it when --mods is omitted.",
        };

        var skinDisplay = new Option<SkinDisplayMode?>("--skin-display")
        {
            Description = "Whether skins share a tile with their base character or get their own.",
        };

        var theme = new Option<AppTheme?>("--theme")
        {
            Description = "Colour scheme for the desktop app.",
        };

        var showStudio = new Option<bool?>("--show-pack-studio")
        {
            Description = "Show Pack Studio in the desktop app's rail (true or false). The 'xxsm studio' commands work either way.",
        };

        var checkUpdates = new Option<bool?>("--check-for-updates")
        {
            Description = "Let the desktop app ask GitHub at start and daily whether a newer XXSM is out (true or false). " +
                          "It never downloads anything.",
        };

        var resetColumns = new Option<string?>("--reset-column-widths")
        {
            Description = "Forget the column widths set by dragging or double-clicking in the desktop app's tables, " +
                          "so each goes back to its own. Name a table (studio, mods) for that one only.",
            Arity = ArgumentArity.ZeroOrOne,
            HelpName = "table",
        };

        var ambiguity = new Option<int?>("--ambiguity-threshold")
        {
            Description = "How many characters a hash may span before auto-sort ignores it.",
        };

        var minScore = new Option<int?>("--min-score")
        {
            Description = "The score auto-sort requires before it will file a mod at all.",
        };

        var marginRatio = new Option<double?>("--margin-ratio")
        {
            Description = "How far the best match must beat the runner-up before it is trusted.",
        };

        var confidenceCeiling = new Option<double?>("--confidence-ceiling")
        {
            Description = "The confidence cap applied when auto-sort had to guess which outfit.",
        };

        var resetSort = new Option<bool>("--reset-sort")
        {
            Description = "Put every auto-sort constant back to its measured default.",
        };

        var gameBanana = new Option<bool?>("--gamebanana")
        {
            Description = "Let XXSM look mods up on GameBanana (true or false). Off by default; "
                          + "with it off, nothing here contacts the site at all.",
        };

        var gbFetchOnInstall = new Option<bool?>("--gamebanana-fetch-on-install")
        {
            Description = "Fill a mod installed from an address in from its page (true or false).",
        };

        var gbAddFromUrl = new Option<bool?>("--gamebanana-add-from-url")
        {
            Description = "Offer 'From GameBanana…' in the desktop app's Add mod menu (true or false).",
        };

        var gbPaste = new Option<bool?>("--gamebanana-paste")
        {
            Description = "Let a paste on a character page start a look-up (true or false).",
        };

        var gbPasteInto = new Option<GameBananaPasteAction?>("--gamebanana-paste-into-existing")
        {
            Description = "What a look-up on a mod you already have does: ask, fillBlanks or saveOnly.",
        };

        var gbPictures = new Option<bool?>("--gamebanana-pictures")
        {
            Description = "Download a mod's preview picture along with its words (true or false).",
        };

        var gbCheckUpdates = new Option<bool?>("--gamebanana-check-updates")
        {
            Description = "Check installed mods against their pages in the background (true or false).",
        };

        var gbInterval = new Option<double?>("--gamebanana-check-interval")
        {
            Description = "How many hours between update checks. 24 by default.",
        };

        var gbCacheHours = new Option<double?>("--gamebanana-cache-hours")
        {
            Description = "How many hours a fetched page stays usable from disk. 6 by default, 1 at least.",
        };

        var gbKeepDownloads = new Option<double?>("--gamebanana-keep-downloads-days")
        {
            Description =
                "How many days a finished download stays on the list before its archive is "
                + "removed. 7 by default.",
        };

        var gbHistory = new Option<int?>("--gamebanana-download-history")
        {
            Description = "How many downloads the list keeps at most, newest first. 50 by default.",
        };

        var gbRate = new Option<double?>("--gamebanana-seconds-between-requests")
        {
            Description = "How many seconds XXSM waits between two requests. 1 by default, and 1 at least: longer is allowed.",
        };

        var gbApi = new Option<string?>("--gamebanana-api")
        {
            Description = "The API address, so a future API version is a settings change, not a new build.",
        };

        var pin = new Option<string?>("--pin")
        {
            Description = "Pin a character to the top of the grid, by internal name. Requires --game.",
        };

        var unpin = new Option<string?>("--unpin")
        {
            Description = "Unpin a character, by internal name. Requires --game.",
        };

        var command = new Command("set", "Change a setting.")
        {
            game,
            mods,
            skinDisplay,
            theme,
            showStudio,
            checkUpdates,
            resetColumns,
            ambiguity,
            minScore,
            marginRatio,
            confidenceCeiling,
            resetSort,
            gameBanana,
            gbFetchOnInstall,
            gbAddFromUrl,
            gbPaste,
            gbPasteInto,
            gbPictures,
            gbCheckUpdates,
            gbInterval,
            gbCacheHours,
            gbKeepDownloads,
            gbHistory,
            gbRate,
            gbApi,
            pin,
            unpin,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var store = provider.GetRequiredService<IAppSettingsStore>();

            var gameId = parse.GetValue(game);
            var modsDirectory = parse.GetValue(mods);
            var skin = parse.GetValue(skinDisplay);
            var chosenTheme = parse.GetValue(theme);
            var chosenShowStudio = parse.GetValue(showStudio);
            var chosenCheckUpdates = parse.GetValue(checkUpdates);
            // Given without a table, the option resets them all; its value is then null.
            var resetWidths = parse.GetResult(resetColumns) is not null;
            var resetTable = parse.GetValue(resetColumns) is { } named && !string.IsNullOrWhiteSpace(named) ? named.Trim() : null;
            var reset = parse.GetValue(resetSort);
            var pinName = parse.GetValue(pin);
            var unpinName = parse.GetValue(unpin);

            // Only what was given: the rest keeps what the file says.
            GameBananaSettings ApplyGameBanana(GameBananaSettings current) => current with
            {
                Enabled = parse.GetValue(gameBanana) ?? current.Enabled,
                FetchOnInstall = parse.GetValue(gbFetchOnInstall) ?? current.FetchOnInstall,
                AddFromUrl = parse.GetValue(gbAddFromUrl) ?? current.AddFromUrl,
                PasteStartsLookup = parse.GetValue(gbPaste) ?? current.PasteStartsLookup,
                PasteIntoExisting = parse.GetValue(gbPasteInto) ?? current.PasteIntoExisting,
                DownloadPictures = parse.GetValue(gbPictures) ?? current.DownloadPictures,
                CheckForUpdates = parse.GetValue(gbCheckUpdates) ?? current.CheckForUpdates,
                CheckIntervalHours = parse.GetValue(gbInterval) ?? current.CheckIntervalHours,
                CacheHours = parse.GetValue(gbCacheHours) ?? current.CacheHours,
                KeepDownloadsDays = parse.GetValue(gbKeepDownloads) ?? current.KeepDownloadsDays,
                DownloadHistory = parse.GetValue(gbHistory) ?? current.DownloadHistory,
                SecondsBetweenRequests = parse.GetValue(gbRate) ?? current.SecondsBetweenRequests,
                ApiBaseUrl = parse.GetValue(gbApi) ?? current.ApiBaseUrl,
            };

            var changedGameBanana =
                parse.GetValue(gameBanana) is not null
                || parse.GetValue(gbFetchOnInstall) is not null
                || parse.GetValue(gbAddFromUrl) is not null
                || parse.GetValue(gbPaste) is not null
                || parse.GetValue(gbPasteInto) is not null
                || parse.GetValue(gbPictures) is not null
                || parse.GetValue(gbCheckUpdates) is not null
                || parse.GetValue(gbInterval) is not null
                || parse.GetValue(gbCacheHours) is not null
                || parse.GetValue(gbKeepDownloads) is not null
                || parse.GetValue(gbHistory) is not null
                || parse.GetValue(gbRate) is not null
                || parse.GetValue(gbApi) is not null;

            var thresholds = new SortThresholdSettings
            {
                AmbiguityThreshold = parse.GetValue(ambiguity),
                MinScore = parse.GetValue(minScore),
                MarginRatio = parse.GetValue(marginRatio),
                DefaultVariantConfidenceCeiling = parse.GetValue(confidenceCeiling),
            };

            var changedAnything = modsDirectory is not null || skin is not null
                                                            || chosenTheme is not null || reset
                                                            || chosenShowStudio is not null || resetWidths
                                                            || chosenCheckUpdates is not null
                                                            || !thresholds.IsDefault
                                                            || changedGameBanana
                                                            || pinName is not null || unpinName is not null;

            if (!changedAnything)
            {
                throw new SettingsLoadException(
                    "Nothing to change. Give at least one option — run 'xxsm config set --help' " +
                    "to see them, or 'xxsm config show' to see what is set now.");
            }

            if ((modsDirectory is not null || skin is not null || pinName is not null || unpinName is not null)
                && string.IsNullOrWhiteSpace(gameId))
            {
                throw new SettingsLoadException(
                    "--mods, --skin-display, --pin and --unpin are per-game settings, so --game is " +
                    "required with them.");
            }

            if (modsDirectory is { Length: > 0 })
            {
                var probe = await provider
                    .GetRequiredService<IModsFolderProbe>()
                    .ProbeAsync(modsDirectory, cancellationToken)
                    .ConfigureAwait(false);

                if (!probe.IsUsable)
                {
                    throw new SettingsLoadException(
                        $"'{PathDisplay.Show(probe.Path)}' will not work as a Mods folder. {probe.Summary} " +
                        "Nothing has been saved.");
                }

                CliOutput.WriteError(probe.Summary);
            }

            if (chosenTheme is not null || reset || !thresholds.IsDefault || chosenShowStudio is not null
                || resetWidths || changedGameBanana || chosenCheckUpdates is not null)
            {
                await store.UpdateAsync(
                    current =>
                    {
                        var changed = current with
                        {
                            Theme = chosenTheme ?? current.Theme,
                            ShowPackStudio = chosenShowStudio ?? current.ShowPackStudio,
                            UpdateCheckOff = chosenCheckUpdates is { } check ? !check : current.UpdateCheckOff,
                            Sort = reset ? SortThresholdSettings.Default : Combine(current.Sort, thresholds),
                            GameBanana = changedGameBanana ? ApplyGameBanana(current.GameBanana) : current.GameBanana,
                        };

                        return resetWidths ? changed.WithoutColumnWidths(resetTable) : changed;
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            if (gameId is { Length: > 0 }
                && (modsDirectory is not null || skin is not null || pinName is not null || unpinName is not null))
            {
                await store.UpdateGameAsync(
                    gameId,
                    current => current with
                    {
                        ModsDirectory = modsDirectory ?? current.ModsDirectory,
                        SkinDisplayMode = skin ?? current.SkinDisplayMode,
                        PinnedCharacters = ApplyPin(current.PinnedCharacters, pinName, unpinName),
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            Console.Out.WriteLine($"Saved to {PathDisplay.Show(store.SettingsPath)}");
            return 0;
        });

        return command;
    }

    /// <summary>Adds or removes one name from a game's pinned-characters list.</summary>
    private static IReadOnlyList<string> ApplyPin(
        IReadOnlyList<string> current, string? pin, string? unpin)
    {
        if (pin is null && unpin is null)
        {
            return current;
        }

        var pinned = new List<string>(current);

        if (unpin is { Length: > 0 })
        {
            pinned.RemoveAll(name => string.Equals(name, unpin, StringComparison.OrdinalIgnoreCase));
        }

        if (pin is { Length: > 0 } && !pinned.Contains(pin, StringComparer.OrdinalIgnoreCase))
        {
            pinned.Add(pin);
        }

        return pinned;
    }

    /// <summary>Applies only the thresholds the user actually gave, leaving the rest as they were.</summary>
    private static SortThresholdSettings Combine(
        SortThresholdSettings current, SortThresholdSettings given) =>
        new()
        {
            AmbiguityThreshold = given.AmbiguityThreshold ?? current.AmbiguityThreshold,
            MinScore = given.MinScore ?? current.MinScore,
            MarginRatio = given.MarginRatio ?? current.MarginRatio,
            DefaultVariantConfidenceCeiling =
                given.DefaultVariantConfidenceCeiling ?? current.DefaultVariantConfidenceCeiling,
        };

    private static string Yes(bool value) => value ? "yes" : "no";

    private static void WriteHuman(ConfigReport report)
    {
        var rows = new List<(string, string)>
        {
            ("Settings file", PathDisplay.Show(report.SettingsPath)),
            ("Packs file", PathDisplay.Show(report.PackPreferencesPath)),
            ("Theme", report.Theme),
            ("First run", report.FirstRunCompleted ? "completed" : "not completed"),
            ("Pack Studio", report.ShowPackStudio ? "shown" : "hidden (xxsm config set --show-pack-studio true)"),
            ("New versions", report.CheckForAppUpdates
                ? "asked about at start and daily (xxsm version --check asks now)"
                : "not asked about (xxsm config set --check-for-updates true)"),
            ("Column widths", report.ColumnWidths.Count == 0
                ? "as designed"
                : $"set for {string.Join(", ", report.ColumnWidths.Keys)} (xxsm config set --reset-column-widths)"),
        };

        if (report.LastGameId is { Length: > 0 } last)
        {
            rows.Add(("Last game", last));
        }

        CliOutput.WriteRows(rows);

        Console.Out.WriteLine();
        Console.Out.WriteLine("Registries");

        foreach (var registry in report.Registries)
        {
            Console.Out.WriteLine($"  {registry}");
        }

        Console.Out.WriteLine();
        Console.Out.WriteLine(report.Sort.IsDefault
            ? "Auto-sort (measured defaults, nothing overridden)"
            : "Auto-sort (overridden)");

        CliOutput.WriteRows(
        [
            ("  ambiguity threshold", report.Sort.AmbiguityThreshold.ToString(CultureInfo.InvariantCulture)),
            ("  minimum score", report.Sort.MinScore.ToString(CultureInfo.InvariantCulture)),
            ("  margin ratio", report.Sort.MarginRatio.ToString(CultureInfo.InvariantCulture)),
            ("  confidence ceiling",
                report.Sort.DefaultVariantConfidenceCeiling.ToString(CultureInfo.InvariantCulture)),
        ]);

        Console.Out.WriteLine();
        Console.Out.WriteLine(report.GameBanana.Enabled
            ? "GameBanana (switched on)"
            : "GameBanana (switched off — nothing here contacts the site; "
              + "xxsm config set --gamebanana true)");

        if (report.GameBanana.Enabled)
        {
            CliOutput.WriteRows(
            [
                ("  fill in on install", Yes(report.GameBanana.FetchOnInstall)),
                ("  offer From GameBanana", Yes(report.GameBanana.AddFromUrl)),
                ("  a paste starts a look-up", Yes(report.GameBanana.PasteStartsLookup)),
                ("  a look-up on an existing mod", report.GameBanana.PasteIntoExisting),
                ("  download pictures", Yes(report.GameBanana.DownloadPictures)),
                ("  check for updates", report.GameBanana.CheckForUpdates
                    ? $"yes, every {report.GameBanana.CheckIntervalHours.ToString(CultureInfo.InvariantCulture)} h"
                    : "no"),
                ("  cache a page for", $"{report.GameBanana.CacheHours.ToString(CultureInfo.InvariantCulture)} h"),
                ("  keep a download for",
                    $"{report.GameBanana.KeepDownloadsDays.ToString("0.#", CultureInfo.InvariantCulture)} days, "
                    + $"{report.GameBanana.DownloadHistory.ToString(CultureInfo.InvariantCulture)} at most"),
                ("  seconds between requests", report.GameBanana.SecondsBetweenRequests.ToString("0.##", CultureInfo.InvariantCulture)),
                ("  api", report.GameBanana.ApiBaseUrl),
            ]);
        }

        Console.Out.WriteLine();

        if (report.Games.Count == 0)
        {
            Console.Out.WriteLine("No game has a Mods folder saved yet.");
        }
        else
        {
            Console.Out.WriteLine("Games");

            foreach (var entry in report.Games)
            {
                var state = entry.ModsDirectory is null
                    ? "no Mods folder saved"
                    : entry.ModsDirectoryExists
                        ? entry.ModsDirectory
                        : $"{PathDisplay.Show(entry.ModsDirectory)}  (not there right now)";

                Console.Out.WriteLine($"  {entry.GameId}  {state}");
                Console.Out.WriteLine($"    skins: {entry.SkinDisplayMode}");

                if (entry.PinnedCharacters.Count > 0)
                {
                    Console.Out.WriteLine($"    pinned: {string.Join(", ", entry.PinnedCharacters)}");
                }
            }
        }

        Console.Out.WriteLine();
        Console.Out.WriteLine("Directories");
        CliOutput.WriteRows(
            [.. report.Directories.Select(directory => ($"  {directory.Purpose}", directory.Path))]);
    }
}
