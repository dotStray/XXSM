using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Xxsm.Cli.Output;
using Xxsm.Core;
using Xxsm.Core.Hashes;
using Xxsm.Core.Text;
using Xxsm.Packs.Studio;

namespace Xxsm.Cli.Commands;

/// <summary><c>xxsm studio hashes</c>: the Problems panel's hash warnings, and the fixes it offers.</summary>
internal static class StudioHashCommands
{
    /// <summary>Builds <c>xxsm studio hashes</c>.</summary>
    public static Command Create() =>
        new("hashes", "See which hashes characters share, who carries a hash and which the pack ignores, and fix it: keep hashes on one character, ignore a hash, or delete hashes no character claims.")
        {
            CreateShared(),
            CreateWho(),
            CreateKeep(),
            CreateIgnored(),
            CreateIgnore(),
            CreateUnignore(),
            CreateDropUnclaimed(),
        };

    private static Command CreateShared()
    {
        var game = StudioCommand.GameArgument();
        var characters = new Argument<string[]>("characters")
        {
            Description = "Only the hashes these families share, by any of their characters' internal names. All when none are given.",
            Arity = ArgumentArity.ZeroOrMore,
        };

        var command = new Command("shared", "List the hashes characters share, as the Problems panel's 'share N hashes' warnings count them.")
        {
            game, characters,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var draft = await StudioCommand.LoadAsync(provider, parse.GetValue(game)!, cancellationToken).ConfigureAwait(false);

            var groups = parse.GetValue(characters) is { Length: > 0 } named
                ? [ForFamilies(draft, named)]
                : HashSharing.Groups(draft)
                    .Select(g => new StudioSharedGroupReport(g.Families, [.. HashSharing.Between(draft, g.Families).Select(Report)]))
                    .ToList();

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(new StudioSharedHashesReport(draft.GameId, groups));
            }

            if (groups.All(g => g.Hashes.Count == 0))
            {
                Console.Out.WriteLine("No hashes are shared between characters.");
                return 0;
            }

            foreach (var group in groups.Where(g => g.Hashes.Count > 0))
            {
                Console.Out.WriteLine($"{JoinNames(group.Families)} share {EnglishCount.Plural(group.Hashes.Count, "hash", "hashes")}:");

                foreach (var hash in group.Hashes)
                {
                    Console.Out.WriteLine($"  {hash.Hash}  {string.Join(", ", hash.Characters)}");
                }

                Console.Out.WriteLine();
            }

            return 0;
        });

        return command;
    }

    /// <summary>"A and B", "A, B and C".</summary>
    private static string JoinNames(IReadOnlyList<string> names) =>
        names.Count <= 1 ? string.Join(string.Empty, names) : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];

    private static StudioSharedGroupReport ForFamilies(PackDraft draft, IReadOnlyList<string> named)
    {
        var families = named
            .Select(name => HashSharing.FamilyOf(draft, name)
                            ?? throw new ModOperationException($"The draft has no character called '{name}'."))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new StudioSharedGroupReport(families, [.. HashSharing.Between(draft, families).Select(Report)]);
    }

    private static StudioSharedHashReport Report(SharedHash hash) => new(hash.Hash, hash.Characters);

    private static Command CreateWho()
    {
        var game = StudioCommand.GameArgument();
        var hash = new Argument<string>("hash") { Description = "The hash." };
        var command = new Command("who", "List every character that carries a hash.") { game, hash };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var draft = await StudioCommand.LoadAsync(provider, parse.GetValue(game)!, cancellationToken).ConfigureAwait(false);
            var asked = parse.GetValue(hash)!;
            var carriers = HashSharing.Carriers(draft, asked);
            var ignored = HashSharing.IsIgnored(draft, asked);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(new StudioHashCarriersReport(draft.GameId, asked, carriers, ignored));
            }

            Console.Out.WriteLine(carriers.Count == 0
                ? $"No character carries {asked}."
                : $"{asked} is on {EnglishCount.Plural(carriers.Count, "character", "characters")}: {string.Join(", ", carriers)}");

            if (ignored)
            {
                Console.Out.WriteLine("The pack ignores it, so the sorter never scores it.");
            }

            return 0;
        });

        return command;
    }

    private static Command CreateKeep()
    {
        var game = StudioCommand.GameArgument();
        var keeper = new Argument<string>("character") { Description = "The character to keep them on. Its outfits keep theirs too." };
        var hashes = new Option<string[]>("--hash") { Description = "A hash to keep on it only. May be given more than once.", AllowMultipleArgumentsPerToken = true };
        var sharedWith = new Option<string[]>("--shared-with")
        {
            Description = "Every hash it shares with these characters, as 'shared' lists them. May be given more than once.",
            AllowMultipleArgumentsPerToken = true,
        };

        var command = new Command("keep", "Keep hashes on one character only: every other character outside its family loses them.")
        {
            game, keeper, hashes, sharedWith,
        };

        command.SetAction((parse, cancellationToken) => StudioEditCommands.EditAsync(parse, parse.GetValue(game)!, (_, draft, _) =>
        {
            var name = parse.GetValue(keeper)!;
            var wanted = (parse.GetValue(hashes) ?? []).ToList();

            if (parse.GetValue(sharedWith) is { Length: > 0 } others)
            {
                wanted.AddRange(ForFamilies(draft, [name, .. others]).Hashes.Select(h => h.Hash));
            }

            if (wanted.Count == 0)
            {
                throw new ModOperationException("Name the hashes with --hash, or the characters it shares them with with --shared-with.");
            }

            var change = DraftEdits.KeepHashesOn(draft, name, wanted);

            return Task.FromResult<(PackDraft, IReadOnlyList<string>, IReadOnlyList<string>)>(
                (change.Draft, change.Count == 0 ? [] : [name], [$"Removed {EnglishCount.Plural(change.Count, "hash", "hashes")} from other characters."]));
        }, cancellationToken));

        return command;
    }

    private static Command CreateIgnored()
    {
        var game = StudioCommand.GameArgument();
        var command = new Command("ignored", "List the pack's hashes to ignore, and who carries each. 'unignore' takes one off.") { game };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var draft = await StudioCommand.LoadAsync(provider, parse.GetValue(game)!, cancellationToken).ConfigureAwait(false);

            var entries = (draft.Hashes.IgnoredHashes ?? [])
                .Select(entry => new StudioIgnoredHashReport(
                    entry,
                    HashText.Normalize(entry) is not null,
                    HashSharing.Carriers(draft, entry)))
                .ToList();

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(new StudioIgnoredHashesReport(draft.GameId, entries));
            }

            if (entries.Count == 0)
            {
                Console.Out.WriteLine("The pack ignores no hashes.");
                return 0;
            }

            Console.Out.WriteLine($"The pack ignores {EnglishCount.Plural(entries.Count, "hash", "hashes")}, so the sorter never scores them:");

            foreach (var entry in entries)
            {
                var who = !entry.IsAHash
                    ? "not a hash — it does nothing"
                    : entry.Characters.Count == 0
                        ? "no character carries it"
                        : $"on {string.Join(", ", entry.Characters)}";

                Console.Out.WriteLine($"  {entry.Entry}  ({who})");
            }

            Console.Out.WriteLine("Take one off with: xxsm studio hashes unignore <game> <hash>");
            return 0;
        });

        return command;
    }

    private static Command CreateIgnore()
    {
        var game = StudioCommand.GameArgument();
        var hashes = new Argument<string[]>("hashes") { Description = "The hashes.", Arity = ArgumentArity.OneOrMore };
        var anyway = new Option<bool>("--anyway")
        {
            Description = "Ignore them even though characters would stop being found by their own hashes.",
        };

        var command = new Command(
            "ignore",
            "Put hashes on the pack's list of hashes to ignore, so the sorter never scores them. The characters keep them. Refused when it would cost a character its identification, unless --anyway.")
        {
            game, hashes, anyway,
        };

        command.SetAction((parse, cancellationToken) => StudioEditCommands.EditAsync(parse, parse.GetValue(game)!, (provider, draft, _) =>
        {
            var wanted = parse.GetValue(hashes)!;

            // Measured first: ignoring a shared hash can stop a pack sorting at all.
            var cost = provider.GetRequiredService<IHashIgnoreCost>().Measure(draft, wanted);

            if (!cost.IsFree && !parse.GetValue(anyway))
            {
                throw new ModOperationException(Refusal(cost));
            }

            var change = DraftEdits.IgnoreHashes(draft, wanted);

            IReadOnlyList<string> notes =
            [
                $"Now ignoring {EnglishCount.Plural(change.Count, "more hash", "more hashes")}.",
                .. cost.Losses.Select(loss =>
                    $"{loss.InternalName} is no longer found by its own hashes: {Lands(loss)}."),
            ];

            return Task.FromResult<(PackDraft, IReadOnlyList<string>, IReadOnlyList<string>)>(
                (change.Draft, change.Count == 0 ? [] : ["ignored hashes"], notes));
        }, cancellationToken));

        return command;
    }

    /// <summary>Why an ignore was refused, with what it would have cost, in full.</summary>
    private static string Refusal(HashIgnoreCostReport cost)
    {
        var lines = cost.Losses.Select(loss => $"  {loss.InternalName}: {Lands(loss)}");

        return $"""
                Ignoring {EnglishCount.Plural(cost.Adding, "hash", "hashes")} would leave {EnglishCount.Plural(cost.Losses.Count, "character", "characters")} unable to find their own mods, out of {cost.Checked} checked:
                {string.Join(Environment.NewLine, lines)}
                A hash two characters share still tells them apart from everyone else, so the sorter counts it. Removing the wrong hashes from a character ('keep') usually fixes the warning instead. Pass --anyway to ignore them regardless.
                """;
    }

    /// <summary>Where one character's mods would land instead.</summary>
    private static string Lands(HashIgnoreLoss loss) =>
        loss.IsAmbiguous
            ? "it would tie with another character and go to Others"
            : loss.GoesToInstead is { } other
                ? $"its mods would go to {other}"
                : "its mods would go to Others";

    private static Command CreateUnignore()
    {
        var game = StudioCommand.GameArgument();
        var entries = new Argument<string[]>("entries") { Description = "The entries, as they are written on the list.", Arity = ArgumentArity.OneOrMore };
        var command = new Command("unignore", "Take entries off the pack's list of hashes to ignore, including ones that are not hashes at all.")
        {
            game, entries,
        };

        command.SetAction((parse, cancellationToken) => StudioEditCommands.EditAsync(parse, parse.GetValue(game)!, (_, draft, _) =>
        {
            var change = DraftEdits.UnignoreHashes(draft, parse.GetValue(entries)!);

            return Task.FromResult<(PackDraft, IReadOnlyList<string>, IReadOnlyList<string>)>(
                (change.Draft, change.Count == 0 ? [] : ["ignored hashes"], [$"Took {EnglishCount.Plural(change.Count, "entry", "entries")} off the list."]));
        }, cancellationToken));

        return command;
    }

    private static Command CreateDropUnclaimed()
    {
        var game = StudioCommand.GameArgument();
        var name = new Argument<string>("name") { Description = "The internal name the hashes are filed under, which no character has." };
        var command = new Command("drop-unclaimed", "Delete the hashes filed under a name no character has. 'character add --internal-name' claims them instead.")
        {
            game, name,
        };

        command.SetAction((parse, cancellationToken) => StudioEditCommands.EditAsync(parse, parse.GetValue(game)!, (_, draft, _) =>
        {
            var change = DraftEdits.RemoveUnclaimedHashes(draft, parse.GetValue(name)!);

            return Task.FromResult<(PackDraft, IReadOnlyList<string>, IReadOnlyList<string>)>(
                (change.Draft, change.Count == 0 ? [] : [parse.GetValue(name)!], [$"Deleted {EnglishCount.Plural(change.Count, "hash", "hashes")}."]));
        }, cancellationToken));

        return command;
    }
}
