using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Xxsm.Cli.Output;
using Xxsm.Core;
using Xxsm.Core.Text;
using Xxsm.Packs.Characters;
using Xxsm.Packs.Hashes;
using Xxsm.Packs.Model;
using Xxsm.Packs.Studio;

namespace Xxsm.Cli.Commands;

/// <summary>The edits Pack Studio's table and editor make, one command each, saved at once.</summary>
internal static class StudioEditCommands
{
    /// <summary>Builds <c>xxsm studio character</c>.</summary>
    public static Command CreateCharacter() =>
        new("character", "Add, change, link and delete a draft's characters.")
        {
            CreateAdd(),
            CreateSet(),
            CreateLink(),
            CreateDefault(),
            CreateRename(),
            CreateDelete(),
            CreatePicture(),
            CreateHashes(),
        };

    private static Argument<string[]> Names() => new("characters")
    {
        Description = "The characters' internal names.",
        Arity = ArgumentArity.OneOrMore,
    };

    /// <summary>Loads a draft, applies one edit, saves it, and reports the change.</summary>
    internal static async Task<int> EditAsync(
        ParseResult parse,
        string gameId,
        Func<ServiceProvider, PackDraft, CancellationToken, Task<(PackDraft Draft, IReadOnlyList<string> Changed, IReadOnlyList<string> Notes)>> edit,
        CancellationToken cancellationToken)
    {
        await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
        var draft = await StudioCommand.LoadAsync(provider, gameId, cancellationToken).ConfigureAwait(false);
        var (changed, names, notes) = await edit(provider, draft, cancellationToken).ConfigureAwait(false);

        await StudioCommand.SaveAsync(provider, changed, cancellationToken).ConfigureAwait(false);

        if (parse.GetValue(GlobalOptions.Json))
        {
            return CliJson.Write(new StudioEditReport(draft.GameId, names, notes));
        }

        Console.Out.WriteLine(names.Count == 0 ? "Nothing changed." : $"Changed: {string.Join(", ", names)}");

        foreach (var note in notes)
        {
            Console.Out.WriteLine($"  {note}");
        }

        return 0;
    }

    private static Command CreateAdd()
    {
        var game = StudioCommand.GameArgument();
        var name = new Argument<string>("name") { Description = "The character's name. The only thing that is required." };
        var internalName = new Option<string?>("--internal-name") { Description = "The id to use instead of one made from the name." };
        var skinOf = new Option<string?>("--skin-of") { Description = "Make it an outfit of this character." };
        var aliases = new Option<string[]>("--alias") { Description = "Another name for it. May be given more than once.", AllowMultipleArgumentsPerToken = true };
        var notes = new Option<string?>("--notes") { Description = "Free-text notes." };

        var command = new Command("add", "Add a character to a draft. Only a name is required.")
        {
            game, name, internalName, skinOf, aliases, notes,
        };

        command.SetAction((parse, cancellationToken) => EditAsync(parse, parse.GetValue(game)!, (_, draft, _) =>
        {
            var added = DraftEdits.AddCharacter(draft, new NewCharacter
            {
                DisplayName = parse.GetValue(name)!,
                InternalName = parse.GetValue(internalName),
                BaseCharacterId = parse.GetValue(skinOf),
                Aliases = parse.GetValue(aliases),
                Notes = parse.GetValue(notes),
            });

            IReadOnlyList<string> changed = [added.InternalName];
            IReadOnlyList<string> said = added.RequestedInternalName is { } wanted
                ? [$"'{wanted}' was taken, so it is '{added.InternalName}'."]
                : [];

            return Task.FromResult((added.Draft, changed, said));
        }, cancellationToken));

        return command;
    }

    private static Command CreateSet()
    {
        var game = StudioCommand.GameArgument();
        var names = Names();
        var displayName = new Option<string?>("--name") { Description = "A new name. Only with one character." };
        var attributes = new Option<string[]>("--attribute")
        {
            Description = "Set an attribute as ID=VALUE; separate several values with ',' or ';', or leave VALUE empty to clear it. May be given more than once.",
            AllowMultipleArgumentsPerToken = true,
        };
        var aliasPrefix = new Option<string?>("--alias-prefix") { Description = "Add an alias: this, then each character's name." };
        var aliasSuffix = new Option<string?>("--alias-suffix") { Description = "Add an alias: each character's name, then this." };
        var hidden = new Option<bool?>("--hidden") { Description = "Hide them from the grid, or show them with --hidden false." };
        var modFilesName = new Option<string?>("--mod-files-name") { Description = "The folder mods are filed in. Only with one character." };
        var notes = new Option<string?>("--notes") { Description = "Free-text notes." };

        var command = new Command("set", "Change characters' details. Several characters at once, like the table's bulk actions.")
        {
            game, names, displayName, attributes, aliasPrefix, aliasSuffix, hidden, modFilesName, notes,
        };

        command.SetAction((parse, cancellationToken) => EditAsync(parse, parse.GetValue(game)!, (_, draft, _) =>
        {
            var ids = parse.GetValue(names)!;

            if ((parse.GetValue(displayName) is not null || parse.GetValue(modFilesName) is not null) && ids.Length != 1)
            {
                throw new ModOperationException("--name and --mod-files-name change one character at a time.");
            }

            var edit = new CharacterEdit
            {
                DisplayName = parse.GetValue(displayName) is { } name ? EditField<string>.To(name) : default,
                ModFilesName = parse.GetValue(modFilesName) is { } folder ? EditField<string>.To(folder) : default,
                Notes = parse.GetValue(notes) is { } text ? EditField<string>.To(text) : default,
                Hidden = parse.GetValue(hidden) is { } hide ? EditField<bool>.To(hide) : default,
            };

            var notesSaid = new List<string>();

            if (!edit.IsEmpty)
            {
                foreach (var id in ids)
                {
                    var result = DraftEdits.EditCharacter(draft, id, edit);
                    draft = result.Draft;
                    notesSaid.AddRange(result.Notes);
                }
            }

            foreach (var assignment in parse.GetValue(attributes) ?? [])
            {
                var parts = assignment.Split('=', 2);

                if (parts.Length != 2)
                {
                    throw new ModOperationException($"'{assignment}' is not ID=VALUE.");
                }

                draft = DraftEdits.SetAttribute(draft, ids, parts[0].Trim(), DraftEdits.ParseAttributeValue(draft, parts[0].Trim(), parts[1]));
            }

            if (parse.GetValue(aliasPrefix) is not null || parse.GetValue(aliasSuffix) is not null)
            {
                draft = DraftEdits.AddAlias(draft, ids, parse.GetValue(aliasPrefix), parse.GetValue(aliasSuffix));
            }

            return Task.FromResult<(PackDraft, IReadOnlyList<string>, IReadOnlyList<string>)>((draft, ids, notesSaid));
        }, cancellationToken));

        return command;
    }

    private static Command CreateLink()
    {
        var game = StudioCommand.GameArgument();
        var names = Names();
        var skinOf = new Option<string?>("--skin-of") { Description = "Make them outfits of this character." };
        var asBase = new Option<bool>("--base") { Description = "Make them characters in their own right, not outfits of anyone." };

        var command = new Command("link", "Make characters outfits of another, or undo a wrong link with --base.")
        {
            game, names, skinOf, asBase,
        };

        command.SetAction((parse, cancellationToken) =>
        {
            if (parse.GetValue(asBase) == parse.GetValue(skinOf) is { Length: > 0 })
            {
                CliOutput.WriteError("Say either --skin-of <character> or --base.");
                return Task.FromResult(1);
            }

            return EditAsync(parse, parse.GetValue(game)!, (_, draft, _) =>
            {
                var result = DraftEdits.SetBase(draft, parse.GetValue(names)!, parse.GetValue(asBase) ? null : parse.GetValue(skinOf));
                return Task.FromResult<(PackDraft, IReadOnlyList<string>, IReadOnlyList<string>)>((result.Draft, parse.GetValue(names)!, result.Notes));
            }, cancellationToken);
        });

        return command;
    }

    private static Command CreateDefault()
    {
        var game = StudioCommand.GameArgument();
        var name = new Argument<string>("character") { Description = "The outfit to make its family's default." };

        var command = new Command("default", "Make one outfit its family's default, used when a mod cannot be told apart.") { game, name };

        command.SetAction((parse, cancellationToken) => EditAsync(parse, parse.GetValue(game)!, (_, draft, _) =>
            Task.FromResult<(PackDraft, IReadOnlyList<string>, IReadOnlyList<string>)>(
                (DraftEdits.SetDefault(draft, parse.GetValue(name)!), [parse.GetValue(name)!], [])),
            cancellationToken));

        return command;
    }

    private static Command CreateRename()
    {
        var game = StudioCommand.GameArgument();
        var from = new Argument<string>("character") { Description = "The character's internal name now." };
        var to = new Argument<string>("new-internal-name") { Description = "Its new internal name." };

        var command = new Command("rename", "Give a character a different internal name, keeping its hashes and outfits.") { game, from, to };

        command.SetAction((parse, cancellationToken) => EditAsync(parse, parse.GetValue(game)!, (_, draft, _) =>
            Task.FromResult<(PackDraft, IReadOnlyList<string>, IReadOnlyList<string>)>(
                (DraftEdits.RenameCharacter(draft, parse.GetValue(from)!, parse.GetValue(to)!), [parse.GetValue(to)!], [])),
            cancellationToken));

        return command;
    }

    private static Command CreateDelete()
    {
        var game = StudioCommand.GameArgument();
        var names = Names();

        var command = new Command("delete", "Delete characters from the draft. Their outfits become characters in their own right.") { game, names };

        command.SetAction((parse, cancellationToken) => EditAsync(parse, parse.GetValue(game)!, (_, draft, _) =>
        {
            var result = DraftEdits.DeleteCharacters(draft, parse.GetValue(names)!);
            IReadOnlyList<string> deleted = [.. draft.Variants.Select(v => v.InternalName).Except(result.Draft.Variants.Select(v => v.InternalName), StringComparer.Ordinal)];
            return Task.FromResult((result.Draft, deleted, result.Notes));
        }, cancellationToken));

        return command;
    }

    private static Command CreatePicture()
    {
        var game = StudioCommand.GameArgument();
        var name = new Argument<string>("character") { Description = "The character." };
        var file = new Argument<string>("file") { Description = "A PNG, JPEG or WebP picture. It is copied into the draft." };

        var command = new Command("picture", "Give a character a portrait from a picture file.") { game, name, file };

        command.SetAction((parse, cancellationToken) => EditAsync(parse, parse.GetValue(game)!, async (provider, draft, token) =>
        {
            var id = draft.Variants.FirstOrDefault(v => string.Equals(v.InternalName, parse.GetValue(name), StringComparison.OrdinalIgnoreCase))?.InternalName
                     ?? throw new ModOperationException($"There is no character called '{parse.GetValue(name)}' in this draft.");

            var image = await provider.GetRequiredService<IStudioDraftStore>()
                .StoreImageAsync(draft.GameId, id, Path.GetFullPath(parse.GetValue(file)!), token)
                .ConfigureAwait(false);

            var result = DraftEdits.EditCharacter(draft, id, new CharacterEdit { Image = EditField<string>.To(image) });
            return (result.Draft, (IReadOnlyList<string>)[id], result.Notes);
        }, cancellationToken));

        return command;
    }

    private static Command CreateHashes()
    {
        var game = StudioCommand.GameArgument();
        var name = new Argument<string>("character") { Description = "The character." };
        var paste = new Option<bool>("--paste")
        {
            Description = "Add the hashes in text read from standard input: an INI section, a hash.json, or one hash per line.",
        };
        var replace = new Option<bool>("--replace")
        {
            Description = "With --paste: the pasted hashes become the character's whole list, and any others are removed.",
        };
        var remove = new Option<string[]>("--remove") { Description = "A hash to remove. May be given more than once.", AllowMultipleArgumentsPerToken = true };

        var command = new Command("hashes", "Add hashes to a character from pasted text, replace them all, or remove some.")
        {
            game, name, paste, replace, remove,
        };

        command.SetAction((parse, cancellationToken) => EditAsync(parse, parse.GetValue(game)!, async (_, draft, token) =>
        {
            var id = parse.GetValue(name)!;
            var notes = new List<string>();

            if (parse.GetValue(replace) && !parse.GetValue(paste))
            {
                throw new ModOperationException("--replace needs --paste: the pasted hashes are what replaces the list.");
            }

            if (parse.GetValue(paste))
            {
                var text = await Console.In.ReadToEndAsync(token).ConfigureAwait(false);
                var parsed = HashPaste.Parse(text);

                if (parse.GetValue(replace))
                {
                    var replaced = DraftEdits.ReplaceHashes(draft, id, parsed.Entries);
                    draft = replaced.Draft;
                    notes.Add(
                        $"Added {EnglishCount.Plural(replaced.Added, "hash", "hashes")} and removed " +
                        $"{EnglishCount.Plural(replaced.Removed, "hash", "hashes")}.");
                }
                else
                {
                    var added = DraftEdits.AddHashes(draft, id, parsed.Entries);
                    draft = added.Draft;
                    notes.Add($"Added {EnglishCount.Plural(added.Count, "hash", "hashes")}.");
                }

                notes.AddRange(parsed.Rejected.Select(r => $"Not a hash: {r.Text} — {r.Reason}"));
            }

            if (parse.GetValue(remove) is { Length: > 0 } hashes)
            {
                var removed = DraftEdits.RemoveHashes(draft, id, hashes);
                draft = removed.Draft;
                notes.Add($"Removed {EnglishCount.Plural(removed.Count, "hash", "hashes")}.");
            }

            return (draft, (IReadOnlyList<string>)[id], (IReadOnlyList<string>)notes);
        }, cancellationToken));

        return command;
    }

    /// <summary>Builds <c>xxsm studio attribute</c> — the attribute editor.</summary>
    public static Command CreateAttribute()
    {
        var setGame = StudioCommand.GameArgument();
        var setId = new Argument<string>("attribute") { Description = "The attribute's id, such as element." };
        var label = new Option<string?>("--label") { Description = "The name shown on its filter chip." };
        var number = new Option<bool>("--number") { Description = "It holds a number, such as a rarity." };
        var values = new Option<string[]>("--value")
        {
            Description = "One value it can have, as ID or ID=Label. May be given more than once.",
            AllowMultipleArgumentsPerToken = true,
        };

        var set = new Command("set", "Add an attribute to the game, or replace one. The grid's filter chips come from these.")
        {
            setGame, setId, label, number, values,
        };

        set.SetAction((parse, cancellationToken) => EditAsync(parse, parse.GetValue(setGame)!, (_, draft, _) =>
        {
            var definition = new AttributeDefinition
            {
                DisplayName = parse.GetValue(label),
                Kind = parse.GetValue(number) ? "number" : null,
                Values = parse.GetValue(number)
                    ? null
                    : [.. (parse.GetValue(values) ?? []).Select(v => v.Split('=', 2, StringSplitOptions.TrimEntries))
                        .Select(p => new AttributeValueDefinition { Id = p[0], DisplayName = p.Length == 2 ? p[1] : null })],
            };

            return Task.FromResult<(PackDraft, IReadOnlyList<string>, IReadOnlyList<string>)>(
                (DraftEdits.SetAttributeDefinition(draft, parse.GetValue(setId)!, definition), [parse.GetValue(setId)!], []));
        }, cancellationToken));

        var removeGame = StudioCommand.GameArgument();
        var removeId = new Argument<string>("attribute") { Description = "The attribute's id." };
        var remove = new Command("remove", "Remove an attribute from the game and from every character.") { removeGame, removeId };

        remove.SetAction((parse, cancellationToken) => EditAsync(parse, parse.GetValue(removeGame)!, (_, draft, _) =>
            Task.FromResult<(PackDraft, IReadOnlyList<string>, IReadOnlyList<string>)>(
                (DraftEdits.RemoveAttributeDefinition(draft, parse.GetValue(removeId)!), [parse.GetValue(removeId)!], [])),
            cancellationToken));

        var orderGame = StudioCommand.GameArgument();
        var orderIds = new Argument<string[]>("attributes")
        {
            Description = "Attribute ids in the order wanted. Any not named keep their order after these.",
            Arity = ArgumentArity.OneOrMore,
        };
        var order = new Command(
            "order",
            "Put the game's attributes in a new order: the order of the grid's filter rows and the table's columns.")
        {
            orderGame, orderIds,
        };

        order.SetAction((parse, cancellationToken) => EditAsync(parse, parse.GetValue(orderGame)!, (_, draft, _) =>
        {
            var ordered = DraftEdits.OrderAttributes(draft, parse.GetValue(orderIds)!);
            return Task.FromResult<(PackDraft, IReadOnlyList<string>, IReadOnlyList<string>)>(
                (ordered, [.. ordered.Game.Attributes?.Keys ?? []], []));
        }, cancellationToken));

        var addGame = StudioCommand.GameArgument();
        var addId = new Argument<string>("attribute") { Description = "The attribute's id, such as element." };
        var addValue = new Argument<string>("value") { Description = "The value's id, such as plasma. Give it a label later with 'set'." };
        var addValueCommand = new Command(
            "add-value",
            "Add one value to an attribute's list, keeping the others: the fix for a character set to a value its attribute does not list.")
        {
            addGame, addId, addValue,
        };

        addValueCommand.SetAction((parse, cancellationToken) => EditAsync(parse, parse.GetValue(addGame)!, (_, draft, _) =>
        {
            var changed = DraftEdits.AddAttributeValue(draft, parse.GetValue(addId)!, parse.GetValue(addValue)!);

            return Task.FromResult<(PackDraft, IReadOnlyList<string>, IReadOnlyList<string>)>(
                ReferenceEquals(changed, draft)
                    ? (draft, [], [$"'{parse.GetValue(addValue)}' is already one of its values."])
                    : (changed, [parse.GetValue(addId)!], [$"Added '{parse.GetValue(addValue)}'."]));
        }, cancellationToken));

        return new Command("attribute", "Define the game's attributes, such as an element or a rarity.") { set, addValueCommand, remove, order };
    }

    /// <summary>Builds <c>xxsm studio game</c>.</summary>
    public static Command CreateGame()
    {
        var game = StudioCommand.GameArgument();
        var name = new Option<string?>("--name") { Description = "The game's name." };
        var shortName = new Option<string?>("--short-name") { Description = "A short form of the name." };
        var importer = new Option<string?>("--importer") { Description = "The folder name XXMI uses for this game." };
        var icon = new Option<string?>("--icon") { Description = "A PNG, JPEG or WebP picture for the game's icon. It is copied into the draft." };
        var mods = new Option<string?>("--mods") { Description = "The game's Mods folder, which 'xxsm studio try' uses." };

        var command = new Command("game", "Change the game's own details.") { game, name, shortName, importer, icon, mods };

        command.SetAction((parse, cancellationToken) => EditAsync(parse, parse.GetValue(game)!, async (provider, draft, token) =>
        {
            var changed = new List<string>();

            var edit = new GameEdit
            {
                DisplayName = parse.GetValue(name) is { } n ? EditField<string>.To(n) : default,
                ShortName = parse.GetValue(shortName) is { } s ? EditField<string>.To(s) : default,
                Importer = parse.GetValue(importer) is { } i ? EditField<string>.To(i) : default,
            };

            if (parse.GetValue(icon) is { Length: > 0 } picture)
            {
                var stored = await provider.GetRequiredService<IStudioDraftStore>()
                    .StoreImageAsync(draft.GameId, "_game", Path.GetFullPath(picture), token)
                    .ConfigureAwait(false);
                edit = edit with { Icon = EditField<string>.To(stored) };
                changed.Add("icon");
            }

            draft = DraftEdits.EditGame(draft, edit);

            if (parse.GetValue(mods) is { Length: > 0 } folder)
            {
                draft = draft with { Info = draft.Info with { ModsDirectory = Path.GetFullPath(folder) } };
                changed.Add("mods folder");
            }

            if (edit.DisplayName.IsSet)
            {
                changed.Add("name");
            }

            if (edit.ShortName.IsSet)
            {
                changed.Add("short name");
            }

            if (edit.Importer.IsSet)
            {
                changed.Add("importer");
            }

            return (draft, (IReadOnlyList<string>)changed, (IReadOnlyList<string>)[]);
        }, cancellationToken));

        return command;
    }
}
