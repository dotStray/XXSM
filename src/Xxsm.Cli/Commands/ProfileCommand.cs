using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Xxsm.Cli.Output;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Core.Profiles;
using Xxsm.Core.Text;

namespace Xxsm.Cli.Commands;

/// <summary><c>xxsm profile …</c>: saving which mods are on, and putting that back; by id or name.</summary>
internal static class ProfileCommand
{
    /// <summary>Builds the command.</summary>
    public static Command Create()
    {
        var command = new Command("profile", "Save which mods are switched on, and apply a saved set later.")
        {
            CreateList(),
            CreateShow(),
            CreateSave(),
            CreateUpdate(),
            CreateAdd(),
            CreateRemove(),
            CreateReplace(),
            CreateRename(),
            CreateLock(readOnly: true),
            CreateLock(readOnly: false),
            CreateDelete(),
            CreateRestore(),
            CreateApply(),
        };

        return command;
    }

    private static Argument<string> ProfileArgument() => new("profile")
    {
        Description = "The profile's name or id.",
    };

    private static Command CreateList()
    {
        var mods = ModsFolderOptions.Mods();
        var game = ModsFolderOptions.Game();
        var command = new Command("list", "List the profiles saved for a Mods folder.") { mods, game };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var profiles = provider.GetRequiredService<IProfileService>();
            var modsDirectory = await ModsFolderOptions.ResolveAsync(provider, parse, mods, game, cancellationToken)
                .ConfigureAwait(false);

            var list = await profiles.ListAsync(modsDirectory, cancellationToken).ConfigureAwait(false);

            var report = new ProfileListReport(
                modsDirectory,
                profiles.GetProfilesDirectory(modsDirectory),
                [.. list.Profiles.Select(profile => Report(profile, withEntries: false))],
                list.Problems);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report, report.Problems.Count > 0 ? 1 : 0);
            }

            CliOutput.WriteRows(
            [
                ("Mods folder", PathDisplay.Show(report.ModsDirectory)),
                ("Profiles", PathDisplay.Show(report.ProfilesDirectory)),
            ]);

            if (report.Profiles.Count == 0)
            {
                Console.Out.WriteLine("  No profiles yet. Save one with: xxsm profile save \"<name>\"");
            }

            foreach (var profile in report.Profiles)
            {
                Console.Out.WriteLine(
                    $"  {profile.Id}  {profile.Name}  ({EnglishCount.Plural(profile.EnabledCount, "mod", "mods")} on)" +
                    (profile.ReadOnly ? "  read-only" : string.Empty));
            }

            foreach (var problem in report.Problems)
            {
                CliOutput.WriteError($"Could not read {PathDisplay.Show(problem.Path)}: {problem.Message}");
            }

            return report.Problems.Count > 0 ? 1 : 0;
        });

        return command;
    }

    private static Command CreateShow()
    {
        var mods = ModsFolderOptions.Mods();
        var game = ModsFolderOptions.Game();
        var profile = ProfileArgument();
        var command = new Command("show", "List the mods a profile switches on.") { profile, mods, game };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var profiles = provider.GetRequiredService<IProfileService>();
            var modsDirectory = await ModsFolderOptions.ResolveAsync(provider, parse, mods, game, cancellationToken)
                .ConfigureAwait(false);

            var plan = await profiles.PlanApplyAsync(modsDirectory, parse.GetValue(profile)!, cancellationToken)
                .ConfigureAwait(false);
            var report = Report(plan.Profile, withEntries: true);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report);
            }

            WriteProfile(report);

            foreach (var entry in report.Enabled ?? [])
            {
                // Exact on purpose: Missing holds this profile's entries as stored, and case matters.
                var gone = plan.Missing.Any(missing => missing.Path == entry.Path && missing.Id == entry.Id);
                Console.Out.WriteLine($"  {(gone ? "gone" : "on  ")}  {entry.Path}");
            }

            return 0;
        });

        return command;
    }

    private static Command CreateSave()
    {
        var mods = ModsFolderOptions.Mods();
        var game = ModsFolderOptions.Game();
        var name = new Argument<string>("name") { Description = "What to call the new profile." };
        var empty = new Option<bool>("--empty")
        {
            Description = "Save a profile with no mods in it, whatever is on now. Applying it switches every mod off.",
        };
        var command = new Command(
            "save",
            "Save which mods are switched on now as a new profile. A switched-on mod with no " +
            ".xxsm/mod.json is given one, so the profile can find it after a rename.")
        {
            name,
            empty,
            mods,
            game,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var modsDirectory = await ModsFolderOptions.ResolveAsync(provider, parse, mods, game, cancellationToken)
                .ConfigureAwait(false);

            var profiles = provider.GetRequiredService<IProfileService>();
            var saved = parse.GetValue(empty)
                ? await profiles.SaveEmptyAsync(modsDirectory, parse.GetValue(name)!, cancellationToken).ConfigureAwait(false)
                : await profiles.SaveAsync(modsDirectory, parse.GetValue(name)!, cancellationToken).ConfigureAwait(false);

            return WriteSaved(parse, saved, "Saved");
        });

        return command;
    }

    private static Command CreateUpdate()
    {
        var mods = ModsFolderOptions.Mods();
        var game = ModsFolderOptions.Game();
        var profile = ProfileArgument();
        var command = new Command("update", "Save which mods are switched on now over an existing profile.")
        {
            profile,
            mods,
            game,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var modsDirectory = await ModsFolderOptions.ResolveAsync(provider, parse, mods, game, cancellationToken)
                .ConfigureAwait(false);

            var saved = await provider.GetRequiredService<IProfileService>()
                .UpdateAsync(modsDirectory, parse.GetValue(profile)!, cancellationToken)
                .ConfigureAwait(false);

            return WriteSaved(parse, saved, "Saved over");
        });

        return command;
    }

    private static Argument<string[]> ModsArgument(string description) => new("mod")
    {
        Description = description,
        Arity = ArgumentArity.OneOrMore,
    };

    /// <summary>A mod named on the command line: a folder as given, or inside the Mods folder.</summary>
    private static string ModFolder(string modsDirectory, string given) =>
        Path.IsPathRooted(given) || Directory.Exists(given)
            ? Path.GetFullPath(given)
            : Path.GetFullPath(Path.Combine(modsDirectory, given));

    private static Command CreateAdd()
    {
        var mods = ModsFolderOptions.Mods();
        var game = ModsFolderOptions.Game();
        var profile = ProfileArgument();
        var folders = ModsArgument("Each mod's folder, switched on or off: a path, or one inside the Mods folder such as Klee/Red.");
        var command = new Command(
            "add",
            "Put mods in a profile without switching anything, so applying the profile switches them on. " +
            "A mod with no .xxsm/mod.json is given one, so the profile can find it after a rename.")
        {
            profile,
            folders,
            mods,
            game,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var modsDirectory = await ModsFolderOptions.ResolveAsync(provider, parse, mods, game, cancellationToken)
                .ConfigureAwait(false);

            var result = await provider.GetRequiredService<IProfileService>()
                .AddModsAsync(
                    modsDirectory,
                    parse.GetValue(profile)!,
                    [.. parse.GetValue(folders)!.Select(given => ModFolder(modsDirectory, given))],
                    cancellationToken)
                .ConfigureAwait(false);

            return WriteEdit(parse, result, "Added", "to", []);
        });

        return command;
    }

    private static Command CreateRemove()
    {
        var mods = ModsFolderOptions.Mods();
        var game = ModsFolderOptions.Game();
        var profile = ProfileArgument();
        var folders = ModsArgument(
            "Each mod to take out: its folder, or the path `profile show` lists, which is the only way to name a mod that has gone.");
        var command = new Command(
            "remove",
            "Take mods out of a profile. Switches nothing: the mods themselves are not touched.")
        {
            profile,
            folders,
            mods,
            game,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var modsDirectory = await ModsFolderOptions.ResolveAsync(provider, parse, mods, game, cancellationToken)
                .ConfigureAwait(false);
            var service = provider.GetRequiredService<IProfileService>();
            var wanted = parse.GetValue(profile)!;
            var all = await service.ReadContentsAsync(modsDirectory, cancellationToken: cancellationToken).ConfigureAwait(false);
            var contents = all.FirstOrDefault(found => string.Equals(found.Profile.Id, wanted, StringComparison.Ordinal))
                ?? all.FirstOrDefault(found => string.Equals(found.Profile.Name, wanted, StringComparison.CurrentCultureIgnoreCase))
                ?? throw new ProfileException($"There is no profile '{wanted}' for this Mods folder.");

            var entries = new List<ModProfileEntry>();
            var notInProfile = new List<string>();

            foreach (var given in parse.GetValue(folders)!)
            {
                var folder = ModFolder(modsDirectory, given);
                var matched = contents.Members
                    .Where(member => (member.Mod is { } mod && PathComparer.AreEqual(mod.Path, folder)) ||
                                     PathComparer.AreEqual(member.Entry.Path, given))
                    .Select(member => member.Entry)
                    .ToList();

                if (matched.Count == 0)
                {
                    notInProfile.Add(given);
                }

                entries.AddRange(matched);
            }

            var result = await service.RemoveEntriesAsync(modsDirectory, contents.Profile.Id, entries, cancellationToken)
                .ConfigureAwait(false);

            return WriteEdit(parse, result, "Took", "out of", notInProfile);
        });

        return command;
    }

    private static Command CreateReplace()
    {
        var mods = ModsFolderOptions.Mods();
        var game = ModsFolderOptions.Game();
        var profile = ProfileArgument();
        var old = new Argument<string>("old")
        {
            Description = "The mod to replace: its folder, or the path `profile show` lists, which is how to name one that has gone.",
        };
        var replacement = new Argument<string>("new") { Description = "The mod to put in its place, switched on or off." };
        var command = new Command(
            "replace",
            "Put another mod in a profile where one is, such as one that has gone from the Mods folder. Switches nothing.")
        {
            profile,
            old,
            replacement,
            mods,
            game,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var modsDirectory = await ModsFolderOptions.ResolveAsync(provider, parse, mods, game, cancellationToken)
                .ConfigureAwait(false);
            var service = provider.GetRequiredService<IProfileService>();
            var wanted = parse.GetValue(profile)!;
            var all = await service.ReadContentsAsync(modsDirectory, cancellationToken: cancellationToken).ConfigureAwait(false);
            var contents = all.FirstOrDefault(found => string.Equals(found.Profile.Id, wanted, StringComparison.Ordinal))
                ?? all.FirstOrDefault(found => string.Equals(found.Profile.Name, wanted, StringComparison.CurrentCultureIgnoreCase))
                ?? throw new ProfileException($"There is no profile '{wanted}' for this Mods folder.");
            var given = parse.GetValue(old)!;
            var folder = ModFolder(modsDirectory, given);
            var entry = contents.Members
                .FirstOrDefault(member => (member.Mod is { } mod && PathComparer.AreEqual(mod.Path, folder)) ||
                                          PathComparer.AreEqual(member.Entry.Path, given))?.Entry
                ?? throw new ProfileException($"'{contents.Profile.Name}' has nothing at '{given}'.");

            var result = await service
                .ReplaceEntryAsync(modsDirectory, contents.Profile.Id, entry, ModFolder(modsDirectory, parse.GetValue(replacement)!), cancellationToken)
                .ConfigureAwait(false);

            return WriteEdit(parse, result, "Put", "in", []);
        });

        return command;
    }

    private static int WriteEdit(ParseResult parse, ProfileEditResult result, string verb, string preposition, List<string> notInProfile)
    {
        var report = new ProfileEditReport(
            Report(result.Profile, withEntries: false),
            [.. result.Changed.Select(entry => new ProfileEntryReport(entry.Id, entry.Path, entry.Name))],
            result.AlreadyThere,
            result.RecordedByFolder,
            notInProfile);

        if (parse.GetValue(GlobalOptions.Json))
        {
            return CliJson.Write(report, notInProfile.Count > 0 ? 1 : 0);
        }

        Console.Out.WriteLine(report.Changed.Count == 0
            ? $"Nothing changed in '{report.Profile.Name}'."
            : $"{verb} {EnglishCount.Plural(report.Changed.Count, "mod", "mods")} {preposition} '{report.Profile.Name}'. " +
              "Nothing was switched on or off.");

        foreach (var entry in report.Changed)
        {
            Console.Out.WriteLine($"  {PathDisplay.Show(entry.Path)}");
        }

        if (report.AlreadyThere > 0)
        {
            Console.Out.WriteLine($"  {EnglishCount.Plural(report.AlreadyThere, "mod was", "mods were")} in it already.");
        }

        foreach (var folder in report.RecordedByFolder)
        {
            Console.Out.WriteLine(
                $"  by folder  {PathDisplay.Show(folder)}  (its details could not be read; renaming it will lose it from the profile)");
        }

        foreach (var given in notInProfile)
        {
            Console.Out.WriteLine($"  not in the profile  {given}");
        }

        return notInProfile.Count > 0 ? 1 : 0;
    }

    private static Command CreateRename()
    {
        var mods = ModsFolderOptions.Mods();
        var game = ModsFolderOptions.Game();
        var profile = ProfileArgument();
        var name = new Argument<string>("new-name") { Description = "The profile's new name." };
        var command = new Command("rename", "Give a profile a different name.") { profile, name, mods, game };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var modsDirectory = await ModsFolderOptions.ResolveAsync(provider, parse, mods, game, cancellationToken)
                .ConfigureAwait(false);

            var renamed = await provider.GetRequiredService<IProfileService>()
                .RenameAsync(modsDirectory, parse.GetValue(profile)!, parse.GetValue(name)!, cancellationToken)
                .ConfigureAwait(false);

            return WriteProfile(parse, renamed);
        });

        return command;
    }

    private static Command CreateLock(bool readOnly)
    {
        var mods = ModsFolderOptions.Mods();
        var game = ModsFolderOptions.Game();
        var profile = ProfileArgument();
        var command = new Command(
            readOnly ? "lock" : "unlock",
            readOnly
                ? "Make a profile read-only: it can still be applied, but not saved over, renamed or deleted."
                : "Make a read-only profile editable again.")
        {
            profile,
            mods,
            game,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var modsDirectory = await ModsFolderOptions.ResolveAsync(provider, parse, mods, game, cancellationToken)
                .ConfigureAwait(false);

            var changed = await provider.GetRequiredService<IProfileService>()
                .SetReadOnlyAsync(modsDirectory, parse.GetValue(profile)!, readOnly, cancellationToken)
                .ConfigureAwait(false);

            return WriteProfile(parse, changed);
        });

        return command;
    }

    private static Command CreateDelete()
    {
        var mods = ModsFolderOptions.Mods();
        var game = ModsFolderOptions.Game();
        var profile = ProfileArgument();
        var command = new Command("delete", "Move a profile to the trash. It can be restored.") { profile, mods, game };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var profiles = provider.GetRequiredService<IProfileService>();
            var modsDirectory = await ModsFolderOptions.ResolveAsync(provider, parse, mods, game, cancellationToken)
                .ConfigureAwait(false);

            var found = (await profiles.PlanApplyAsync(modsDirectory, parse.GetValue(profile)!, cancellationToken)
                .ConfigureAwait(false)).Profile;
            var trashed = await profiles.DeleteAsync(modsDirectory, found.Id, cancellationToken).ConfigureAwait(false);

            var report = new ProfileDeleteReport(found.Id, found.Name, trashed.TrashedPath, trashed.InfoFilePath);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report);
            }

            CliOutput.WriteRows(
            [
                ("Deleted", report.Name),
                ("Now at", PathDisplay.Show(report.TrashedPath)),
                ("Put back with", $"xxsm profile restore \"{report.RestoreRecord}\""),
            ]);

            return 0;
        });

        return command;
    }

    private static Command CreateRestore()
    {
        var record = new Argument<string>("record")
        {
            Description = "The .trashinfo file that xxsm profile delete printed.",
        };

        var command = new Command("restore", "Put a deleted profile back.") { record };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var trashed = await provider.GetRequiredService<ITrashService>()
                .ReadRecordAsync(parse.GetValue(record)!, cancellationToken)
                .ConfigureAwait(false);

            await provider.GetRequiredService<IProfileService>()
                .RestoreAsync(trashed, cancellationToken)
                .ConfigureAwait(false);

            Console.Out.WriteLine($"Restored {PathDisplay.Show(trashed.OriginalPath)}");

            return 0;
        });

        return command;
    }

    private static Command CreateApply()
    {
        var mods = ModsFolderOptions.Mods();
        var game = ModsFolderOptions.Game();
        var profile = ProfileArgument();
        var dryRun = new Option<bool>("--dry-run")
        {
            Description = "Show what would be switched and change nothing.",
        };

        var command = new Command(
            "apply",
            "Switch on the mods a profile lists and switch every other mod off. " +
            "Undo with: xxsm switches undo")
        {
            profile,
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

            var plan = await profiles.PlanApplyAsync(modsDirectory, parse.GetValue(profile)!, cancellationToken)
                .ConfigureAwait(false);

            ModSwitchRunResult? run = null;

            if (!parse.GetValue(dryRun) && !plan.IsEmpty)
            {
                run = await profiles.ApplyAsync(plan, cancellationToken).ConfigureAwait(false);
            }

            var errors = run?.Outcomes
                             .Where(outcome => !outcome.Succeeded)
                             .ToDictionary(outcome => outcome.Switch.ModFolder, outcome => outcome.Error, PathComparer.Instance)
                         ?? new Dictionary<string, string?>(PathComparer.Instance);

            var report = new ProfileApplyReport(
                plan.Profile.Name,
                run is not null,
                run?.RunId,
                plan.EnableCount,
                plan.DisableCount,
                plan.UnchangedCount,
                [
                    .. plan.Switches.Select(change => new ProfileSwitchReport(
                        change.Mod.Path, change.Enable, errors.GetValueOrDefault(change.Mod.Path))),
                ],
                [.. plan.Missing.Select(entry => new ProfileEntryReport(entry.Id, entry.Path, entry.Name))],
                [
                    .. plan.Copies.Select(copy => new ProfileCopyReport(
                        copy.Entry.Path, copy.Chosen.Path, [.. copy.Others.Select(other => other.Path)])),
                ],
                errors.Count);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report, report.FailureCount > 0 ? 1 : 0);
            }

            CliOutput.WriteRows(
            [
                ("Profile", report.Profile),
                ("Switch on", report.EnableCount.ToString(CultureInfo.InvariantCulture)),
                ("Switch off", report.DisableCount.ToString(CultureInfo.InvariantCulture)),
                ("Already right", report.UnchangedCount.ToString(CultureInfo.InvariantCulture)),
                ("Not found", report.Missing.Count.ToString(CultureInfo.InvariantCulture)),
            ]);

            foreach (var change in report.Switches)
            {
                Console.Out.WriteLine(change.Error is { } error
                    ? $"  fail  {change.Mod}  —  {error}"
                    : $"  {(change.Enable ? "on " : "off")}   {change.Mod}");
            }

            foreach (var entry in report.Missing)
            {
                Console.Out.WriteLine($"  gone  {PathDisplay.Show(entry.Path)}");
            }

            foreach (var copy in report.Copies)
            {
                Console.Out.WriteLine($"  copy  {PathDisplay.Show(copy.Path)} is there more than once; switching on {copy.Chosen}");
            }

            if (report.RunId is { } runId)
            {
                Console.Out.WriteLine($"Undo with: xxsm switches undo --run {runId}");
            }
            else if (parse.GetValue(dryRun))
            {
                Console.Out.WriteLine("Nothing was switched (--dry-run).");
            }
            else
            {
                Console.Out.WriteLine("Nothing to switch: every mod is already as the profile has it.");
            }

            return report.FailureCount > 0 ? 1 : 0;
        });

        return command;
    }

    private static ProfileReport Report(ModProfile profile, bool withEntries) => new(
        profile.Id,
        profile.Name,
        profile.ReadOnly,
        profile.CreatedAt,
        profile.UpdatedAt,
        profile.Enabled.Count,
        withEntries ? [.. profile.Enabled.Select(entry => new ProfileEntryReport(entry.Id, entry.Path, entry.Name))] : null);

    private static int WriteSaved(ParseResult parse, ProfileSaveResult saved, string verb)
    {
        var report = new ProfileSaveReport(Report(saved.Profile, withEntries: false), saved.RecordedByFolder);

        if (parse.GetValue(GlobalOptions.Json))
        {
            return CliJson.Write(report);
        }

        Console.Out.WriteLine($"{verb} '{report.Profile.Name}' ({report.Profile.Id}): " +
                              $"{EnglishCount.Plural(report.Profile.EnabledCount, "mod", "mods")} switched on.");

        foreach (var folder in report.RecordedByFolder)
        {
            Console.Out.WriteLine(
                $"  by folder  {PathDisplay.Show(folder)}  (its details could not be read; renaming it will lose it from the profile)");
        }

        return 0;
    }

    private static int WriteProfile(ParseResult parse, ModProfile profile)
    {
        var report = Report(profile, withEntries: false);

        if (parse.GetValue(GlobalOptions.Json))
        {
            return CliJson.Write(report);
        }

        WriteProfile(report);
        return 0;
    }

    private static void WriteProfile(ProfileReport report) =>
        CliOutput.WriteRows(
        [
            ("Profile", report.Name),
            ("Id", report.Id),
            ("Read-only", report.ReadOnly ? "yes" : "no"),
            ("Switched on", report.EnabledCount.ToString(CultureInfo.InvariantCulture)),
            ("Saved", report.UpdatedAt.ToString("u", CultureInfo.InvariantCulture)),
        ]);
}

/// <summary><c>xxsm switches …</c>: the runs of a profile or the randomiser, and undoing one.</summary>
internal static class SwitchesCommand
{
    /// <summary>Builds the command.</summary>
    public static Command Create() => new(
        "switches",
        "The mods a profile or the randomiser switched on and off, and undoing a run of them.")
    {
        CreateList(),
        CreateUndo(),
    };

    private static Command CreateList()
    {
        var mods = ModsFolderOptions.Mods();
        var game = ModsFolderOptions.Game();
        var command = new Command("list", "List the runs recorded in a Mods folder, newest first.") { mods, game };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var switcher = provider.GetRequiredService<IModSwitcher>();
            var modsDirectory = await ModsFolderOptions.ResolveAsync(provider, parse, mods, game, cancellationToken)
                .ConfigureAwait(false);

            var runs = await switcher.ListRunsAsync(modsDirectory, cancellationToken).ConfigureAwait(false);

            var report = new SwitchRunsReport(
                modsDirectory,
                switcher.GetJournalPath(modsDirectory),
                [
                    .. runs.Select(run => new SwitchRunReport(
                        run.RunId,
                        run.At,
                        run.Source switch
                        {
                            ModSwitchSource.Profile => "profile",
                            ModSwitchSource.AllOff => "all-off",
                            ModSwitchSource.Install => "install",
                            _ => "randomiser",
                        },
                        run.Label,
                        run.SwitchCount,
                        run.UndoneCount,
                        run.IsFullyUndone)),
                ]);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report);
            }

            CliOutput.WriteRows(
            [
                ("Mods folder", PathDisplay.Show(report.ModsDirectory)),
                ("Journal", PathDisplay.Show(report.JournalPath)),
                ("Runs", report.Runs.Count.ToString(CultureInfo.InvariantCulture)),
            ]);

            foreach (var run in report.Runs)
            {
                var what = run.Label is { Length: > 0 } label ? $"{run.Source} '{label}'" : run.Source;
                var state = run.FullyUndone ? "undone" : "undoable";

                Console.Out.WriteLine(
                    $"  {run.RunId}  {run.At.ToString("u", CultureInfo.InvariantCulture)}  {what}  " +
                    $"{EnglishCount.Plural(run.SwitchCount, "switch", "switches")}  {state}");
            }

            return 0;
        });

        return command;
    }

    private static Command CreateUndo()
    {
        var mods = ModsFolderOptions.Mods();
        var game = ModsFolderOptions.Game();
        var run = new Option<string?>("--run")
        {
            Description = "The run to undo. Defaults to the most recent one that has not been undone.",
        };

        var command = new Command("undo", "Switch every mod a run switched back the way it was.") { mods, game, run };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var modsDirectory = await ModsFolderOptions.ResolveAsync(provider, parse, mods, game, cancellationToken)
                .ConfigureAwait(false);

            var result = await provider.GetRequiredService<IModSwitcher>()
                .UndoAsync(modsDirectory, parse.GetValue(run), cancellationToken)
                .ConfigureAwait(false);

            var report = new SwitchUndoReport(
                result.RunId,
                result.RestoredCount,
                result.NotRestored.Count,
                [.. result.Outcomes.Select(outcome => new SwitchUndoRowReport(outcome.From, outcome.To, outcome.Restored, outcome.Reason))]);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report, report.SkippedCount > 0 ? 1 : 0);
            }

            CliOutput.WriteRows(
            [
                ("Run", report.RunId),
                ("Put back", report.RestoredCount.ToString(CultureInfo.InvariantCulture)),
                ("Skipped", report.SkippedCount.ToString(CultureInfo.InvariantCulture)),
            ]);

            foreach (var row in report.Rows)
            {
                Console.Out.WriteLine(row.Restored
                    ? $"  ok    {row.From}  ->  {row.To}"
                    : $"  skip  {row.From}  —  {row.Reason}");
            }

            return report.SkippedCount > 0 ? 1 : 0;
        });

        return command;
    }
}
