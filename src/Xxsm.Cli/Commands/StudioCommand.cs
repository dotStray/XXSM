using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Xxsm.Cli.Output;
using Xxsm.Core.Diagnostics;
using Xxsm.Core.Io;
using Xxsm.Core.Text;
using Xxsm.Packs.Installation;
using Xxsm.Packs.Loading;
using Xxsm.Packs.Overlays;
using Xxsm.Packs.Registry;
using Xxsm.Packs.Studio;

namespace Xxsm.Cli.Commands;

/// <summary><c>xxsm studio</c>: Pack Studio, headless, through the same services.</summary>
internal static class StudioCommand
{
    /// <summary>Builds the command.</summary>
    public static Command Create() =>
        new("studio", "Make a Game Pack: start a game, bring its characters in, check it, try it and export it.")
        {
            CreateList(),
            CreateNew(),
            CreateOpen(),
            CreateShow(),
            CreateDelete(),
            CreateRestore(),
            CreateCheck(),
            StudioImportCommands.Create(),
            StudioEditCommands.CreateCharacter(),
            StudioEditCommands.CreateAttribute(),
            StudioHashCommands.Create(),
            StudioEditCommands.CreateGame(),
            CreateTry(),
            CreateExport(),
            CreateInstall(),
            CreatePromote(),
            CreateCaughtUp(),
        };

    internal static Argument<string> GameArgument() => new("game") { Description = "The draft's game id." };

    internal static Task<PackDraft> LoadAsync(ServiceProvider provider, string gameId, CancellationToken cancellationToken) =>
        provider.GetRequiredService<IStudioDraftStore>().ReadAsync(gameId, cancellationToken);

    internal static Task<StudioSaveResult> SaveAsync(ServiceProvider provider, PackDraft draft, CancellationToken cancellationToken) =>
        provider.GetRequiredService<IStudioDraftStore>().WriteAsync(draft, cancellationToken);

    internal static DateTimeOffset Now(ServiceProvider provider) => provider.GetRequiredService<TimeProvider>().GetUtcNow();

    internal static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A draft's pictures by pack-relative path, for the validator's picture checks.</summary>
    internal static Func<string, long?> ImageSizes(ServiceProvider provider, string gameId)
    {
        var store = provider.GetRequiredService<IStudioDraftStore>();
        return relative => store.GetImageSize(gameId, relative);
    }

    // Drafts

    private static Command CreateList()
    {
        var command = new Command("list", "List Pack Studio drafts.");

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var drafts = await provider.GetRequiredService<IStudioDraftStore>().ListAsync(cancellationToken).ConfigureAwait(false);
            var rows = drafts
                .Select(d => new StudioDraftReport(d.GameId, d.DisplayName, d.PackVersion, d.Directory, d.VariantCount, d.LastSavedAt, d.Error))
                .ToList();

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(rows);
            }

            if (rows.Count == 0)
            {
                Console.Out.WriteLine("No Pack Studio drafts yet.");
                Console.Out.WriteLine("Start one with: xxsm studio new \"Game name\"");
                return 0;
            }

            foreach (var row in rows)
            {
                Console.Out.WriteLine(row.Error is null
                    ? $"{row.DisplayName}  ({row.GameId})  {row.PackVersion}  {EnglishCount.Plural(row.Variants, "variant", "variants")}"
                    : $"{row.GameId}  cannot be read: {row.Error}");
            }

            return 0;
        });

        return command;
    }

    private static Command CreateNew()
    {
        var name = new Argument<string>("name") { Description = "The game's name. The only thing that is required." };
        var id = new Option<string?>("--id") { Description = "The game id to use instead of one made from the name." };
        var shortName = new Option<string?>("--short-name") { Description = "A short form of the name, such as an abbreviation." };
        var importer = new Option<string?>("--importer") { Description = "The folder name XXMI uses for this game." };
        var mods = new Option<string?>("--mods") { Description = "The game's Mods folder, which 'xxsm studio try' uses." };

        var command = new Command("new", "Start a draft for a game XXSM has never heard of. Only a name is required.")
        {
            name, id, shortName, importer, mods,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var store = provider.GetRequiredService<IStudioDraftStore>();

            var installed = await provider.GetRequiredService<IPackInstaller>().ListInstalledAsync(cancellationToken).ConfigureAwait(false);
            var drafts = await store.ListAsync(cancellationToken).ConfigureAwait(false);

            var draft = PackDrafts.Create(
                new NewGame
                {
                    DisplayName = parse.GetValue(name)!,
                    GameId = parse.GetValue(id),
                    ShortName = parse.GetValue(shortName),
                    Importer = parse.GetValue(importer),
                    ModsDirectory = parse.GetValue(mods) is { Length: > 0 } folder ? Path.GetFullPath(folder) : null,
                },
                [.. installed.Select(p => p.GameId), .. drafts.Select(d => d.GameId)],
                Now(provider));

            var saved = await store.CreateAsync(draft, cancellationToken).ConfigureAwait(false);

            return WriteDraft(parse, draft, saved.Directory, "Started");
        });

        return command;
    }

    private static Command CreateOpen()
    {
        var game = GameArgument();

        var command = new Command("open", "Open an installed pack as a draft, to edit it and export it again.") { game };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var gameId = parse.GetValue(game)!;

            var directory = await provider.GetRequiredService<IPackInstaller>()
                .FindActivePackDirectoryAsync(gameId, cancellationToken)
                .ConfigureAwait(false);

            if (directory is null)
            {
                CliOutput.WriteError($"There is no pack installed for '{gameId}'. Install one first, or start a new game with: xxsm studio new");
                return 1;
            }

            var pack = await provider.GetRequiredService<IGamePackLoader>().LoadAsync(directory, cancellationToken).ConfigureAwait(false);
            var store = provider.GetRequiredService<IStudioDraftStore>();
            var draft = await store.CreateFromPackAsync(pack, cancellationToken).ConfigureAwait(false);

            return WriteDraft(parse, draft, store.GetDraftDirectory(draft.GameId), "Opened");
        });

        return command;
    }

    private static Command CreateDelete()
    {
        var game = GameArgument();

        var command = new Command(
            "delete", "Move a draft to the trash, pictures and all. 'xxsm studio restore' puts it back.")
        {
            game,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var gameId = parse.GetValue(game)!;

            var trashed = await provider.GetRequiredService<IStudioDraftStore>()
                .DeleteAsync(gameId, cancellationToken)
                .ConfigureAwait(false);

            var report = new StudioDeleteReport(
                gameId, trashed.OriginalPath, trashed.TrashedPath, CliOutput.Camel(trashed.Method.ToString()), trashed.InfoFilePath);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report);
            }

            CliOutput.WriteRows(
            [
                ("Deleted", gameId),
                ("Was at", PathDisplay.Show(report.Directory)),
                ("Now at", PathDisplay.Show(report.TrashedPath)),
                ("Undo with", $"xxsm studio restore \"{report.TrashRecord}\""),
            ]);

            return 0;
        });

        return command;
    }

    private static Command CreateRestore()
    {
        var record = new Argument<string>("record")
        {
            Description = "The .trashinfo record that 'xxsm studio delete' reported.",
        };

        var command = new Command(
            "restore", "Put a deleted draft back. Never replaces a draft started since for the same game.")
        {
            record,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var store = provider.GetRequiredService<IStudioDraftStore>();

            var trashed = await provider.GetRequiredService<ITrashService>()
                .ReadRecordAsync(parse.GetValue(record)!, cancellationToken)
                .ConfigureAwait(false);

            var gameId = await store.RestoreAsync(trashed, cancellationToken).ConfigureAwait(false);
            var draft = await store.ReadAsync(gameId, cancellationToken).ConfigureAwait(false);

            return WriteDraft(parse, draft, store.GetDraftDirectory(gameId), "Restored");
        });

        return command;
    }

    private static int WriteDraft(ParseResult parse, PackDraft draft, string directory, string verb)
    {
        if (parse.GetValue(GlobalOptions.Json))
        {
            return CliJson.Write(new StudioDraftReport(
                draft.GameId, draft.Game.DisplayName, draft.Manifest.PackVersion, directory, draft.Variants.Count, null, null));
        }

        CliOutput.WriteRows(
        [
            (verb, draft.Game.DisplayName),
            ("Game id", draft.GameId),
            ("Variants", Count(draft.Variants.Count)),
            ("Folder", directory),
        ]);

        Console.Out.WriteLine();
        Console.Out.WriteLine(draft.Variants.Count == 0
            ? $"Next, bring characters in: xxsm studio import assets {draft.GameId} <assets repository folder or zip>"
            : $"Check it with: xxsm studio check {draft.GameId}");

        return 0;
    }

    private static Command CreateShow()
    {
        var game = GameArgument();
        var command = new Command("show", "Show what a draft holds and how many problems it has.") { game };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var draft = await LoadAsync(provider, parse.GetValue(game)!, cancellationToken).ConfigureAwait(false);
            var directory = provider.GetRequiredService<IStudioDraftStore>().GetDraftDirectory(draft.GameId);
            var validation = PackValidator.Validate(draft, imageBytes: ImageSizes(provider, draft.GameId));

            var report = new StudioShowReport(
                draft.GameId,
                draft.Game.DisplayName,
                draft.Manifest.PackVersion,
                directory,
                draft.Variants.Count,
                draft.Variants.Count(v => v.BaseCharacterId is not null),
                draft.Hashes.Entries?.Count ?? 0,
                [.. (draft.Game.Attributes ?? new Dictionary<string, Xxsm.Packs.Model.AttributeDefinition>()).Keys],
                draft.Info.ModsDirectory,
                validation.ErrorCount,
                validation.WarningCount);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report);
            }

            CliOutput.WriteRows(
            [
                ("Game", report.DisplayName),
                ("Game id", report.GameId),
                ("Version", report.PackVersion),
                ("Variants", $"{Count(report.Variants)} ({EnglishCount.Plural(report.Outfits, "outfit", "outfits")})"),
                ("Hash entries", Count(report.HashEntries)),
                ("Attributes", report.Attributes.Count == 0 ? "—" : string.Join(", ", report.Attributes)),
                ("Mods folder", report.ModsDirectory is { } shown ? PathDisplay.Show(shown) : "—"),
                ("Problems", $"{EnglishCount.Plural(report.Errors, "error", "errors")}, {EnglishCount.Plural(report.Warnings, "warning", "warnings")}"),
                ("Folder", PathDisplay.Show(report.Directory)),
            ]);

            return 0;
        });

        return command;
    }

    private static Command CreateCheck()
    {
        var game = GameArgument();
        var errorsOnly = new Option<bool>("--errors") { Description = "Only list the problems that stop the pack being exported." };
        var kinds = new Option<string[]>("--kind")
        {
            Description = $"Only list problems of this kind, as the Problems panel's filter does: {string.Join(", ", PackProblemKinds.All)}. May be given more than once.",
            AllowMultipleArgumentsPerToken = true,
        };
        var character = new Option<string>("--character")
        {
            Description = "Only list the problems about one character, as pressing its Problems cell in the table does.",
        };

        var command = new Command(
            "check",
            "List a draft's problems, the way the Problems panel does. Exits with 1 when any would stop it being exported.")
        {
            game, errorsOnly, kinds, character,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kind in parse.GetValue(kinds) ?? [])
            {
                wanted.Add(PackProblemKinds.Find(kind) ?? throw new Xxsm.Core.ModOperationException(
                    $"'{kind}' is not a kind of problem. The kinds are: {string.Join(", ", PackProblemKinds.All)}."));
            }

            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var draft = await LoadAsync(provider, parse.GetValue(game)!, cancellationToken).ConfigureAwait(false);
            var directory = provider.GetRequiredService<IStudioDraftStore>().GetDraftDirectory(draft.GameId);
            var result = PackValidator.Validate(draft, imageBytes: ImageSizes(provider, draft.GameId));

            var only = parse.GetValue(character);

            if (only is { Length: > 0 }
                && !draft.Variants.Any(v => string.Equals(v.InternalName, only, StringComparison.OrdinalIgnoreCase)))
            {
                throw new Xxsm.Core.ModOperationException($"'{only}' is not a character in this draft.");
            }

            var problems = result.Problems
                .Where(p => !parse.GetValue(errorsOnly) || p.Severity == DiagnosticSeverity.Error)
                .Where(p => wanted.Count == 0 || wanted.Contains(PackProblemKinds.Of(p.Code)))
                // By subject, as the table counts a character's problems.
                .Where(p => only is not { Length: > 0 } || string.Equals(p.Subject, only, StringComparison.OrdinalIgnoreCase))
                .Select(p => new StudioProblemReport(
                    CliOutput.Camel(p.Severity.ToString()), p.Code, PackProblemKinds.Of(p.Code), p.Message, p.File, p.Subject))
                .ToList();

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(new StudioCheckReport(draft.GameId, result.ErrorCount, result.WarningCount, result.CanExport, problems), result.CanExport ? 0 : 1);
            }

            foreach (var problem in problems)
            {
                Console.Out.WriteLine($"[{problem.Severity}] {problem.Message}");
            }

            if (problems.Count > 0)
            {
                Console.Out.WriteLine();
            }

            if (result.Problems.Count > 0)
            {
                Console.Out.WriteLine("By kind: " + string.Join(", ", result.Problems
                    .GroupBy(p => PackProblemKinds.Of(p.Code))
                    .OrderBy(g => PackProblemKinds.All.ToList().IndexOf(g.Key))
                    .Select(g => $"{g.Key} {Count(g.Count())}")));
            }

            Console.Out.WriteLine(
                $"{EnglishCount.Plural(result.ErrorCount, "error", "errors")}, {EnglishCount.Plural(result.WarningCount, "warning", "warnings")}. " +
                (result.CanExport ? "It can be exported." : "Fix the errors before exporting."));

            return result.CanExport ? 0 : 1;
        });

        return command;
    }

    // Trying, exporting, promoting

    private static Command CreateTry()
    {
        var game = GameArgument();
        var mods = new Option<string?>("--mods") { Description = "The folder of mods to try it against. Defaults to the Mods folder the draft was given." };
        var all = new Option<bool>("--all") { Description = "List every mod, not only the ones that would move." };

        var command = new Command("try", "See where every mod in a folder would go with this draft installed. Moves nothing.")
        {
            game, mods, all,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var draft = await LoadAsync(provider, parse.GetValue(game)!, cancellationToken).ConfigureAwait(false);

            if ((parse.GetValue(mods) ?? draft.Info.ModsDirectory) is not { Length: > 0 } folder)
            {
                CliOutput.WriteError("Say which folder of mods to try it against, with --mods.");
                return 1;
            }

            var plan = await provider.GetRequiredService<IPackTrial>()
                .PlanAsync(draft, Path.GetFullPath(folder), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var rows = (parse.GetValue(all) ? plan.Rows : plan.Moves)
                .Select(r => new StudioTryRowReport(
                    r.Mod.Path, r.DestinationFolderName, CliOutput.Camel(r.Action.ToString()), r.Decision.VariantId, r.Reason))
                .ToList();

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(new StudioTryReport(draft.GameId, plan.ModsDirectory, plan.Rows.Count, plan.Moves.Count, rows));
            }

            foreach (var row in rows)
            {
                Console.Out.WriteLine($"{Path.GetFileName(row.Mod)}  →  {PathDisplay.Show(row.Destination)}");
                Console.Out.WriteLine($"    {row.Reason}");
            }

            if (rows.Count > 0)
            {
                Console.Out.WriteLine();
            }

            Console.Out.WriteLine(
                $"{EnglishCount.Plural(plan.Rows.Count, "mod", "mods")} looked at; " +
                $"{EnglishCount.Plural(plan.Moves.Count, "would move", "would move")}. Nothing was moved.");

            return 0;
        });

        return command;
    }

    private static Command CreateInstall()
    {
        var game = GameArgument();
        var version = new Option<string?>("--version")
        {
            Description = "The version to install as: 26.9.27 or 2026.09.27. Defaults to a new one: today's date, or the next after the last export.",
        };

        var command = new Command(
            "install",
            "Install the draft into this copy of XXSM, without writing a pack file to keep. Refused while it has errors.")
        {
            game, version,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var draft = await LoadAsync(provider, parse.GetValue(game)!, cancellationToken).ConfigureAwait(false);
            var packVersion = parse.GetValue(version) is { Length: > 0 } chosen ? PackVersionText.ToStored(chosen) : PackDrafts.ProposeVersion(draft, Now(provider));

            var result = await provider.GetRequiredService<IPackPublisher>()
                .InstallHereAsync(draft, packVersion, cancellationToken)
                .ConfigureAwait(false);

            await SaveAsync(provider, PackDrafts.AfterExport(draft, result.PackVersion), cancellationToken)
                .ConfigureAwait(false);

            var report = new StudioInstallReport(
                draft.GameId,
                result.PackVersion,
                result.Install.Directory,
                result.SizeBytes,
                result.Validation.WarningCount,
                result.Install.SkippedUpdates.Count);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report);
            }

            CliOutput.WriteRows(
            [
                ("Installed", $"{report.GameId} {report.PackVersion}"),
                ("Where", report.Directory is { } shown ? PathDisplay.Show(shown) : "(unknown)"),
                ("Size", CliOutput.Bytes(report.SizeBytes)),
                ("Warnings", Count(report.Warnings)),
            ]);

            PackRegistryCommands.WriteAfterInstall(result.Install);
            return 0;
        });

        return command;
    }

    private static Command CreateExport()
    {
        var game = GameArgument();
        var output = new Option<string>("--out") { Description = "The folder to write the pack zip to.", Required = true };
        var version = new Option<string?>("--version")
        {
            Description = "The version to export as: 26.9.27 or 2026.09.27. Defaults to a new one: today's date, or the next after the last export.",
        };
        var overwrite = new Option<bool>("--overwrite") { Description = "Replace a pack zip of the same name already there." };
        var registry = new Option<string?>("--registry") { Description = "Also add the pack to this registry folder and its index.json." };
        var changelog = new Option<string?>("--changelog") { Description = "What changed in this version, for the registry." };
        var contribution = new Option<string?>("--contribution")
        {
            Description = "Also write, in this folder, a folder for offering the pack to XXSM's default registry.",
        };

        var command = new Command("export", "Write the draft out as a pack zip anyone can install. Refused while it has errors.")
        {
            game, output, version, overwrite, registry, changelog, contribution,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var draft = await LoadAsync(provider, parse.GetValue(game)!, cancellationToken).ConfigureAwait(false);
            var packVersion = parse.GetValue(version) is { Length: > 0 } chosen ? PackVersionText.ToStored(chosen) : PackDrafts.ProposeVersion(draft, Now(provider));

            var result = await provider.GetRequiredService<IPackExporter>()
                .ExportAsync(draft, Path.GetFullPath(parse.GetValue(output)!), packVersion, parse.GetValue(overwrite), cancellationToken)
                .ConfigureAwait(false);

            draft = PackDrafts.AfterExport(draft, result.PackVersion);
            await SaveAsync(provider, draft, cancellationToken).ConfigureAwait(false);

            var publisher = provider.GetRequiredService<IPackPublisher>();
            string? indexPath = null;
            string? contributionDirectory = null;

            if (parse.GetValue(registry) is { Length: > 0 } registryFolder)
            {
                indexPath = (await publisher.AddToRegistryAsync(draft, result, Path.GetFullPath(registryFolder), parse.GetValue(changelog), cancellationToken)
                    .ConfigureAwait(false)).IndexPath;
            }

            if (parse.GetValue(contribution) is { Length: > 0 } contributionFolder)
            {
                contributionDirectory = (await publisher.WriteContributionAsync(draft, result, Path.GetFullPath(contributionFolder), cancellationToken)
                    .ConfigureAwait(false)).Directory;
            }

            var report = new StudioExportReport(
                result.GameId,
                result.PackVersion,
                result.PackFile,
                result.Sha256,
                result.SizeBytes,
                result.VariantCount,
                result.HashEntryCount,
                result.ImageCount,
                result.Validation.WarningCount,
                indexPath,
                contributionDirectory);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report);
            }

            var rows = new List<(string, string)>
            {
                ("Exported", $"{report.GameId} {report.PackVersion}"),
                ("Pack", PathDisplay.Show(report.PackFile)),
                ("Size", CliOutput.Bytes(report.SizeBytes)),
                ("SHA-256", report.Sha256),
                ("Variants", Count(report.Variants)),
                ("Hash entries", Count(report.HashEntries)),
                ("Pictures", Count(report.Pictures)),
                ("Warnings", Count(report.Warnings)),
            };

            if (indexPath is not null)
            {
                rows.Add(("Registry index", PathDisplay.Show(indexPath)));
            }

            if (contributionDirectory is not null)
            {
                rows.Add(("Contribution", PathDisplay.Show(contributionDirectory)));
            }

            CliOutput.WriteRows(rows);
            Console.Out.WriteLine();
            Console.Out.WriteLine($"Anyone can install it with: xxsm pack import \"{PathDisplay.Show(report.PackFile)}\"");

            return 0;
        });

        return command;
    }

    private static Command CreatePromote()
    {
        var game = GameArgument();
        var dryRun = new Option<bool>("--dry-run") { Description = "Show what would change without changing the draft." };

        var command = new Command(
            "promote",
            "Bring the corrections you made in the Character Manager for this game into the draft. Your corrections stay as they are.")
        {
            game, dryRun,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var draft = await LoadAsync(provider, parse.GetValue(game)!, cancellationToken).ConfigureAwait(false);
            var promotion = provider.GetRequiredService<IOverlayPromotion>();
            var plan = await promotion.PlanAsync(draft, cancellationToken).ConfigureAwait(false);

            var rows = plan.Rows
                .Select(r => new StudioImportRowReport(
                    r.InternalName, r.DisplayName, r.InternalName, CliOutput.Camel(r.Action.ToString()), true,
                    null, null, string.Join(", ", r.Fields), r.Hashes?.Count, r.Notes))
                .ToList();

            var outcome = parse.GetValue(dryRun)
                ? null
                : await promotion.ApplyAsync(draft, plan, null, Now(provider), cancellationToken).ConfigureAwait(false);

            if (outcome is { Changed: true })
            {
                await SaveAsync(provider, outcome.Draft, cancellationToken).ConfigureAwait(false);
            }

            var report = new StudioImportReport(
                draft.GameId,
                plan.OverlayPath,
                outcome is not null,
                outcome?.Created ?? [],
                outcome?.Updated ?? [],
                outcome?.Skipped ?? [],
                rows,
                [.. plan.Diagnostics.Select(d => d.Message)]);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report);
            }

            foreach (var row in plan.Rows)
            {
                Console.Out.WriteLine($"{(row.Action == PromotionAction.Add ? "Add" : "Update")}  {row.DisplayName} ({row.InternalName}): {string.Join(", ", row.Fields)}");

                foreach (var note in row.Notes)
                {
                    Console.Out.WriteLine($"    {note}");
                }
            }

            if (plan.IgnoredHashes.Count > 0)
            {
                Console.Out.WriteLine($"Ignore {EnglishCount.Plural(plan.IgnoredHashes.Count, "hash", "hashes")}: {string.Join(", ", plan.IgnoredHashes)}");
            }

            foreach (var diagnostic in plan.Diagnostics)
            {
                Console.Out.WriteLine(diagnostic.Message);
            }

            StudioImportCommands.WriteOutcome(outcome);
            return 0;
        });

        return command;
    }

    private static Command CreateCaughtUp()
    {
        var game = GameArgument();
        var clear = new Option<bool>("--clear") { Description = "Remove the corrections the installed pack now makes itself." };

        var command = new Command(
            "caught-up",
            "List the corrections the installed pack for a game now makes itself, and clear them if asked.")
        {
            game, clear,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var gameId = parse.GetValue(game)!;

            var directory = await provider.GetRequiredService<IPackInstaller>()
                .FindActivePackDirectoryAsync(gameId, cancellationToken)
                .ConfigureAwait(false);

            if (directory is null)
            {
                CliOutput.WriteError($"There is no pack installed for '{gameId}'.");
                return 1;
            }

            var redundancy = provider.GetRequiredService<IOverlayRedundancy>();
            var found = await redundancy.FindAsync(gameId, directory, cancellationToken).ConfigureAwait(false);
            var names = found.Redundant.Select(r => r.InternalName).ToList();

            var cleared = parse.GetValue(clear) && names.Count > 0
                ? await redundancy.ClearAsync(gameId, directory, names, cancellationToken).ConfigureAwait(false)
                : null;

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(new StudioCaughtUpReport(
                    gameId, found.PackVersion, names, cleared?.Cleared ?? [], cleared?.Kept ?? []));
            }

            if (names.Count == 0)
            {
                Console.Out.WriteLine($"None of your corrections for {gameId} are in pack {found.PackVersion} yet.");
                return 0;
            }

            foreach (var correction in found.Redundant)
            {
                Console.Out.WriteLine($"{correction.DisplayName} ({correction.InternalName}): {string.Join(", ", correction.Fields)}");
            }

            Console.Out.WriteLine();

            Console.Out.WriteLine(cleared is null
                ? $"Pack {found.PackVersion} already makes {EnglishCount.Plural(names.Count, "correction", "corrections")}. Clear them with --clear."
                : $"Cleared {EnglishCount.Plural(cleared.Cleared.Count, "correction", "corrections")}. The previous file is at {cleared.BackupPath}.");

            return 0;
        });

        return command;
    }
}
