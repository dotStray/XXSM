using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Xxsm.Cli.Output;
using Xxsm.Core;
using Xxsm.Core.Io;
using Xxsm.Core.Text;
using Xxsm.Packs.Importing;

namespace Xxsm.Cli.Commands;

/// <summary><c>xxsm mod import</c>: bring in what JASM or XX-Mod-Manager knew about the mods.</summary>
internal static class ModImportCommand
{
    /// <summary>Builds the command.</summary>
    public static Command Create()
    {
        var game = new Option<string>("--game")
        {
            Description = "The game, which says what each character tag means.",
            Required = true,
        };

        var pack = new Option<string?>("--pack")
        {
            Description = "Use a pack directory directly instead of the installed one.",
        };

        var mods = ModsFolderOptions.Mods();

        var character = new Option<string[]>("--character")
        {
            Description =
                "The character for a tag XXSM does not know, as 'tag=internal name', e.g. 'March 7th=march7', " +
                "or 'tag=Others' to file its mods in Others, e.g. '!QoL=Others'. Repeat it for several.",
        };

        var useHashes = new Option<string[]>("--use-hashes")
        {
            Description = "A mod folder whose tag should not decide its character, leaving it to the hashes. Repeat it for several.",
        };

        var dryRun = new Option<bool>("--dry-run")
        {
            Description = "Show what would be imported and write nothing.",
        };

        var command = new Command(
            "import",
            "Import the details JASM or XX-Mod-Manager kept in each mod folder: names, links, descriptions, " +
            "hotkey notes, and the character a mod was tagged with. Writes .xxsm/mod.json only; nothing is moved. " +
            "Run xxsm sort run afterwards to file the mods under their characters.")
        {
            game,
            pack,
            mods,
            character,
            useHashes,
            dryRun,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var modsDirectory = await ModsFolderOptions
                .ResolveAsync(provider, parse, mods, parse.GetValue(game)!, cancellationToken)
                .ConfigureAwait(false);
            var data = await SortCommand
                .LoadGameDataAsync(provider, parse.GetValue(game)!, parse.GetValue(pack), cancellationToken)
                .ConfigureAwait(false);

            var choices = Choices(parse.GetValue(character) ?? []);
            var hashesDecide = (parse.GetValue(useHashes) ?? []).Select(Path.GetFullPath).ToList();
            var importer = provider.GetRequiredService<IModImporter>();

            var plan = await importer.PlanAsync(modsDirectory, data, choices, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var rows = plan.Rows
                .Select(row => hashesDecide.Any(folder => PathComparer.AreEqual(folder, row.ModFolder)) ? row with { FollowTag = false } : row)
                .ToList();

            ModImportResult? result = null;

            if (!parse.GetValue(dryRun))
            {
                result = await importer.ApplyAsync(rows, cancellationToken).ConfigureAwait(false);
            }

            var report = new ModImportReport(
                plan.ModsDirectory,
                result is not null,
                plan.CountFrom(ModImportSource.Jasm),
                plan.CountFrom(ModImportSource.XxModManager),
                [
                    .. rows.Select(row => new ModImportRowReport(
                        row.ModFolder,
                        row.Source == ModImportSource.Jasm ? "jasm" : "xxModManager",
                        row.Fills,
                        row.Details.Character,
                        row.Character?.InternalName,
                        row.HashCharacter?.InternalName,
                        row.FollowTag,
                        row.Problem)),
                ],
                plan.UnmatchedTags,
                result?.Written.Count ?? 0,
                [.. (result?.Failures ?? []).Select(failure => new ModImportFailureReport(failure.ModFolder, failure.Error))]);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report, report.Failures.Count > 0 ? 1 : 0);
            }

            CliOutput.WriteRows(
            [
                ("Mods folder", PathDisplay.Show(report.ModsDirectory)),
                ("From JASM", report.JasmCount.ToString(CultureInfo.InvariantCulture)),
                ("From XX-Mod-Manager", report.XxModManagerCount.ToString(CultureInfo.InvariantCulture)),
                ("With something new", rows.Count(row => row.ChangesAnything).ToString(CultureInfo.InvariantCulture)),
                ("Tag and hashes disagree", rows.Count(row => row.Disagrees).ToString(CultureInfo.InvariantCulture)),
                ("Written", report.Applied ? report.WrittenCount.ToString(CultureInfo.InvariantCulture) : "nothing (--dry-run)"),
            ]);

            foreach (var tag in report.UnmatchedTags)
            {
                Console.Out.WriteLine(
                    $"  no character called '{tag.Tag}' ({EnglishCount.Plural(tag.ModCount, "mod", "mods")}) — " +
                    $"choose one with --character \"{tag.Tag}=<internal name>\"");
            }

            foreach (var row in rows.Where(row => row.Disagrees))
            {
                Console.Out.WriteLine(
                    $"  {row.ModName}: tagged {row.Character!.InternalName}, hashes say {row.HashCharacter!.InternalName}" +
                    (row.FollowTag ? " — following the tag" : " — leaving it to the hashes"));
            }

            foreach (var row in rows.Where(row => row.Problem is not null))
            {
                Console.Out.WriteLine($"  skipped {row.ModName}: {row.Problem}");
            }

            foreach (var failure in report.Failures)
            {
                Console.Out.WriteLine($"  fail  {failure.Mod}  —  {failure.Error}");
            }

            if (report.Applied && report.WrittenCount > 0)
            {
                Console.Out.WriteLine($"Next: xxsm sort run --game {parse.GetValue(game)} to file them under their characters.");
            }

            return report.Failures.Count > 0 ? 1 : 0;
        });

        return command;
    }

    private static Dictionary<string, string> Choices(string[] values)
    {
        var choices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var value in values)
        {
            var split = value.LastIndexOf('=');

            if (split <= 0 || split == value.Length - 1)
            {
                throw new ModOperationException($"--character takes 'tag=internal name', e.g. 'March 7th=march7', not '{value}'.");
            }

            choices[value[..split].Trim()] = value[(split + 1)..].Trim();
        }

        return choices;
    }
}
