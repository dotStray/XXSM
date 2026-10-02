using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Xxsm.Cli.Output;
using Xxsm.Core.Diagnostics;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Core.Text;
using Xxsm.Packs.Characters;
using Xxsm.Packs.Hashes;
using Xxsm.Packs.Installation;
using Xxsm.Packs.Merge;
using Xxsm.Packs.Model;
using Xxsm.Packs.Portraits;

namespace Xxsm.Cli.Commands;

/// <summary><c>xxsm character</c>: the Character Manager, headless.</summary>
internal static class CharacterCommand
{
    /// <summary>Builds the command.</summary>
    public static Command Create() =>
        new("character", "Create, edit and delete characters, and teach them their hashes.")
        {
            CreateList(),
            CreateShow(),
            CreateCreate(),
            CreateEdit(),
            CreateDelete(),
            CreateRestore(),
            CreateHashes(),
            CreateLearn(),
        };

    private static Option<string> GameOption() => new("--game")
    {
        Description = "Which game's characters to work with.",
        Required = true,
    };

    private static Option<string?> PackOption() => new("--pack")
    {
        Description = "Use a pack directory directly instead of the installed one.",
    };

    /// <summary>Loads the merged data; a game with only an overlay and no pack is fine here.</summary>
    private static async Task<GameData> LoadAsync(
        ServiceProvider provider, string gameId, string? explicitPack, CancellationToken cancellationToken)
    {
        var directory = explicitPack ?? await provider
            .GetRequiredService<IPackInstaller>()
            .FindActivePackDirectoryAsync(gameId, cancellationToken)
            .ConfigureAwait(false);

        return await provider.GetRequiredService<IGameDataService>()
            .LoadAsync(gameId, directory, cancellationToken)
            .ConfigureAwait(false);
    }

    // Reading

    private static Command CreateList()
    {
        var game = GameOption();
        var pack = PackOption();

        var customised = new Option<bool>("--customised")
        {
            Description = "Only the characters you have created or edited.",
        };

        var pending = new Option<bool>("--hashes-pending")
        {
            Description = "Only the characters with no hashes yet.",
        };

        var hidden = new Option<bool>("--include-hidden")
        {
            Description = "Include characters you have hidden from the grid.",
        };

        var command = new Command("list", "List a game's characters.") { game, pack, customised, pending, hidden };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var data = await LoadAsync(provider, parse.GetValue(game)!, parse.GetValue(pack), cancellationToken)
                .ConfigureAwait(false);

            var rows = (parse.GetValue(hidden) ? data.Variants : [.. data.VisibleVariants])
                .Where(variant => !parse.GetValue(customised) || variant.IsCustomised)
                .Where(variant => !parse.GetValue(pending) || variant.IsHashesPending)
                .OrderBy(variant => variant.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(Describe)
                .ToList();

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(rows);
            }

            if (rows.Count == 0)
            {
                Console.Out.WriteLine("No characters match.");
                return 0;
            }

            foreach (var row in rows)
            {
                var badges = new List<string>();

                if (row.Origin != "pack")
                {
                    badges.Add(row.Origin);
                }

                if (row.HashesPending)
                {
                    badges.Add("hashes pending");
                }

                if (row.Hidden)
                {
                    badges.Add("hidden");
                }

                if (row.BaseCharacterId is { Length: > 0 } baseId)
                {
                    badges.Add($"skin of {baseId}");
                }

                Console.Out.WriteLine(
                    $"{row.DisplayName}  ({row.InternalName})  " +
                    $"{EnglishCount.Plural(row.HashCount, "hash", "hashes")}" +
                    (badges.Count > 0 ? $"  [{string.Join(", ", badges)}]" : string.Empty));
            }

            Console.Out.WriteLine();
            Console.Out.WriteLine($"{EnglishCount.Plural(rows.Count, "character", "characters")}.");

            return 0;
        });

        return command;
    }

    private static Command CreateShow()
    {
        var name = new Argument<string>("name") { Description = "The character's internal name." };
        var game = GameOption();
        var pack = PackOption();

        var command = new Command("show", "Show everything about one character.") { name, game, pack };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var data = await LoadAsync(provider, parse.GetValue(game)!, parse.GetValue(pack), cancellationToken)
                .ConfigureAwait(false);

            var variant = data.Find(parse.GetValue(name)!);

            if (variant is null)
            {
                CliOutput.WriteError($"There is no character called '{parse.GetValue(name)}'.");
                return 1;
            }

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(Describe(variant));
            }

            CliOutput.WriteRows(
            [
                ("Name", variant.DisplayName),
                ("Internal name", variant.InternalName),
                ("Skin of", variant.BaseCharacterId ?? "—"),
                ("Family", variant.FamilyId),
                ("Default of its family", variant.IsDefaultVariant ? "yes" : "no"),
                ("Mods folder", variant.ModFilesName),
                ("Portrait", variant.Image ?? "— (initials are drawn instead)"),
                ("Aliases", variant.Aliases.Count == 0 ? "—" : string.Join(", ", variant.Aliases)),
                ("Hashes", variant.Hashes.Count.ToString(CultureInfo.InvariantCulture)),
                ("Origin", Origin(variant.Origin)),
                ("Locked", Locked(variant)),
                ("Hidden", variant.Hidden ? "yes" : "no"),
                ("Notes", variant.Notes ?? "—"),
            ]);

            if (variant.Hashes.Count > 0)
            {
                Console.Out.WriteLine();

                foreach (var group in variant.Hashes.GroupBy(hash => hash.Kind).OrderBy(g => g.Key))
                {
                    Console.Out.WriteLine(
                        $"  {CliOutput.Camel(group.Key.ToString())}: {string.Join(", ", group.Select(h => h.Hash))}");
                }
            }

            return 0;
        });

        return command;
    }

    // Writing

    private static Command CreateCreate()
    {
        var name = new Argument<string>("name")
        {
            Description = "The character's name. The only thing that is required.",
        };

        var game = GameOption();
        var pack = PackOption();

        var internalName = new Option<string?>("--internal-name")
        {
            Description = "Override the id derived from the name.",
        };

        var baseCharacter = new Option<string?>("--skin-of")
        {
            Description = "Make this an alternate outfit of another character.",
        };

        var modFilesName = new Option<string?>("--mod-files-name")
        {
            Description = "Override the Mods sub-folder name. Defaults to the internal name.",
        };

        var image = new Option<string?>("--image")
        {
            Description = "A portrait: a picture file, which is copied into XXSM's data folder, or an https:// URL.",
        };

        var aliases = new Option<string[]>("--alias")
        {
            Description = "An extra name the sorter should accept. May be given more than once.",
            AllowMultipleArgumentsPerToken = true,
        };

        var notes = new Option<string?>("--notes") { Description = "Free-text notes." };

        var hidden = new Option<bool>("--hidden")
        {
            Description = "Keep it out of the grid from the start. Mods still sort under it.",
        };

        var command = new Command("create", "Create a character. Only a name is required.")
        {
            name, game, pack, internalName, baseCharacter, modFilesName, image, aliases, notes, hidden,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var data = await LoadAsync(provider, parse.GetValue(game)!, parse.GetValue(pack), cancellationToken)
                .ConfigureAwait(false);

            var result = await provider.GetRequiredService<ICharacterEditor>()
                .CreateAsync(
                    data,
                    new NewCharacter
                    {
                        DisplayName = parse.GetValue(name)!,
                        InternalName = parse.GetValue(internalName),
                        BaseCharacterId = parse.GetValue(baseCharacter),
                        ModFilesName = parse.GetValue(modFilesName),
                        Image = parse.GetValue(image) is { Length: > 0 } picture
                            ? await PortraitAsync(provider, data.GameId, picture, cancellationToken).ConfigureAwait(false)
                            : null,
                        Aliases = parse.GetValue(aliases),
                        Notes = parse.GetValue(notes),
                        Hidden = parse.GetValue(hidden),
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            return Report(parse, result, "Created");
        });

        return command;
    }

    private static Command CreateEdit()
    {
        var name = new Argument<string>("name") { Description = "The character's internal name." };
        var game = GameOption();
        var pack = PackOption();

        var displayName = new Option<string?>("--name") { Description = "The name shown in the app." };
        var modFilesName = new Option<string?>("--mod-files-name") { Description = "The Mods sub-folder name." };
        var image = new Option<string?>("--image")
        {
            Description = "A portrait: a picture file, which is copied into XXSM's data folder, or an https:// URL.",
        };
        var notes = new Option<string?>("--notes") { Description = "Free-text notes." };

        var baseCharacter = new Option<string?>("--skin-of")
        {
            Description = "Make this an alternate outfit of another character.",
        };

        var detach = new Option<bool>("--detach")
        {
            Description = "Stop this being an outfit of anything, making it a base character.",
        };

        var clearImage = new Option<bool>("--clear-image")
        {
            Description = "Remove the portrait, so initials are drawn instead.",
        };

        var aliases = new Option<string[]>("--alias")
        {
            Description = "Replace the aliases the sorter accepts. Give none to clear them.",
            AllowMultipleArgumentsPerToken = true,
        };

        var hide = new Option<bool?>("--hidden")
        {
            Description = "Hide this character from the grid, or show it again with --hidden false.",
        };

        var reset = new Option<string[]>("--reset")
        {
            Description =
                "Put a field back to the pack's value. Give no field to reset the whole "
                + "character. May be given more than once.",
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.ZeroOrMore,
        };

        var resetAll = new Option<bool>("--reset-all")
        {
            Description = "Discard every edit to this character and go back to the pack's version.",
        };

        var unlock = new Option<bool>("--unlock")
        {
            Description = "Keep your values but let future pack updates change them again.",
        };

        var keepLocked = new Option<string[]>("--keep-locked")
        {
            Description = "With --unlock, the fields to keep protected. May be given more than once.",
            AllowMultipleArgumentsPerToken = true,
        };

        var command = new Command("edit", "Edit a character. Editing a pack character locks it.")
        {
            name, game, pack, displayName, modFilesName, image, notes, baseCharacter,
            detach, clearImage, aliases, hide, reset, resetAll, unlock, keepLocked,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var editor = provider.GetRequiredService<ICharacterEditor>();
            var id = parse.GetValue(name)!;

            var data = await LoadAsync(provider, parse.GetValue(game)!, parse.GetValue(pack), cancellationToken)
                .ConfigureAwait(false);

            if (parse.GetValue(resetAll))
            {
                var reverted = await editor.ResetAsync(data, id, fields: null, cancellationToken)
                    .ConfigureAwait(false);

                return Report(parse, reverted, "Reset");
            }

            if (parse.GetValue(reset) is { Length: > 0 } fields)
            {
                var partial = await editor.ResetAsync(data, id, fields, cancellationToken).ConfigureAwait(false);
                return Report(parse, partial, "Reset");
            }

            if (parse.GetValue(unlock))
            {
                var unlocked = await editor
                    .UnlockAsync(data, id, parse.GetValue(keepLocked), cancellationToken)
                    .ConfigureAwait(false);

                return Report(parse, unlocked, "Unlocked");
            }

            var edit = new CharacterEdit
            {
                DisplayName = Field(parse, displayName),
                ModFilesName = Field(parse, modFilesName),
                Notes = Field(parse, notes),
                Image = parse.GetValue(clearImage)
                    ? EditField<string>.Cleared()
                    : parse.GetValue(image) is { Length: > 0 } picture
                        ? EditField<string>.To(
                            await PortraitAsync(provider, data.GameId, picture, cancellationToken).ConfigureAwait(false))
                        : Field(parse, image),
                BaseCharacterId = parse.GetValue(detach)
                    ? EditField<string>.Cleared()
                    : Field(parse, baseCharacter),
                Aliases = WasGiven(parse, aliases)
                    ? EditField<IReadOnlyList<string>>.To(parse.GetValue(aliases) ?? [])
                    : EditField<IReadOnlyList<string>>.Unchanged,
                Hidden = parse.GetValue(hide) is { } hiddenValue
                    ? EditField<bool>.To(hiddenValue)
                    : EditField<bool>.Unchanged,
            };

            if (edit.IsEmpty)
            {
                CliOutput.WriteError(
                    "Nothing to change. Pass one of the options above, or --reset to go back to the pack.");

                return 1;
            }

            var result = await editor.EditAsync(data, id, edit, cancellationToken).ConfigureAwait(false);
            return Report(parse, result, "Edited");
        });

        return command;
    }

    private static Command CreateDelete()
    {
        var name = new Argument<string>("name") { Description = "The custom character's internal name." };
        var game = GameOption();
        var pack = PackOption();
        var mods = ModsFolderOptions.Mods();

        var rehome = new Option<string?>("--rehome-to")
        {
            Description = "Where its mods should go. Defaults to Others/.",
        };

        var command = new Command("delete", "Delete a character you created, re-homing its mods first.")
        {
            name, game, pack, mods, rehome,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var modsDirectory = await ModsFolderOptions
                .ResolveAsync(provider, parse, mods, parse.GetValue(game)!, cancellationToken)
                .ConfigureAwait(false);

            var data = await LoadAsync(provider, parse.GetValue(game)!, parse.GetValue(pack), cancellationToken)
                .ConfigureAwait(false);

            var result = await provider.GetRequiredService<ICharacterEditor>()
                .DeleteAsync(data, parse.GetValue(name)!, modsDirectory, parse.GetValue(rehome), cancellationToken)
                .ConfigureAwait(false);

            var report = new CharacterDeleteReport(
                result.InternalName,
                result.Changed,
                result.RehomedTo,
                result.RehomedMods,
                [.. result.Diagnostics.Select(Message)],
                result.RestoreRecordPath);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report);
            }

            Console.Out.WriteLine(
                result.Changed
                    ? $"Deleted {result.InternalName}."
                    : $"{result.InternalName} was not in your overlay; nothing to delete.");

            if (result.RehomedTo is { Length: > 0 } destination)
            {
                Console.Out.WriteLine(
                    $"Moved {EnglishCount.Plural(result.RehomedMods.Count, "mod", "mods")} to {destination}/:");

                foreach (var mod in result.RehomedMods)
                {
                    Console.Out.WriteLine($"  {mod}");
                }
            }

            WriteNotes(result.Diagnostics);

            if (result.RestoreRecordPath is { Length: > 0 } record)
            {
                Console.Out.WriteLine($"To undo: xxsm character restore \"{record}\"");
            }

            return 0;
        });

        return command;
    }

    private static Command CreateRestore()
    {
        var record = new Argument<string>("record")
        {
            Description = "The record `xxsm character delete` printed.",
        };

        var pack = PackOption();

        var command = new Command(
            "restore", "Bring a deleted character back and move its mods home. Never overwrites anything.")
        {
            record, pack,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var editor = provider.GetRequiredService<ICharacterEditor>();
            var path = parse.GetValue(record)!;
            var deletion = await editor.ReadDeletionAsync(path, cancellationToken).ConfigureAwait(false);

            var data = await LoadAsync(provider, deletion.GameId, parse.GetValue(pack), cancellationToken)
                .ConfigureAwait(false);

            var result = await editor.RestoreDeletedAsync(data, deletion, path, cancellationToken).ConfigureAwait(false);

            var report = new CharacterRestoreReport(
                result.InternalName,
                result.DisplayName,
                result.IsComplete,
                result.RestoredMods,
                [.. result.Skipped.Select(skip => new CharacterRestoreSkipReport(skip.Path, skip.Reason))],
                [.. result.Diagnostics.Select(Message)]);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report, result.IsComplete ? 0 : 1);
            }

            Console.Out.WriteLine(
                result.IsComplete
                    ? $"{result.DisplayName} is back."
                    : $"{result.DisplayName} is back, but not every mod could be moved back.");

            if (result.RestoredMods.Count > 0)
            {
                Console.Out.WriteLine(
                    $"Moved {EnglishCount.Plural(result.RestoredMods.Count, "mod", "mods")} back:");

                foreach (var mod in result.RestoredMods)
                {
                    Console.Out.WriteLine($"  {mod}");
                }
            }

            foreach (var skip in result.Skipped)
            {
                CliOutput.WriteError($"{PathDisplay.Show(skip.Path)}: {skip.Reason}");
            }

            if (!result.IsComplete)
            {
                Console.Out.WriteLine(
                    $"Put them back where they were and run it again: xxsm character restore \"{PathDisplay.Show(path)}\"");
            }

            WriteNotes(result.Diagnostics);
            return result.IsComplete ? 0 : 1;
        });

        return command;
    }

    // Hashes

    private static Command CreateHashes() =>
        new("hashes", "Read, add and remove a character's hashes.")
        {
            CreateHashesParse(),
            CreateHashesAdd(),
            CreateHashesRemove(),
        };

    private static Command CreateHashesParse()
    {
        var file = new Argument<string?>("file")
        {
            Description = "A file to read. Omit it to read standard input.",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var command = new Command(
            "parse", "Show what XXSM makes of pasted hash text, without saving anything.") { file };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var (text, source) = await ReadTextAsync(parse.GetValue(file), cancellationToken).ConfigureAwait(false);
            var result = HashPaste.Parse(text, source);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(Describe(result, learned: null));
            }

            WriteHashes(result);
            return result.IsEmpty && result.Rejected.Count > 0 ? 1 : 0;
        });

        return command;
    }

    private static Command CreateHashesAdd()
    {
        var name = new Argument<string>("name") { Description = "The character's internal name." };
        var game = GameOption();
        var pack = PackOption();

        var file = new Option<string?>("--file")
        {
            Description = "Read the hash text from a file instead of standard input.",
        };

        var command = new Command("add", "Add hashes to a character from pasted text or a file.")
        {
            name, game, pack, file,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var (text, source) = await ReadTextAsync(parse.GetValue(file), cancellationToken).ConfigureAwait(false);
            var parsed = HashPaste.Parse(text, source);

            if (parsed.IsEmpty)
            {
                CliOutput.WriteError("No hashes were understood. Try: xxsm character hashes parse");
                WriteHashes(parsed);
                return 1;
            }

            var data = await LoadAsync(provider, parse.GetValue(game)!, parse.GetValue(pack), cancellationToken)
                .ConfigureAwait(false);

            var result = await provider.GetRequiredService<ICharacterEditor>()
                .AddHashesAsync(data, parse.GetValue(name)!, parsed.Entries, cancellationToken)
                .ConfigureAwait(false);

            return Report(parse, result, "Added hashes to");
        });

        return command;
    }

    private static Command CreateHashesRemove()
    {
        var name = new Argument<string>("name") { Description = "The character's internal name." };

        var hashes = new Argument<string[]>("hashes")
        {
            Description = "The hashes to remove.",
            Arity = ArgumentArity.OneOrMore,
        };

        var game = GameOption();
        var pack = PackOption();

        var command = new Command("remove", "Remove hashes from a character.") { name, hashes, game, pack };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var data = await LoadAsync(provider, parse.GetValue(game)!, parse.GetValue(pack), cancellationToken)
                .ConfigureAwait(false);

            var result = await provider.GetRequiredService<ICharacterEditor>()
                .RemoveHashesAsync(data, parse.GetValue(name)!, parse.GetValue(hashes)!, cancellationToken)
                .ConfigureAwait(false);

            return Report(parse, result, "Removed hashes from");
        });

        return command;
    }

    private static Command CreateLearn()
    {
        var folder = new Argument<string>("folder") { Description = "The mod folder to read." };
        var game = GameOption();
        var pack = PackOption();

        var addTo = new Option<string?>("--add-to")
        {
            Description = "Record what was found against this character, instead of only reporting it.",
        };

        var createAs = new Option<string?>("--create")
        {
            Description = "Create a character with this name and record what was found against it.",
        };

        var command = new Command("learn", "Read every hash in a mod folder, and say which character it names.")
        {
            folder, game, pack, addTo, createAs,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var data = await LoadAsync(provider, parse.GetValue(game)!, parse.GetValue(pack), cancellationToken)
                .ConfigureAwait(false);

            var learned = await provider.GetRequiredService<IModHashLearner>()
                .LearnAsync(parse.GetValue(folder)!, data, default, null, cancellationToken)
                .ConfigureAwait(false);

            var editor = provider.GetRequiredService<ICharacterEditor>();
            CharacterEditResult? written = null;

            if (parse.GetValue(createAs) is { Length: > 0 } newName)
            {
                written = await editor
                    .CreateAsync(
                        data,
                        new NewCharacter { DisplayName = newName, Hashes = Entries(learned) },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (parse.GetValue(addTo) is { Length: > 0 } existing)
            {
                written = await editor
                    .AddHashesAsync(data, existing, Entries(learned), cancellationToken)
                    .ConfigureAwait(false);
            }

            if (parse.GetValue(GlobalOptions.Json))
            {
                Console.Out.WriteLine(CliJson.Serialize(new CharacterLearnReport(
                    Describe(
                        new HashPasteResult
                        {
                            Hashes = learned.Hashes,
                            Rejected = [],
                            Shape = HashPasteShape.Ini,
                            IgnoredLines = 0,
                        },
                        learned),
                    written is null ? null : Describe(written))));

                return 0;
            }

            Console.Out.WriteLine(
                $"{EnglishCount.Plural(learned.Hashes.Count, "hash", "hashes")} found in " +
                $"{EnglishCount.Plural(learned.Files.Count, "file", "files")}.");

            foreach (var (kind, count) in CountByKind(learned.Hashes))
            {
                Console.Out.WriteLine(
                    $"  {CliOutput.Camel(kind.ToString())}: {count.ToString(CultureInfo.InvariantCulture)}");
            }

            Console.Out.WriteLine();
            Console.Out.WriteLine(
                learned.WasMatched
                    ? $"These name {data.Find(learned.MatchedVariantId!)?.DisplayName ?? learned.MatchedVariantId}."
                    : "These name no character XXSM knows about.");

            Console.Out.WriteLine($"  {learned.Decision.Explanation}");

            if (!learned.WasMatched)
            {
                Console.Out.WriteLine();
                Console.Out.WriteLine(
                    $"To make a character from it:  xxsm character learn '{parse.GetValue(folder)}' " +
                    $"--game {parse.GetValue(game)} --create '{learned.SuggestedName}'");
            }

            if (written is not null)
            {
                Console.Out.WriteLine();
                Report(parse, written, parse.GetValue(createAs) is { Length: > 0 } ? "Created" : "Added hashes to");
            }

            WriteNotes(learned.Diagnostics);
            return 0;
        });

        return command;
    }

    // Shared

    /// <summary>A portrait given on the command line: a web address as it is, a file copied in.</summary>
    private static async Task<string> PortraitAsync(
        ServiceProvider provider, string gameId, string given, CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(given, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            return given;
        }

        var path = uri is { IsFile: true } ? uri.LocalPath : Path.GetFullPath(given);

        return await provider.GetRequiredService<ICharacterPortraitStore>()
            .StoreAsync(gameId, PreviewImageSource.FromFile(path), cancellationToken)
            .ConfigureAwait(false);
    }

    private static EditField<string> Field(ParseResult parse, Option<string?> option) =>
        WasGiven(parse, option) ? EditField<string>.To(parse.GetValue(option)) : EditField<string>.Unchanged;

    /// <summary>Whether an option appeared on the command line at all, even as an empty string.</summary>
    private static bool WasGiven(ParseResult parse, Option option) =>
        parse.GetResult(option) is { Implicit: false };

    private static IReadOnlyList<PackHashEntry> Entries(LearnedFromMod learned) =>
        [.. learned.Hashes.Select(hash => hash.Entry)];

    private static IReadOnlyList<(HashKind Kind, int Count)> CountByKind(IReadOnlyList<ParsedHash> hashes) =>
    [
        .. hashes
            .GroupBy(hash => hash.Entry.Kind)
            .Select(group => (Kind: group.Key, Count: group.Count()))
            .OrderByDescending(pair => pair.Count)
            .ThenBy(pair => pair.Kind),
    ];

    private static async Task<(string Text, string Source)> ReadTextAsync(
        string? file, CancellationToken cancellationToken)
    {
        if (file is { Length: > 0 })
        {
            return (await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false),
                Path.GetFileName(file));
        }

        return (await Console.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false), "standard input");
    }

    private static int Report(ParseResult parse, CharacterEditResult result, string verb)
    {
        if (parse.GetValue(GlobalOptions.Json))
        {
            return CliJson.Write(Describe(result));
        }

        Console.Out.WriteLine(
            result.Changed
                ? $"{verb} {result.DisplayName} ({result.InternalName}), now {Origin(result.Origin)}, " +
                  $"{EnglishCount.Plural(result.HashCount, "hash", "hashes")}."
                : $"Nothing changed for {result.DisplayName} ({result.InternalName}).");

        WriteNotes(result.Diagnostics);
        return 0;
    }

    private static void WriteNotes(IReadOnlyList<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            Console.Out.WriteLine($"  note: {diagnostic.Message}");
        }
    }

    private static void WriteHashes(HashPasteResult result)
    {
        Console.Out.WriteLine(
            $"Read as {CliOutput.Camel(result.Shape.ToString())}: " +
            $"{EnglishCount.Plural(result.Hashes.Count, "hash", "hashes")}, " +
            $"{EnglishCount.Plural(result.IgnoredLines, "other line", "other lines")} ignored.");

        foreach (var (kind, count) in result.CountByKind())
        {
            Console.Out.WriteLine(
                $"  {CliOutput.Camel(kind.ToString())}: {count.ToString(CultureInfo.InvariantCulture)}");
        }

        if (result.Hashes.Count > 0)
        {
            Console.Out.WriteLine();

            foreach (var hash in result.Hashes)
            {
                Console.Out.WriteLine(
                    $"  {hash.Entry.Hash}  {CliOutput.Camel(hash.Entry.Kind.ToString())}" +
                    $"{(hash.KindWasInferred ? " (guessed)" : string.Empty)}  {hash.Evidence}");
            }
        }

        if (result.Rejected.Count > 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("Not kept:");

            foreach (var rejection in result.Rejected)
            {
                var where = rejection.Line is { } line
                    ? $"line {line.ToString(CultureInfo.InvariantCulture)}: "
                    : string.Empty;

                Console.Out.WriteLine($"  {where}{rejection.Text} — {rejection.Reason}");
            }
        }
    }

    private static CharacterReport Describe(MergedVariant variant) => new(
        variant.InternalName,
        variant.DisplayName,
        variant.BaseCharacterId,
        variant.IsDefaultVariant,
        variant.ModFilesName,
        Origin(variant.Origin),
        variant.IsLocked,
        variant.LockedFields,
        variant.Hashes.Count,
        variant.IsHashesPending,
        variant.Hidden,
        variant.Aliases);

    private static CharacterEditReport Describe(CharacterEditResult result) => new(
        result.InternalName,
        result.DisplayName,
        Origin(result.Origin),
        result.Changed,
        result.RequestedInternalName,
        result.HashCount,
        [.. result.Diagnostics.Select(Message)]);

    private static HashReadReport Describe(HashPasteResult result, LearnedFromMod? learned) => new(
        CliOutput.Camel(result.Shape.ToString()),
        [
            .. result.Hashes.Select(hash => new ParsedHashReport(
                hash.Entry.Hash,
                CliOutput.Camel(hash.Entry.Kind.ToString()),
                hash.KindWasInferred,
                hash.Entry.Component,
                hash.Evidence)),
        ],
        [.. result.CountByKind().Select(pair => new KindCountReport(CliOutput.Camel(pair.Kind.ToString()), pair.Count))],
        result.Rejected,
        result.IgnoredLines,
        learned?.MatchedVariantId,
        learned is null ? null : CliOutput.Camel(learned.Decision.DecidedBy.ToString()),
        learned?.Decision.Confidence ?? 0,
        learned?.SuggestedName,
        learned?.Decision.Explanation);

    private static string Message(Diagnostic diagnostic) => diagnostic.Message;

    private static string Origin(VariantOrigin origin) => origin switch
    {
        VariantOrigin.Modified => "modified",
        VariantOrigin.Custom => "custom",
        _ => "pack",
    };

    private static string Locked(MergedVariant variant) => variant.IsLocked
        ? "yes — pack updates will not change it"
        : variant.LockedFields.Count > 0
            ? $"only {string.Join(", ", variant.LockedFields)}"
            : "no";
}
