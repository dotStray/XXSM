using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Xxsm.Cli.Output;
using Xxsm.Core;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Core.Text;
using Xxsm.Packs.Installation;
using Xxsm.Packs.Merge;
using Xxsm.Packs.Sorting;

namespace Xxsm.Cli.Commands;

/// <summary><c>xxsm sort</c>: decide where a mod belongs, and show the working.</summary>
internal static class SortCommand
{
    /// <summary>Builds the command.</summary>
    public static Command Create()
    {
        var command = new Command("sort", "Work out which character a mod belongs to.")
        {
            CreateExplain(),
            CreateIndex(),
        };

        foreach (var subcommand in SortRunCommands.Create(LoadGameDataAsync))
        {
            command.Subcommands.Add(subcommand);
        }

        return command;
    }

    private static Option<string> GameOption() => new("--game")
    {
        Description = "The game to sort against, for example the gameId from a pack's manifest.",
        Required = true,
    };

    private static Option<string?> PackOption() => new("--pack")
    {
        Description = "Use a pack directory directly instead of the installed one.",
    };

    /// <summary>Loads the merged pack data for a game, refusing when no pack is installed.</summary>
    public static async Task<GameData> LoadGameDataAsync(
        ServiceProvider provider, string gameId, string? explicitPack, CancellationToken cancellationToken)
    {
        var directory = explicitPack ?? await provider
            .GetRequiredService<IPackInstaller>()
            .FindActivePackDirectoryAsync(gameId, cancellationToken)
            .ConfigureAwait(false);

        if (directory is null)
        {
            throw new PackLoadException(
                $"No Game Pack is installed for '{gameId}'. " +
                "Install one with: xxsm pack import <folder-or-zip>");
        }

        return await provider
            .GetRequiredService<IGameDataService>()
            .LoadAsync(gameId, directory, cancellationToken)
            .ConfigureAwait(false);
    }

    private static Command CreateExplain()
    {
        var folder = new Argument<string>("folder") { Description = "The mod folder to place." };
        var game = GameOption();
        var pack = PackOption();

        var variantOverride = new Option<string?>("--override")
        {
            Description = "Treat the mod as manually filed to this variant, as .xxsm/mod.json would.",
        };

        var archive = new Option<string?>("--archive-name")
        {
            Description = "The name of the archive it came from, which often carries the character's name.",
        };

        var command = new Command("explain", "Say which variant a mod folder belongs to, and why.")
        {
            folder,
            game,
            pack,
            variantOverride,
            archive,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var gameId = parse.GetValue(game)!;
            var data = await LoadGameDataAsync(provider, gameId, parse.GetValue(pack), cancellationToken)
                .ConfigureAwait(false);

            var root = parse.GetValue(folder)!;
            var signals = await provider
                .GetRequiredService<IModSignalExtractor>()
                .ExtractAsync(root, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var decision = provider.GetRequiredService<IModSorter>().Sort(
                SortIndex.Build(data),
                signals,
                new SortRequest(
                    parse.GetValue(variantOverride),
                    ArchiveName: parse.GetValue(archive)));

            var report = new SortReport(
                decision.Root,
                gameId,
                decision.VariantId,
                decision.FamilyId,
                decision.DecidedBy.ToString().ToLowerInvariant(),
                decision.Confidence,
                decision.IsAmbiguous,
                decision.IsDefaultVariantFallback,
                decision.MemberDecidedByName,
                decision.MatchedVariantHasNoHashes,
                decision.RunnerUpVariantId,
                decision.Explanation,
                decision.ExtractedHashes,
                [.. decision.PrunedMatches.Select(Pruned)],
                [
                    .. decision.Candidates.Select(c => new SortReportCandidate(
                        c.VariantId, c.FamilyId, c.Score, c.FamilyScore, c.MatchedHashes, c.FamilyUniqueMatches)),
                ]);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report);
            }

            WriteExplain(report);
            return 0;
        });

        return command;
    }

    private static Command CreateIndex()
    {
        var game = GameOption();
        var pack = PackOption();

        var command = new Command(
            "index",
            "Show the hash index a sort scores against, and everything pruned from it.")
        {
            game,
            pack,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var gameId = parse.GetValue(game)!;
            var data = await LoadGameDataAsync(provider, gameId, parse.GetValue(pack), cancellationToken)
                .ConfigureAwait(false);

            var index = SortIndex.Build(data);
            var report = new SortIndexReport(
                gameId,
                data.Variants.Count,
                data.Variants.Count(v => v.IsHashesPending),
                index.IdentifyingCount,
                index.Settings.AmbiguityThreshold,
                [.. index.Pruned.Select(Pruned)]);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report);
            }

            CliOutput.WriteRows(
            [
                ("Game", report.GameId),
                ("Variants", report.VariantCount.ToString(CultureInfo.InvariantCulture)),
                ("Awaiting hashes", report.HashesPendingCount.ToString(CultureInfo.InvariantCulture)),
                ("Identifying hashes", report.IdentifyingHashes.ToString(CultureInfo.InvariantCulture)),
                ("Ambiguity threshold", report.AmbiguityThreshold.ToString(CultureInfo.InvariantCulture)),
                ("Pruned", EnglishCount.Plural(report.Pruned.Count, "hash", "hashes")),
            ]);

            if (report.Pruned.Count > 0)
            {
                Console.Out.WriteLine();
                Console.Out.WriteLine("Pruned — these identify nobody and never score");
                foreach (var pruned in report.Pruned)
                {
                    Console.Out.WriteLine(
                        $"  {pruned.Hash,-18} {pruned.Reason,-13} " +
                        $"{pruned.VariantCount.ToString(CultureInfo.InvariantCulture)} variants");
                }
            }

            return 0;
        });

        return command;
    }

    private static SortPrunedHash Pruned(PrunedHash pruned) => new(
        pruned.Hash,
        pruned.Reason switch
        {
            HashPruneReason.Ignored => "ignored",
            HashPruneReason.FanOut => "fanOut",
            HashPruneReason.SharedShader => "sharedShader",
            _ => pruned.Reason.ToString().ToLowerInvariant(),
        },
        pruned.VariantCount);

    private static void WriteExplain(SortReport report)
    {
        CliOutput.WriteRows(
        [
            ("Mod folder", PathDisplay.Show(report.Root)),
            ("Game", report.GameId),
            ("Destination", report.Variant ?? "Others/"),
            ("Family", report.Family ?? "—"),
            ("Decided by", report.DecidedBy),
            ("Confidence", report.Confidence.ToString("F2", CultureInfo.InvariantCulture)),
            ("Runner-up", report.RunnerUp ?? "—"),
        ]);

        Console.Out.WriteLine();
        Console.Out.WriteLine(report.Explanation);

        if (report.Ambiguous)
        {
            Console.Out.WriteLine(
                "Nothing was chosen. Confirm one of the candidates, or it goes to Others/.");
        }

        if (report.DefaultVariantFallback)
        {
            Console.Out.WriteLine(
                "This is the family's default variant, not a positive identification of the outfit.");
        }

        if (report.MemberDecidedByName)
        {
            Console.Out.WriteLine(
                "The hashes settled the family but not the outfit, so the mod's own name chose " +
                "between them. Rename the folder, or file it by hand, to correct it.");
        }

        if (report.VariantHasNoHashes)
        {
            Console.Out.WriteLine(
                "That variant has no hashes yet. Use 'Learn hashes from a mod' to teach it some.");
        }

        if (report.Candidates.Count > 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("Candidates");
            Console.Out.WriteLine("  variant                        score  family  unique  matched");
            foreach (var candidate in report.Candidates.Take(10))
            {
                Console.Out.WriteLine(
                    $"  {candidate.Variant,-30} {candidate.Score,5}  " +
                    $"{candidate.FamilyScore,6}  {candidate.FamilyUniqueMatches,6}  " +
                    $"{candidate.MatchedHashes.Count,7}");
            }
        }

        if (report.ExtractedHashes.Count > 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine(
                $"Hashes found: {report.ExtractedHashes.Count.ToString(CultureInfo.InvariantCulture)}");
        }

        if (report.PrunedMatches.Count > 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("Found but not identifying");
            foreach (var pruned in report.PrunedMatches)
            {
                Console.Out.WriteLine(
                    $"  {pruned.Hash,-18} {pruned.Reason} " +
                    $"({pruned.VariantCount.ToString(CultureInfo.InvariantCulture)} variants)");
            }
        }
    }
}
