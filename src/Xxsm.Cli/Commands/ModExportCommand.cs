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

        var useOriginals = new Option<bool>("--use-originals")
        {
            Description = "Copy each INI XXSM changed (keys, defaults) as its author shipped it. The Mods folder keeps the changed one.",
        };

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
            useOriginals,
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
                        },
                        parse.GetValue(useOriginals)),
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
                plan.RenamedCount,
                plan.Options.UseOriginals,
                plan.ChangedIniModCount,
                plan.LosesOriginals);

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
                ("Changed INIs", report.ChangedIniMods == 0
                    ? "none"
                    : EnglishCount.Plural(report.ChangedIniMods, "mod", "mods") + (report.UseOriginals
                        ? ": the copies have the authors' original INIs"
                        : report.SkipMetadata
                            ? ": the copies keep XXSM's changes and not the authors' originals (add --use-originals)"
                            : ": the copies keep XXSM's changes, the originals in each copy's .xxsm folder")),
                ("Copied to", report.Folder is { } shown ? PathDisplay.Show(shown) : "nothing copied (--dry-run)"),
            ]);

            return 0;
        });

        return command;
    }
}
