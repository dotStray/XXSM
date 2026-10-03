using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Xxsm.Cli.Output;
using Xxsm.Core;
using Xxsm.Core.Ini;
using Xxsm.Core.Io;
using Xxsm.Core.Text;

namespace Xxsm.Cli.Commands;

/// <summary><c>xxsm mod defaults</c>: the settings a mod keeps between game sessions, and their defaults.</summary>
internal static class ModDefaultsCommand
{
    /// <summary>Builds the command.</summary>
    public static Command Create()
    {
        var folder = new Argument<string>("folder") { Description = "The mod folder." };

        var useInGame = new Option<bool>("--use-in-game")
        {
            Description = "Make the values the game saved for this mod its defaults.",
        };

        var restore = new Option<bool>("--restore")
        {
            Description = "Put back the defaults the mod came with, from the copy XXSM kept before its first edit.",
        };

        var command = new Command(
            "defaults",
            "List the settings a mod keeps between game sessions, with its defaults and the values the game saved; or make " +
            "the saved values the defaults. Only the default on each setting's line changes.")
        {
            folder,
            useInGame,
            restore,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            if (parse.GetValue(useInGame) && parse.GetValue(restore))
            {
                throw new ModOperationException("Give --use-in-game or --restore, not both.");
            }

            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var settings = provider.GetRequiredService<ISavedSettingsService>();
            var modFolder = Path.GetFullPath(parse.GetValue(folder)!);

            var read = await settings.ReadAsync(modFolder, cancellationToken).ConfigureAwait(false);
            var wanted = parse.GetValue(useInGame)
                ? read.Settings.Where(setting => setting.DiffersFromGame).Select(setting => Edit(setting, setting.InGame!))
                : parse.GetValue(restore)
                    ? read.Settings.Where(setting => setting.DiffersFromOriginal).Select(setting => Edit(setting, setting.Original!))
                    : [];
            List<SavedSettingEdit> edits = [.. wanted];

            if (edits.Count > 0)
            {
                await settings.WriteAsync(modFolder, edits, cancellationToken).ConfigureAwait(false);
            }

            var report = new ModDefaultsReport(
                modFolder,
                read.GameSettingsFile,
                [
                    .. read.Settings.Select(setting => new ModDefaultReport(
                        setting.File,
                        setting.Line,
                        setting.Name,
                        setting.EffectiveDefault,
                        setting.InGame,
                        setting.Original,
                        edits.FirstOrDefault(edit => edit.File == setting.File && edit.Line == setting.Line)?.Value)),
                ],
                read.Problems);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report);
            }

            if (report.Settings.Count == 0)
            {
                Console.Out.WriteLine("This mod keeps no settings between game sessions.");
            }

            foreach (var file in report.Settings.GroupBy(setting => setting.File))
            {
                Console.Out.WriteLine(PathDisplay.Show(file.Key));

                foreach (var setting in file)
                {
                    var inGame = setting.InGame ?? "not saved";
                    var changed = setting.NewDefault is { } value ? $"  → default now {value}" : string.Empty;
                    Console.Out.WriteLine($"  {setting.Name,-20} default {setting.Default,-10} in game {inGame}{changed}");
                }
            }

            foreach (var problem in report.Problems)
            {
                CliOutput.WriteError(problem);
            }

            if (parse.GetValue(useInGame) || parse.GetValue(restore))
            {
                Console.Out.WriteLine(edits.Count == 0 ? "Nothing to change." : $"Changed {EnglishCount.Plural(edits.Count, "default", "defaults")}.");
            }

            return 0;
        });

        return command;
    }

    private static SavedSettingEdit Edit(SavedSetting setting, string value) =>
        new(setting.File, setting.Line, setting.Default, value);
}
