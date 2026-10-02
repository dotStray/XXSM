using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Xxsm.Cli.Output;
using Xxsm.Core.GameBanana;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Core.Text;
using Xxsm.Packs.GameBanana;

namespace Xxsm.Cli.Commands;

/// <summary><c>xxsm mod fetch</c>, <c>link</c>, <c>updates</c> and <c>update</c>: GameBanana, headless.</summary>
/// <remarks>Each refuses while GameBanana is switched off, and says which setting turns it on.</remarks>
internal static class ModGameBananaCommands
{
    /// <summary>Builds the commands.</summary>
    public static IReadOnlyList<Command> Create() => [CreateFetch(), CreateLink(), CreateUpdates(), CreateUpdate()];

    /// <summary><c>xxsm mod update</c>: replace a mod with its page's newer version, dry by default.</summary>
    private static Command CreateUpdate()
    {
        var folder = new Argument<string>("folder")
        {
            Description = "The mod's own folder. It must be linked to a GameBanana page.",
        };

        var mods = ModsFolderOptions.Mods();
        var game = ModsFolderOptions.Game();

        var fileId = new Option<long?>("--file")
        {
            Description =
                "Which of the page's files, by its id. Without it, the one the mod came from, or the "
                + "page's only file; a page with several files and no record of which one is listed instead.",
        };

        var apply = new Option<bool>("--apply")
        {
            Description = "Actually replace the mod. Without it nothing is changed.",
        };

        var command = new Command(
            "update",
            "Replace a mod with the newer version on its GameBanana page. Shows every file that "
            + "changes first; the previous version goes to the trash.")
        {
            folder, mods, game, fileId, apply,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var modsDirectory = await ModsFolderOptions
                .ResolveAsync(provider, parse, mods, game, cancellationToken)
                .ConfigureAwait(false);

            var updater = provider.GetRequiredService<IModUpdater>();

            using var plan = await updater
                .PlanAsync(parse.GetValue(folder)!, parse.GetValue(game), parse.GetValue(fileId), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var result = parse.GetValue(apply)
                ? await updater.ApplyAsync(plan, modsDirectory, cancellationToken).ConfigureAwait(false)
                : null;

            var report = new ModUpdatePlanReport(
                plan.ModFolder,
                plan.InstalledVersion,
                plan.NewVersion,
                plan.File.IdRow,
                plan.File.File,
                [
                    .. plan.Changes.Select(change => new ModUpdateChangeReport(
                        change.RelativePath, CliOutput.Camel(change.Kind.ToString()), change.MayBeEdited)),
                ],
                plan.UnchangedCount,
                result is not null,
                result?.Previous.TrashedPath);

            if (parse.GetValue(GlobalOptions.Json))
            {
                Console.Out.WriteLine(CliJson.Serialize(report));

                return 0;
            }

            WriteUpdatePlan(report);

            return 0;
        });

        return command;
    }

    private static void WriteUpdatePlan(ModUpdatePlanReport report)
    {
        Console.Out.WriteLine(
            $"{PathDisplay.Show(report.ModFolder)}: {report.InstalledVersion ?? "(no version)"} -> {report.NewVersion ?? "(no version)"}"
            + $"  from {report.FileName} ({report.FileId?.ToString(CultureInfo.InvariantCulture)})");

        foreach (var change in report.Changes)
        {
            Console.Out.WriteLine(
                $"  {change.Kind,-9} {PathDisplay.Show(change.Path)}{(change.MayBeEdited ? "   (changed since it was added — edited?)" : string.Empty)}");
        }

        Console.Out.WriteLine($"  {EnglishCount.Plural(report.Unchanged, "file", "files")} unchanged.");

        Console.Out.WriteLine(report.Applied
            ? $"Updated. The previous version is in the trash: {report.TrashedTo}"
            : "Nothing was changed. Add --apply to update it.");
    }

    /// <summary><c>xxsm mod link</c>: give a mod its address; no request is made.</summary>
    private static Command CreateLink()
    {
        var folder = new Argument<string>("folder")
        {
            Description = "The mod's own folder.",
        };

        var address = new Argument<string?>("address")
        {
            Description =
                "The mod's address. A GameBanana mod page links it; any other address is kept but "
                + "leaves the mod unlinked.",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var remove = new Option<bool>("--remove")
        {
            Description = "Clear the mod's address, and with it the link.",
        };

        var command = new Command(
            "link",
            "Give a mod its address, which links it to its GameBanana page so its updates can be "
            + "checked. Nothing is fetched and nothing else is written.")
        {
            folder, address, remove,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var modFolder = parse.GetValue(folder)!;
            var removing = parse.GetValue(remove);
            var given = removing ? null : parse.GetValue(address);

            if (!removing && given is not { Length: > 0 })
            {
                CliOutput.WriteError("Give the mod's address, or --remove to clear it.");

                return 1;
            }

            var written = await provider
                .GetRequiredService<IModConfigStore>()
                .UpdateAsync(modFolder, config => config.WithModUrl(given), cancellationToken)
                .ConfigureAwait(false);

            var report = new ModLinkReport(modFolder, written.ModUrl, written.GameBanana?.ModId);

            if (parse.GetValue(GlobalOptions.Json))
            {
                Console.Out.WriteLine(CliJson.Serialize(report));

                return 0;
            }

            Console.Out.WriteLine(report.ModId is { } linked
                ? $"'{PathDisplay.Show(modFolder)}' is linked to GameBanana mod {linked.ToString(CultureInfo.InvariantCulture)}. "
                  + "Its updates are checked from now on; nothing else was changed."
                : report.ModUrl is { Length: > 0 }
                    ? $"'{PathDisplay.Show(modFolder)}' has its address, but it is not a GameBanana mod page, so it is not linked."
                    : $"'{PathDisplay.Show(modFolder)}' has no address now, and is not linked to GameBanana.");

            return 0;
        });

        return command;
    }

    /// <summary><c>xxsm mod fetch</c>: fill an installed mod in from its page; dry until <c>--apply</c>.</summary>
    private static Command CreateFetch()
    {
        var folder = new Argument<string>("folder")
        {
            Description = "The mod's own folder.",
        };

        var url = new Option<string?>("--url")
        {
            Description =
                "The mod's GameBanana address. Needed the first time; after that the address "
                + "recorded in .xxsm/mod.json is used.",
        };

        var fields = new Option<string[]>("--field")
        {
            Description =
                "Take only these fields: name, author, version, description, picture. "
                + "May be given more than once. Implies --all for the ones named.",
            AllowMultipleArgumentsPerToken = true,
        };

        var all = new Option<bool>("--all")
        {
            Description = "Take every field the page has something for, replacing what is there.",
        };

        var refresh = new Option<bool>("--refresh")
        {
            Description = "Ask the site again instead of using the cached page.",
        };

        var apply = new Option<bool>("--apply")
        {
            Description = "Actually write the metadata. Without it nothing is written.",
        };

        var dryRun = new Option<bool>("--dry-run")
        {
            Description = "Show what would be written and write nothing. The default.",
        };

        var command = new Command(
            "fetch", "Fill a mod in from its GameBanana page: name, author, version, description, picture.")
        {
            folder, url, fields, all, refresh, apply, dryRun,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var modFolder = parse.GetValue(folder)!;
            var configs = provider.GetRequiredService<IModConfigStore>();

            var modId = await ResolveModIdAsync(configs, modFolder, parse.GetValue(url), cancellationToken)
                .ConfigureAwait(false);

            var page = await provider
                .GetRequiredService<IGameBananaClient>()
                .GetModAsync(modId, parse.GetValue(refresh), cancellationToken)
                .ConfigureAwait(false);

            var enrichment = provider.GetRequiredService<IModEnrichment>();
            var plan = await enrichment.PlanAsync(modFolder, page, cancellationToken).ConfigureAwait(false);

            var named = Fields(parse.GetValue(fields) ?? []);

            var take = named.Count > 0
                ? named
                : parse.GetValue(all)
                    ? plan.AllFields
                    : plan.BlankFields;

            var shouldApply = parse.GetValue(apply) && !parse.GetValue(dryRun);
            ModEnrichmentResult? result = null;

            if (shouldApply)
            {
                result = await enrichment.ApplyAsync(plan, take, cancellationToken).ConfigureAwait(false);
            }

            var report = new ModFetchReport(
                plan.ModFolder,
                page.ModId,
                page.PageUrl.AbsoluteUri,
                page.Name,
                page.Author,
                page.Version,
                page.Summary,
                shouldApply,
                [
                    .. plan.Entries.Select(entry => new ModFetchFieldReport(
                        CliOutput.Camel(entry.Field.ToString()),
                        entry.Current,
                        entry.Proposed,
                        take.Contains(entry.Field) && entry.Changes,
                        entry.Changes)),
                ],
                [.. (result?.Applied ?? []).Select(written => CliOutput.Camel(written.ToString()))],
                result?.PictureError);

            if (parse.GetValue(GlobalOptions.Json))
            {
                Console.Out.WriteLine(CliJson.Serialize(report));

                return 0;
            }

            WriteFetch(report);

            return 0;
        });

        return command;
    }

    /// <summary><c>xxsm mod updates</c>: what has a newer version; no network without <c>--check</c>.</summary>
    private static Command CreateUpdates()
    {
        var mods = ModsFolderOptions.Mods();
        var game = ModsFolderOptions.Game();

        var check = new Option<bool>("--check")
        {
            Description = "Ask GameBanana about every mod that is due. Without it, only what is already known is shown.",
        };

        var force = new Option<bool>("--force")
        {
            Description = "With --check, ask about every linked mod however recently it was asked about.",
        };

        var accept = new Option<string?>("--accept")
        {
            Description =
                "Take one mod off the list by agreeing that what is installed is current. "
                + "Takes the mod's folder.",
        };

        var pretend = new Option<string?>("--pretend")
        {
            Description =
                "Mark one mod as having an update without asking GameBanana, to see what the mark "
                + "looks like. Takes the mod's folder; the mod must be linked to a page. Needs "
                + "\"developerTools\": true in the gameBanana block of settings.json.",
        };

        var pretendVersion = new Option<string?>("--pretend-version")
        {
            Description = "With --pretend, the version to show as the newer one. Default: test.",
        };

        var command = new Command(
            "updates", "Show which mods have a newer version on GameBanana. Never downloads anything.")
        {
            mods, game, check, force, accept, pretend, pretendVersion,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var checker = provider.GetRequiredService<IModUpdateChecker>();

            if (parse.GetValue(accept) is { Length: > 0 } accepted)
            {
                var cleared = await checker.AcceptAsync(accepted, cancellationToken).ConfigureAwait(false);

                if (parse.GetValue(GlobalOptions.Json))
                {
                    Console.Out.WriteLine(CliJson.Serialize(new ModUpdateAcceptReport(accepted, cleared)));

                    return 0;
                }

                Console.Out.WriteLine(cleared
                    ? $"'{accepted}' is no longer listed as having an update."
                    : $"'{accepted}' was not listed as having an update.");

                return 0;
            }

            if (parse.GetValue(pretend) is { Length: > 0 } pretended)
            {
                var marked = await checker
                    .PretendAsync(pretended, parse.GetValue(pretendVersion) ?? "test", cancellationToken)
                    .ConfigureAwait(false);

                if (parse.GetValue(GlobalOptions.Json))
                {
                    Console.Out.WriteLine(CliJson.Serialize(new ModUpdatePretendReport(pretended, marked)));

                    return marked ? 0 : 1;
                }

                if (!marked)
                {
                    CliOutput.WriteError(
                        $"'{pretended}' is not linked to a GameBanana page, so there is no update to pretend. "
                        + "Link it first with 'xxsm mod link <folder> <address>'.");

                    return 1;
                }

                Console.Out.WriteLine(
                    $"'{pretended}' is now marked as having an update. Nothing was asked of GameBanana. "
                    + "--accept clears it.");

                return 0;
            }

            var modsDirectory = await ModsFolderOptions
                .ResolveAsync(provider, parse, mods, game, cancellationToken)
                .ConfigureAwait(false);

            ModUpdatesReport report;

            if (parse.GetValue(check))
            {
                var run = await checker
                    .CheckAsync(modsDirectory, parse.GetValue(force), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                report = new ModUpdatesReport(
                    modsDirectory,
                    true,
                    run.Linked,
                    run.NotDue,
                    [.. run.Updates.Select(Describe)],
                    [.. run.Checked.Select(Describe)]);
            }
            else
            {
                var known = await checker.ReadAsync(modsDirectory, cancellationToken).ConfigureAwait(false);

                report = new ModUpdatesReport(
                    modsDirectory,
                    false,
                    known.Count,
                    0,
                    [.. known.Where(status => status.HasUpdate).Select(Describe)],
                    [.. known.Select(Describe)]);
            }

            if (parse.GetValue(GlobalOptions.Json))
            {
                Console.Out.WriteLine(CliJson.Serialize(report));

                return 0;
            }

            WriteUpdates(report);

            return 0;
        });

        return command;
    }

    private static async Task<long> ResolveModIdAsync(
        IModConfigStore configs, string modFolder, string? url, CancellationToken cancellationToken)
    {
        if (url is { Length: > 0 })
        {
            if (GameBananaUrl.TryParseModId(url, out var given))
            {
                return given;
            }

            throw new GameBananaException(GameBananaUrl.IsFileAddress(url)
                ? $"'{url}' names a single file, not a mod page. Paste the mod's own address instead."
                : $"'{url}' is not a GameBanana mod address. It should look like "
                  + "https://gamebanana.com/mods/541886.");
        }

        var config = await configs.ReadAsync(modFolder, cancellationToken).ConfigureAwait(false);

        if (config?.GameBanana?.ModId is > 0 and { } recorded)
        {
            return recorded;
        }

        if (GameBananaUrl.FindModId(config?.ModUrl) is { } fromUrl)
        {
            return fromUrl;
        }

        throw new GameBananaException(
            $"Nothing records where '{PathDisplay.Show(modFolder)}' came from, so there is no page to read. "
            + "Give --url with the mod's GameBanana address.");
    }

    private static List<ModEnrichmentField> Fields(string[] names)
    {
        var chosen = new List<ModEnrichmentField>();

        foreach (var name in names)
        {
            if (Enum.TryParse<ModEnrichmentField>(name, ignoreCase: true, out var parsed))
            {
                chosen.Add(parsed);

                continue;
            }

            throw new GameBananaException(
                $"'{name}' is not a field this can take. Use name, author, version, description or picture.");
        }

        return chosen;
    }

    private static ModUpdateStatusReport Describe(ModUpdateStatus status) => new(
        status.ModFolder,
        status.DisplayName,
        status.ModId,
        status.PageUrl.AbsoluteUri,
        status.InstalledVersion,
        status.LatestVersion,
        status.HasUpdate,
        status.Error);

    private static void WriteFetch(ModFetchReport report)
    {
        CliOutput.WriteRows(
        [
            ("Mod", PathDisplay.Show(report.ModFolder)),
            ("Page", report.PageUrl),
            ("Title", report.Name ?? "(unnamed)"),
            ("Author", report.Author ?? "(unknown)"),
            ("Version", report.Version ?? "(none)"),
        ]);

        if (report.Summary is { Length: > 0 } summary)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine(summary);
        }

        Console.Out.WriteLine();

        if (!report.Fields.Any(entry => entry.Changes))
        {
            Console.Out.WriteLine("The page says nothing this mod does not already have.");

            return;
        }

        foreach (var entry in report.Fields.Where(field => field.Changes))
        {
            Console.Out.WriteLine(
                $"  [{(entry.Selected ? "x" : " ")}] {entry.Field}  "
                + $"{Short(entry.Current) ?? "(empty)"}  ->  {Short(entry.Proposed)}");
        }

        Console.Out.WriteLine();

        if (report.Applied)
        {
            Console.Out.WriteLine(report.Written.Count == 0
                ? "Linked to the page. No field was taken."
                : $"Wrote {string.Join(", ", report.Written)}.");
        }
        else
        {
            Console.Out.WriteLine("Nothing written. Add --apply to take the ticked fields, or --all for every one.");
        }

        if (report.PictureError is { Length: > 0 } picture)
        {
            CliOutput.WriteError($"The picture was not taken: {picture}");
        }
    }

    private static void WriteUpdates(ModUpdatesReport report)
    {
        CliOutput.WriteRows(
        [
            ("Mods", PathDisplay.Show(report.ModsDirectory)),
            ("Linked to GameBanana", report.Linked.ToString(CultureInfo.InvariantCulture)),
        ]);

        Console.Out.WriteLine();

        if (report.Linked == 0)
        {
            Console.Out.WriteLine(
                "No mod here records a GameBanana page. 'xxsm mod fetch <folder> --url …' links one.");

            return;
        }

        if (report.Updates.Count == 0)
        {
            Console.Out.WriteLine(report.Checked
                ? "Everything is current."
                : "Nothing is marked as having an update. Add --check to ask GameBanana.");
        }
        else
        {
            Console.Out.WriteLine(
                $"{EnglishCount.Plural(report.Updates.Count, "mod", "mods")} with a newer version:");
            Console.Out.WriteLine();

            foreach (var status in report.Updates)
            {
                var change = (status.InstalledVersion, status.LatestVersion) switch
                {
                    ({ Length: > 0 } from, { Length: > 0 } to) => $"  {from} -> {to}",
                    ({ Length: > 0 } from, _) => $"  {from} -> ?",
                    _ => string.Empty,
                };

                Console.Out.WriteLine($"  {status.Name}{change}");
                Console.Out.WriteLine($"    {status.PageUrl}");
                Console.Out.WriteLine($"    {PathDisplay.Show(status.ModFolder)}");
            }

            Console.Out.WriteLine();
            Console.Out.WriteLine(
                "Nothing was downloaded. Fetch the archive yourself and install it over the old one, "
                + "or 'xxsm mod updates --accept <folder>' to stop being told.");
        }

        if (report.NotDue > 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine(
                $"{report.NotDue.ToString(CultureInfo.InvariantCulture)} not due to be asked about yet "
                + "(--force asks anyway).");
        }

        var failures = report.Statuses.Where(status => status.Error is { Length: > 0 }).ToList();

        if (failures.Count > 0)
        {
            Console.Out.WriteLine();
            CliOutput.WriteError($"{EnglishCount.Plural(failures.Count, "mod", "mods")} could not be asked about:");

            foreach (var failure in failures)
            {
                CliOutput.WriteError($"  {failure.Name}: {failure.Error}");
            }
        }
    }

    private static string? Short(string? value)
    {
        if (value is not { Length: > 0 })
        {
            return null;
        }

        var oneLine = value.ReplaceLineEndings(" ").Trim();

        return oneLine.Length <= 60 ? oneLine : oneLine[..59] + "…";
    }
}
