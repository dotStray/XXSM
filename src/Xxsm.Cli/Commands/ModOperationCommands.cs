using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Xxsm.Cli.Output;
using Xxsm.Core;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Core.Profiles;
using Xxsm.Core.Text;
using Xxsm.Packs.Merge;
using Xxsm.Packs.Pictures;
using Xxsm.Packs.Sorting;

namespace Xxsm.Cli.Commands;

/// <summary>The <c>xxsm mod</c> subcommands that change files, through the services the app uses.</summary>
internal static class ModOperationCommands
{
    /// <summary>Builds the subcommands.</summary>
    public static IEnumerable<Command> Create() =>
    [
        CreateList(),
        CreateSetEnabled("enable", enabled: true),
        CreateSetEnabled("disable", enabled: false),
        CreateDisableAll(),
        CreateRenameFolder(),
        CreateProposeName(),
        CreateSetDisplayName(),
        CreateSetDetails(),
        CreateMove(),
        CreateForgetFiling(),
        CreateMoveBack(),
        CreateInstall(),
        CreateDelete(),
        CreateDeleteEmpty(),
        CreateRestore(),
        CreateMoveTrashOut(),
        CreateTidyLeftovers(),
        CreateSetPreview(),
        CreateClearPreview(),
    ];

    private static Command CreateList()
    {
        var mods = ModsFolderOptions.Mods();
        var game = ModsFolderOptions.Game();
        var command = new Command("list", "List the mods in a Mods folder.") { mods, game };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var modsDirectory = await ModsFolderOptions
                .ResolveAsync(provider, parse, mods, game, cancellationToken)
                .ConfigureAwait(false);

            var inventory = await provider
                .GetRequiredService<IModRepository>()
                .ScanAsync(modsDirectory, cancellationToken)
                .ConfigureAwait(false);

            var report = ModsReportBuilder.Build(inventory, knownVariants: null);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report);
            }

            ModsReportBuilder.WriteHuman(report);
            return 0;
        });

        return command;
    }

    private static Command CreateSetEnabled(string name, bool enabled)
    {
        var folder = new Argument<string>("folder")
        {
            Description = "The mod folder, in either state.",
        };

        var command = new Command(
            name,
            enabled
                ? "Enable a mod, so 3DMigoto loads it."
                : "Disable a mod, so 3DMigoto ignores it.")
        {
            folder,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var result = await provider
                .GetRequiredService<IModFileOperations>()
                .SetEnabledAsync(parse.GetValue(folder)!, enabled, cancellationToken)
                .ConfigureAwait(false);

            Report(parse, result);
            return 0;
        });

        return command;
    }

    /// <summary><c>xxsm mod disable-all</c>: every mod off as one run, undone with <c>xxsm switches undo</c>.</summary>
    private static Command CreateDisableAll()
    {
        var mods = ModsFolderOptions.Mods();
        var game = ModsFolderOptions.Game();
        var dryRun = new Option<bool>("--dry-run") { Description = "Show what would be switched off and change nothing." };

        var command = new Command(
            "disable-all",
            "Switch every mod in the Mods folder off, so 3DMigoto loads none. Undo with: xxsm switches undo")
        {
            mods,
            game,
            dryRun,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var profiles = provider.GetRequiredService<IProfileService>();
            var modsDirectory = await ModsFolderOptions.ResolveAsync(provider, parse, mods, game, cancellationToken)
                .ConfigureAwait(false);

            var plan = await profiles.PlanAllOffAsync(modsDirectory, cancellationToken).ConfigureAwait(false);
            var run = !parse.GetValue(dryRun) && !plan.IsEmpty
                ? await profiles.ApplyAsync(plan, cancellationToken).ConfigureAwait(false)
                : null;

            var errors = run?.Outcomes
                             .Where(outcome => !outcome.Succeeded)
                             .ToDictionary(outcome => outcome.Switch.ModFolder, outcome => outcome.Error, PathComparer.Instance)
                         ?? new Dictionary<string, string?>(PathComparer.Instance);

            var report = new DisableAllReport(
                run is not null,
                run?.RunId,
                plan.DisableCount,
                plan.UnchangedCount,
                [.. plan.Switches.Select(change => new ProfileSwitchReport(change.Mod.Path, false, errors.GetValueOrDefault(change.Mod.Path)))],
                errors.Count);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report, report.FailureCount > 0 ? 1 : 0);
            }

            foreach (var change in report.Switches)
            {
                Console.Out.WriteLine(change.Error is { } error ? $"  fail  {change.Mod}  —  {error}" : $"  off   {change.Mod}");
            }

            if (report.RunId is { } runId)
            {
                Console.Out.WriteLine(
                    $"{EnglishCount.Plural(report.DisableCount - report.FailureCount, "mod", "mods")} switched off. " +
                    $"Undo with: xxsm switches undo --run {runId}");
            }
            else if (plan.IsEmpty)
            {
                Console.Out.WriteLine("Nothing to switch: every mod is already off.");
            }
            else
            {
                Console.Out.WriteLine($"{EnglishCount.Plural(report.DisableCount, "mod", "mods")} would be switched off (--dry-run).");
            }

            return report.FailureCount > 0 ? 1 : 0;
        });

        return command;
    }

    /// <summary><c>xxsm mod rename-folder</c>: renames the directory on disk, not the display name.</summary>
    private static Command CreateRenameFolder()
    {
        var folder = new Argument<string>("folder") { Description = "The mod or character folder to rename." };
        var newName = new Argument<string>("new-name")
        {
            Description = "The name to give the folder. Any DISABLED_ prefix stays as it is.",
        };

        var command = new Command(
            "rename-folder",
            "Rename a mod's or a character's folder on disk. Refuses a name that is taken, even by " +
            "capitalisation alone, and says which is free.")
        {
            folder,
            newName,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var result = await provider
                .GetRequiredService<IModFileOperations>()
                .RenameAsync(parse.GetValue(folder)!, parse.GetValue(newName)!, cancellationToken)
                .ConfigureAwait(false);

            Report(parse, result);
            return 0;
        });

        return command;
    }

    /// <summary><c>xxsm mod propose-name</c> — what a folder could be called, without renaming it.</summary>
    private static Command CreateProposeName()
    {
        var folder = new Argument<string>("folder") { Description = "The mod folder that would be renamed." };
        var wanted = new Argument<string>("name") { Description = "The name to ask about." };

        var command = new Command(
            "propose-name",
            "Ask what a mod folder could be called. Renames nothing.")
        {
            folder,
            wanted,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var proposal = await provider
                .GetRequiredService<IModFileOperations>()
                .ProposeFolderNameAsync(parse.GetValue(folder)!, parse.GetValue(wanted)!, cancellationToken)
                .ConfigureAwait(false);

            var report = new ModNameProposalReport(
                proposal.Wanted, proposal.Name, proposal.IsFree, proposal.CollidesWith);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report);
            }

            CliOutput.WriteRows(
            [
                ("Asked for", report.Wanted),
                ("Free", report.Free ? "yes" : $"no — '{report.CollidesWith}' is already there"),
                ("Suggestion", report.Name),
            ]);

            return 0;
        });

        return command;
    }

    /// <summary><c>xxsm mod set-name</c>: changes the label and nothing on disk, keeping unknown keys.</summary>
    private static Command CreateSetDisplayName()
    {
        var folder = new Argument<string>("folder") { Description = "The mod folder to label." };
        var displayName = new Argument<string?>("name")
        {
            Description = "The name to show. Omit it with --clear to fall back to the folder name.",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var clear = new Option<bool>("--clear")
        {
            Description = "Remove the custom name, so the folder name is shown again.",
        };

        var command = new Command(
            "set-name",
            "Set the name XXSM shows for a mod. The folder on disk is not touched.")
        {
            folder,
            displayName,
            clear,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var modFolder = parse.GetValue(folder)!;
            var wanted = parse.GetValue(displayName);
            var clearing = parse.GetValue(clear);

            if (clearing == (wanted is { Length: > 0 }))
            {
                Console.Error.WriteLine(
                    "Give a name, or --clear to remove the one that is there. Not both, not neither.");
                return 1;
            }

            var store = provider.GetRequiredService<IModConfigStore>();
            var before = await store.ReadAsync(modFolder, cancellationToken).ConfigureAwait(false);
            var after = clearing ? null : wanted!.Trim();

            var changed = !string.Equals(before?.CustomName, after, StringComparison.Ordinal);

            if (changed)
            {
                await store
                    .UpdateAsync(modFolder, config => config with { CustomName = after }, cancellationToken)
                    .ConfigureAwait(false);
            }

            var report = new ModDisplayNameReport(
                modFolder, Path.GetFileName(modFolder.TrimEnd(Path.DirectorySeparatorChar)), after, changed);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report);
            }

            CliOutput.WriteRows(
            [
                ("Mod folder", PathDisplay.Show(report.Folder)),
                ("Folder name", report.FolderName),
                ("Shown as", report.DisplayName ?? $"{report.FolderName} (the folder's own name)"),
                ("Changed", report.Changed ? "yes" : "no — it was already like that"),
            ]);

            return 0;
        });

        return command;
    }

    /// <summary><c>xxsm mod set-details</c>: author, version, description and notes; <c>""</c> clears one.</summary>
    private static Command CreateSetDetails()
    {
        var folder = new Argument<string>("folder") { Description = "The mod folder." };
        var author = new Option<string?>("--author") { Description = "Who made it. \"\" clears it." };
        var version = new Option<string?>("--version") { Description = "Its version. \"\" clears it." };
        var description = new Option<string?>("--description") { Description = "What it is. \"\" clears it." };
        var notes = new Option<string?>("--notes") { Description = "Your own notes. \"\" clears them." };

        var command = new Command(
            "set-details",
            "Set a mod's author, version, description or notes. What is left out is kept.")
        {
            folder, author, version, description, notes,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var modFolder = parse.GetValue(folder)!;
            var store = provider.GetRequiredService<IModConfigStore>();
            var before = await store.ReadAsync(modFolder, cancellationToken).ConfigureAwait(false) ?? new ModConfig();

            // Left out keeps; "" clears; anything else is trimmed and stored.
            static string? Next(string? given, string? current) =>
                given is null ? current : given.Trim() is { Length: > 0 } value ? value : null;

            var after = before with
            {
                Author = Next(parse.GetValue(author), before.Author),
                Version = Next(parse.GetValue(version), before.Version),
                Description = Next(parse.GetValue(description), before.Description),
                Notes = Next(parse.GetValue(notes), before.Notes),
            };

            var changed = after.Author != before.Author || after.Version != before.Version
                          || after.Description != before.Description || after.Notes != before.Notes;

            if (changed)
            {
                await store
                    .UpdateAsync(
                        modFolder,
                        config => config with
                        {
                            Author = after.Author,
                            Version = after.Version,
                            Description = after.Description,
                            Notes = after.Notes,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var report = new ModDetailsReport(
                modFolder, after.Author, after.Version, after.Description, after.Notes, changed);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report);
            }

            CliOutput.WriteRows(
            [
                ("Mod folder", PathDisplay.Show(report.Folder)),
                ("Author", report.Author ?? "(none)"),
                ("Version", report.Version ?? "(none)"),
                ("Description", report.Description ?? "(none)"),
                ("Notes", report.Notes ?? "(none)"),
                ("Changed", report.Changed ? "yes" : "no — it was already like that"),
            ]);

            return 0;
        });

        return command;
    }

    private static Command CreateMove()
    {
        var folder = new Argument<string>("folder") { Description = "The mod folder to move." };
        var variant = new Argument<string>("variant")
        {
            Description = "The character folder to move it into, by name.",
        };

        var mods = ModsFolderOptions.Mods();
        var game = ModsFolderOptions.Game();
        var command = new Command(
            "move",
            "Move a mod under a different character, and remember you filed it there so auto-sort leaves it alone.")
        {
            folder,
            variant,
            mods,
            game,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var modsDirectory = await ModsFolderOptions
                .ResolveAsync(provider, parse, mods, game, cancellationToken)
                .ConfigureAwait(false);

            var target = parse.GetValue(variant)!;
            var variantId = await ResolveVariantIdAsync(provider, parse.GetValue(game), target, cancellationToken)
                .ConfigureAwait(false);

            var filed = await provider
                .GetRequiredService<IModFiling>()
                .MoveAsync(parse.GetValue(folder)!, Path.Combine(modsDirectory, target), variantId, cancellationToken)
                .ConfigureAwait(false);

            Report(
                parse,
                filed.Move,
                filed.RememberError is null ? variantId : null,
                filed.RememberError,
                movedByHand: true,
                previousFiling: filed.PreviousFiling);

            return filed.RememberError is null ? 0 : 1;
        });

        return command;
    }

    private static Command CreateMoveBack()
    {
        var folder = new Argument<string>("folder") { Description = "Where the mod is now." };
        var original = new Argument<string>("original")
        {
            Description = "Where it was before, as `xxsm mod move` printed it.",
        };

        var filedUnder = new Option<string?>("--filed-under")
        {
            Description = "The character it was filed under before the move. Without it, the filing is forgotten.",
        };

        var command = new Command(
            "move-back",
            "Undo a `mod move`: put the mod back where it was and restore what it was filed under. " +
            "Never renames on the way back.")
        {
            folder,
            original,
            filedUnder,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var result = await provider
                .GetRequiredService<IModFiling>()
                .UndoMoveAsync(
                    parse.GetValue(folder)!,
                    Path.GetFullPath(parse.GetValue(original)!),
                    parse.GetValue(filedUnder),
                    cancellationToken)
                .ConfigureAwait(false);

            Report(parse, result, parse.GetValue(filedUnder));
            return 0;
        });

        return command;
    }

    private static Command CreateForgetFiling()
    {
        var folder = new Argument<string>("folder") { Description = "The mod folder." };

        var command = new Command(
            "forget-filing",
            "Forget that you filed a mod by hand, so the next auto-sort decides where it belongs — the character folder it " +
            "is in no longer counts as filing it either. Moves nothing.")
        {
            folder,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var path = parse.GetValue(folder)!;

            var forgot = await provider
                .GetRequiredService<IModFiling>()
                .LetAutoSortDecideAsync(path, cancellationToken)
                .ConfigureAwait(false);

            var report = new ModFilingForgetReport(path, forgot);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report);
            }

            CliOutput.WriteRows(
            [
                ("Mod", PathDisplay.Show(report.Folder)),
                ("Forgot", forgot
                    ? "yes — the next auto-sort decides where it belongs"
                    : "nobody had filed it by hand; the next auto-sort still decides, its folder included"),
            ]);

            return 0;
        });

        return command;
    }

    /// <summary>The internal name to remember a filing under, for the character folder the user named.</summary>
    /// <remarks>Without a game or a pack, the folder name as given.</remarks>
    private static async Task<string> ResolveVariantIdAsync(
        ServiceProvider provider, string? gameId, string folderName, CancellationToken cancellationToken)
    {
        if (gameId is not { Length: > 0 })
        {
            return folderName;
        }

        GameData data;

        try
        {
            data = await SortCommand.LoadGameDataAsync(provider, gameId, null, cancellationToken).ConfigureAwait(false);
        }
        catch (PackLoadException)
        {
            return folderName;
        }

        return (UnsortedMods.CharacterFor(folderName, data) ?? data.Find(folderName))?.InternalName ?? folderName;
    }

    private static Command CreateInstall()
    {
        var source = new Argument<string>("folder") { Description = "The folder to install. Left untouched." };
        var variant = new Argument<string>("variant")
        {
            Description = "The character folder to install it under, by name.",
        };

        var mods = ModsFolderOptions.Mods();
        var game = ModsFolderOptions.Game();
        var name = new Option<string?>("--name")
        {
            Description = "The name to give the installed mod. Defaults to the source folder's own name.",
        };

        var command = new Command(
            "install",
            "Copy a folder into the Mods folder as a new mod, filed under the character you name and remembered there.")
        {
            source,
            variant,
            mods,
            game,
            name,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var modsDirectory = await ModsFolderOptions
                .ResolveAsync(provider, parse, mods, game, cancellationToken)
                .ConfigureAwait(false);

            var target = parse.GetValue(variant)!;
            var variantId = await ResolveVariantIdAsync(provider, parse.GetValue(game), target, cancellationToken)
                .ConfigureAwait(false);

            var result = await provider
                .GetRequiredService<IModFileOperations>()
                .InstallAsync(
                    parse.GetValue(source)!,
                    Path.Combine(modsDirectory, target),
                    parse.GetValue(name),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // Naming the character is choosing it; a choice that could not be saved is said, not thrown.
            string? filingError = null;

            try
            {
                await provider.GetRequiredService<IModFiling>()
                    .RememberAsync(result.ToPath, variantId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ModOperationException ex)
            {
                filingError = ex.Message;
            }

            Report(parse, result, filingError is null ? variantId : null, filingError);
            return filingError is null ? 0 : 1;
        });

        return command;
    }

    private static Command CreateDelete()
    {
        var folder = new Argument<string>("folder") { Description = "The mod folder to delete." };
        var mods = ModsFolderOptions.Mods();
        var game = ModsFolderOptions.Game();

        var command = new Command("delete", "Move a mod to the trash. Nothing is ever unlinked.")
        {
            folder,
            mods,
            game,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var modsDirectory = await ModsFolderOptions
                .ResolveAsync(provider, parse, mods, game, cancellationToken)
                .ConfigureAwait(false);

            var result = await provider
                .GetRequiredService<IModFileOperations>()
                .DeleteAsync(parse.GetValue(folder)!, modsDirectory, cancellationToken)
                .ConfigureAwait(false);

            Report(parse, result);
            return 0;
        });

        return command;
    }

    /// <summary><c>xxsm mod delete-empty</c>: the Notices page's Delete for an empty character folder.</summary>
    private static Command CreateDeleteEmpty()
    {
        var folder = new Argument<string>("folder")
        {
            Description = "The character folder to delete. It must have no mods in it.",
        };
        var mods = ModsFolderOptions.Mods();
        var game = ModsFolderOptions.Game();

        var command = new Command(
            "delete-empty",
            "Move a character folder with no mods in it to the trash. Refuses one that has a mod.")
        {
            folder,
            mods,
            game,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var modsDirectory = await ModsFolderOptions
                .ResolveAsync(provider, parse, mods, game, cancellationToken)
                .ConfigureAwait(false);

            var result = await provider
                .GetRequiredService<IModFileOperations>()
                .DeleteEmptyCharacterFolderAsync(parse.GetValue(folder)!, modsDirectory, cancellationToken)
                .ConfigureAwait(false);

            Report(parse, result);
            return 0;
        });

        return command;
    }

    /// <summary><c>xxsm mod move-trash-out</c>: moves an old trash folder out of Mods to beside it.</summary>
    private static Command CreateMoveTrashOut()
    {
        var folder = new Argument<string>("folder")
        {
            Description = "The .xxsm-trash folder inside the Mods folder, as 'xxsm mod list' reported it.",
        };
        var mods = ModsFolderOptions.Mods();
        var game = ModsFolderOptions.Game();

        var command = new Command(
            "move-trash-out",
            "Move a trash folder that is inside the Mods folder to beside it, where the game does not load " +
            "the mods in it. Nothing is emptied.")
        {
            folder,
            mods,
            game,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var modsDirectory = await ModsFolderOptions
                .ResolveAsync(provider, parse, mods, game, cancellationToken)
                .ConfigureAwait(false);

            var result = await provider
                .GetRequiredService<IModFileOperations>()
                .MoveTrashOutAsync(parse.GetValue(folder)!, modsDirectory, cancellationToken)
                .ConfigureAwait(false);

            if (parse.GetValue(GlobalOptions.Json))
            {
                Console.Out.WriteLine(CliJson.Serialize(result));
            }
            else
            {
                Console.Out.WriteLine(
                    $"{EnglishCount.Plural(result.Moved, "item", "items")} moved to {result.Destination}.");

                foreach (var problem in result.Problems)
                {
                    Console.Out.WriteLine($"  not moved: {problem}");
                }
            }

            return result.Problems.Count == 0 ? 0 : 1;
        });

        return command;
    }

    /// <summary><c>xxsm mod tidy-leftovers</c>: spares to the trash, a mod under a working name put back.</summary>
    private static Command CreateTidyLeftovers()
    {
        var mods = ModsFolderOptions.Mods();
        var game = ModsFolderOptions.Game();

        var command = new Command(
            "tidy-leftovers",
            "Tidy what an interrupted update or move left in the Mods folder: spare copies go to the trash, a mod " +
            "left under a working name gets its own name back. Anything XXSM cannot place is left alone and listed.")
        {
            mods,
            game,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var modsDirectory = await ModsFolderOptions
                .ResolveAsync(provider, parse, mods, game, cancellationToken)
                .ConfigureAwait(false);

            var inventory = await provider
                .GetRequiredService<IModRepository>()
                .ScanAsync(modsDirectory, cancellationToken)
                .ConfigureAwait(false);

            List<string> Found(string code) =>
                [.. inventory.Diagnostics.Where(d => d.Code == code && d.Paths.Count > 0).Select(d => d.Paths[0])];

            var leftovers = provider.GetRequiredService<IModsFolderLeftovers>();
            var trashed = await leftovers
                .TrashSpareCopiesAsync(Found(ModDiagnosticCodes.LeftoverSpareCopy), modsDirectory, cancellationToken)
                .ConfigureAwait(false);
            var putBack = await leftovers
                .PutBackAsync(Found(ModDiagnosticCodes.LeftoverOnlyCopy), modsDirectory, cancellationToken)
                .ConfigureAwait(false);

            // One XXSM cannot place is reported, not touched.
            var unplaced = inventory.Diagnostics
                .Where(d => d.Code == ModDiagnosticCodes.LeftoverUnrecognised && d.Paths.Count > 0)
                .Select(d => new LeftoverProblem(d.Paths[0], d.Message));
            var result = new LeftoverTidyResult(
                trashed.Trashed, putBack.PutBack, [.. trashed.Problems, .. putBack.Problems, .. unplaced]);

            if (parse.GetValue(GlobalOptions.Json))
            {
                Console.Out.WriteLine(CliJson.Serialize(result));
            }
            else if (result.Trashed.Count + result.PutBack.Count + result.Problems.Count == 0)
            {
                Console.Out.WriteLine($"Nothing was left behind in {PathDisplay.Show(modsDirectory)}.");
            }
            else
            {
                foreach (var item in result.Trashed)
                {
                    Console.Out.WriteLine($"Moved to the trash: {PathDisplay.Show(item.OriginalPath)}");
                    Console.Out.WriteLine($"  undo with: xxsm mod restore '{PathDisplay.Show(item.InfoFilePath)}'");
                }

                foreach (var item in result.PutBack)
                {
                    Console.Out.WriteLine($"Put back: {item.From} -> {item.To}");
                }

                foreach (var problem in result.Problems)
                {
                    Console.Out.WriteLine($"Left alone: {problem.Reason}");
                }
            }

            return result.Problems.Count == 0 ? 0 : 1;
        });

        return command;
    }

    private static Command CreateRestore()
    {
        var record = new Argument<string>("record")
        {
            Description = "The .trashinfo record that 'xxsm mod delete' reported.",
        };

        var command = new Command(
            "restore", "Put a deleted mod back where it was. Never overwrites anything in the way.")
        {
            record,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var trashed = await provider
                .GetRequiredService<ITrashService>()
                .ReadRecordAsync(parse.GetValue(record)!, cancellationToken)
                .ConfigureAwait(false);

            var result = await provider
                .GetRequiredService<IModFileOperations>()
                .RestoreAsync(trashed, cancellationToken)
                .ConfigureAwait(false);

            Report(parse, result);
            return 0;
        });

        return command;
    }

    private static Command CreateSetPreview()
    {
        var folder = new Argument<string>("folder") { Description = "The mod folder." };
        var image = new Argument<string>("image")
        {
            Description =
                "The picture to show for it: a png, jpg, webp, bmp or gif file, or the web address of one. " +
                "Copied in; the original is left alone.",
        };

        var command = new Command("set-preview", "Choose the picture shown for a mod.") { folder, image };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var given = parse.GetValue(image)!;
            var picture = Uri.TryCreate(given, UriKind.Absolute, out var address) && address.Scheme is "http" or "https" or "data"
                ? await provider.GetRequiredService<IPictureDownloader>().DownloadAsync(address, cancellationToken).ConfigureAwait(false)
                : PreviewImageSource.FromFile(given);

            var preview = await provider
                .GetRequiredService<IModPreviewEditor>()
                .SetAsync(parse.GetValue(folder)!, picture, cancellationToken)
                .ConfigureAwait(false);

            var report = new ModPreviewSetReport(parse.GetValue(folder)!, preview.RelativePath, preview.Path);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report);
            }

            CliOutput.WriteRows([("Folder", PathDisplay.Show(report.Folder)), ("Image", PathDisplay.Show(report.Image))]);
            return 0;
        });

        return command;
    }

    private static Command CreateClearPreview()
    {
        var folder = new Argument<string>("folder") { Description = "The mod folder." };

        var command = new Command(
            "clear-preview",
            "Remove the picture shown for a mod, so it shows none. A picture XXSM stored goes to the trash; " +
            "the author's own files are left alone.")
        {
            folder,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var cleared = await provider
                .GetRequiredService<IModPreviewEditor>()
                .ClearAsync(parse.GetValue(folder)!, cancellationToken)
                .ConfigureAwait(false);

            var report = new ModPreviewClearReport(parse.GetValue(folder)!, cleared);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report);
            }

            CliOutput.WriteRows(
            [
                ("Folder", PathDisplay.Show(report.Folder)),
                ("Picture", report.Cleared ? "removed" : "there was none to remove"),
            ]);

            return 0;
        });

        return command;
    }

    private static void Report(
        System.CommandLine.ParseResult parse,
        ModOperationResult result,
        string? filedUnder = null,
        string? filingError = null,
        bool movedByHand = false,
        string? previousFiling = null)
    {
        var report = new ModOperationReport(
            result.Kind.ToString().ToLowerInvariant(),
            result.FromPath,
            result.ToPath,
            result.Changed,
            CliOutput.Camel(result.Method.ToString()),
            result.DisambiguatedFromName,
            result.Trash is { } trashed ? CliOutput.Camel(trashed.Method.ToString()) : null,
            result.Kind == ModOperationKind.Delete ? result.Trash?.InfoFilePath : null,
            filedUnder,
            filingError,
            previousFiling);

        if (parse.GetValue(GlobalOptions.Json))
        {
            Console.Out.WriteLine(CliJson.Serialize(report));
            return;
        }

        var rows = new List<(string, string)>
        {
            ("Operation", report.Operation),
            ("From", report.From),
            ("To", report.To),
            ("Changed", report.Changed ? "yes" : "no — it was already like that"),
        };

        if (report.RenamedFrom is { Length: > 0 } renamed)
        {
            rows.Add(("Renamed", $"'{renamed}' was taken, so it became '{Path.GetFileName(report.To)}'"));
        }

        if (report.TrashMethod is { Length: > 0 } trash)
        {
            rows.Add(("Trash", trash));
        }

        if (report.TrashRecord is { Length: > 0 } trashRecord)
        {
            rows.Add(("Undo with", $"xxsm mod restore \"{trashRecord}\""));
        }

        if (report.FiledUnder is { Length: > 0 } filed)
        {
            rows.Add(("Filed by you", $"{filed} — auto-sort will leave it there"));
        }

        if (report.FilingError is { Length: > 0 } notRemembered)
        {
            rows.Add(("Not remembered", notRemembered));
        }

        if (movedByHand && report.Changed)
        {
            var restore = report.PreviousFiling is { Length: > 0 } previous ? $" --filed-under {previous}" : string.Empty;
            rows.Add(("Undo with", $"xxsm mod move-back \"{report.To}\" \"{report.From}\"{restore}"));
        }

        CliOutput.WriteRows(rows);
    }
}
