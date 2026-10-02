using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Xxsm.Cli.Output;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Core.Text;

namespace Xxsm.Cli.Commands;

/// <summary><c>xxsm mod export</c>: a copy of the Mods folder's mods somewhere else.</summary>
internal static class ModExportCommand
{
    /// <summary>Builds the command.</summary>
    public static Command Create()
    {
        var destination = new Argument<string>("destination")
        {
            Description = "The folder to make the export in. A new folder is made inside it; nothing there is touched.",
        };

        var mods = ModsFolderOptions.Mods();
        var game = ModsFolderOptions.Game();

        var enabledOnly = new Option<bool>("--enabled-only")
        {
            Description = "Copy only the mods that are switched on.",
        };

        var noMetadata = new Option<bool>("--no-metadata")
        {
            Description = "Leave out each mod's .xxsm folder: the details, picture and sort record XXSM keeps.",
        };

        var oneFolder = new Option<bool>("--one-folder")
        {
            Description = "Put every mod straight into the export folder, without the character folders it sits in. " +
                          "Two mods with the same name get (2), (3) after it.",
        };

        var switching = new Option<string>("--switch")
        {
            Description = "Switch every copy on or off. Only the copies change; without it each copy is as its mod is now.",
        };
        switching.AcceptOnlyFromAmong("on", "off");

        var dryRun = new Option<bool>("--dry-run")
        {
            Description = "Say what would be copied and copy nothing.",
        };

        var command = new Command(
            "export",
            "Copy the mods to another folder. Nothing in the Mods folder changes.")
        {
            destination,
            mods,
            game,
            enabledOnly,
            noMetadata,
            oneFolder,
            switching,
            dryRun,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var exporter = provider.GetRequiredService<IModExporter>();
            var modsDirectory = await ModsFolderOptions.ResolveAsync(provider, parse, mods, game, cancellationToken)
                .ConfigureAwait(false);

            var plan = await exporter
                .PlanAsync(
                    modsDirectory,
                    new ModExportOptions(
                        parse.GetValue(enabledOnly),
                        parse.GetValue(noMetadata),
                        parse.GetValue(oneFolder),
                        parse.GetValue(switching) switch
                        {
                            "on" => ModExportSwitching.AllOn,
                            "off" => ModExportSwitching.AllOff,
                            _ => ModExportSwitching.AsTheyAre,
                        }),
                    cancellationToken)
                .ConfigureAwait(false);

            ModExportResult? result = null;

            if (!parse.GetValue(dryRun))
            {
                result = await exporter
                    .ExportAsync(plan, Path.GetFullPath(parse.GetValue(destination)!), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            var report = new ModExportReport(
                plan.ModsDirectory,
                result?.Folder,
                plan.Mods.Count,
                plan.SkippedDisabledCount,
                plan.FileCount,
                plan.TotalBytes,
                plan.Options.EnabledOnly,
                plan.Options.SkipMetadata,
                plan.Options.OneFolder,
                plan.Options.Switching switch
                {
                    ModExportSwitching.AllOn => "on",
                    ModExportSwitching.AllOff => "off",
                    _ => "as-they-are",
                },
                plan.RenamedCount);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report);
            }

            CliOutput.WriteRows(
            [
                ("Mods folder", PathDisplay.Show(report.ModsDirectory)),
                ("Mods", report.ModCount.ToString(CultureInfo.InvariantCulture)),
                ("Left out, switched off", report.SkippedDisabledCount.ToString(CultureInfo.InvariantCulture)),
                ("Files", report.FileCount.ToString(CultureInfo.InvariantCulture)),
                ("Size", CliOutput.Bytes(report.Bytes)),
                ("Layout", report.OneFolder ? "every mod in one folder" : "as in the Mods folder"),
                ("Copies", report.Switch switch
                {
                    "on" => "all switched on",
                    "off" => "all switched off",
                    _ => "on or off as the mods are now",
                }),
                ("Numbered", report.RenamedCount == 0
                    ? "none"
                    : EnglishCount.Plural(report.RenamedCount, "mod", "mods") + " had a number added so no two share a name"),
                ("Copied to", report.Folder is { } shown ? PathDisplay.Show(shown) : "nothing copied (--dry-run)"),
            ]);

            return 0;
        });

        return command;
    }
}
