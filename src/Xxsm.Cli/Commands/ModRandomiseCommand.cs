using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Xxsm.Cli.Output;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Packs.Randomising;

namespace Xxsm.Cli.Commands;

/// <summary><c>xxsm mod randomise</c>: one random mod per character, the rest switched off.</summary>
internal static class ModRandomiseCommand
{
    /// <summary>Builds the command.</summary>
    public static Command Create()
    {
        var game = new Option<string>("--game")
        {
            Description = "The game, which says which folders are characters.",
            Required = true,
        };

        var pack = new Option<string?>("--pack")
        {
            Description = "Use a pack directory directly instead of the installed one.",
        };

        var mods = ModsFolderOptions.Mods();

        var character = new Option<string[]>("--character")
        {
            Description = "Only this character's folder. Repeat it for several. Without it, every character.",
        };

        var seed = new Option<int?>("--seed")
        {
            Description = "Choose the same way every time, for a repeatable result.",
        };

        var dryRun = new Option<bool>("--dry-run")
        {
            Description = "Show what would be chosen and switch nothing.",
        };

        var command = new Command(
            "randomise",
            "Switch on one mod per character, chosen at random from all of its mods, and switch the " +
            "rest of that character's mods off. Undo with: xxsm switches undo")
        {
            game,
            pack,
            mods,
            character,
            seed,
            dryRun,
        };

        command.Aliases.Add("randomize");

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var modsDirectory = await ModsFolderOptions
                .ResolveAsync(provider, parse, mods, parse.GetValue(game)!, cancellationToken)
                .ConfigureAwait(false);
            var data = await SortCommand
                .LoadGameDataAsync(provider, parse.GetValue(game)!, parse.GetValue(pack), cancellationToken)
                .ConfigureAwait(false);
            var inventory = await provider.GetRequiredService<IModRepository>()
                .ScanAsync(modsDirectory, cancellationToken)
                .ConfigureAwait(false);

            var randomiser = provider.GetRequiredService<IModRandomiser>();
            var random = parse.GetValue(seed) is { } fixedSeed ? new Random(fixedSeed) : new Random();
            var only = parse.GetValue(character) is { Length: > 0 } named ? named : null;

            var plan = randomiser.Plan(inventory, data, random, only);

            if (only is not null)
            {
                var unknown = only
                    .Where(name => !plan.Rows.Any(row => PathComparer.AreNamesEqual(row.Folder.Name, name)))
                    .ToList();

                if (unknown.Count > 0)
                {
                    CliOutput.WriteError(
                        $"No character folder with mods is called {string.Join(", ", unknown.Select(name => $"'{name}'"))}. " +
                        "Nothing was switched.");
                    return 1;
                }
            }

            ModSwitchRunResult? run = null;

            // A run that changes nothing is not journalled, so there is no Undo to print.
            if (!parse.GetValue(dryRun) && plan.Rows.Any(row => row.ChangesAnything))
            {
                run = await randomiser.ApplyAsync(plan.ModsDirectory, plan.Rows, cancellationToken).ConfigureAwait(false);
            }

            var report = new RandomiseReport(
                run is not null,
                run?.RunId,
                [
                    .. plan.Rows.Select(row => new RandomiseRowReport(
                        row.Folder.Name,
                        row.Chosen.Path,
                        row.Chosen.DisplayName,
                        [.. row.SwitchedOff.Select(mod => mod.Path)])),
                ],
                run?.EnabledCount ?? 0,
                run?.DisabledCount ?? 0,
                [
                    .. (run?.Failures ?? []).Select(failure => new RandomiseFailureReport(
                        failure.Switch.ModFolder, failure.Error ?? string.Empty)),
                ]);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report, report.Failures.Count > 0 ? 1 : 0);
            }

            CliOutput.WriteRows(
            [
                ("Characters", report.Rows.Count.ToString(CultureInfo.InvariantCulture)),
                ("Switched on", report.EnabledCount.ToString(CultureInfo.InvariantCulture)),
                ("Switched off", report.DisabledCount.ToString(CultureInfo.InvariantCulture)),
            ]);

            foreach (var row in report.Rows)
            {
                Console.Out.WriteLine($"  {row.Character}: {row.ChosenName}");
            }

            foreach (var failure in report.Failures)
            {
                Console.Out.WriteLine($"  fail  {failure.Mod}  —  {failure.Error}");
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
                Console.Out.WriteLine("Nothing to switch: every chosen mod is already the only one on.");
            }

            return report.Failures.Count > 0 ? 1 : 0;
        });

        return command;
    }
}
