using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Xxsm.Cli.Output;
using Xxsm.Core.GameBanana;
using Xxsm.Core.Io;
using Xxsm.Core.Settings;
using Xxsm.Core.Text;
using Xxsm.Packs.Downloads;

namespace Xxsm.Cli.Commands;

/// <summary><c>xxsm mod downloads</c>: what has been fetched from GameBanana, and what became of it.</summary>
internal static class ModDownloadCommands
{
    /// <summary>Builds the command.</summary>
    public static Command Create()
    {
        var remove = new Option<string[]>("--remove")
        {
            Description = "Take these entries off the list by id, and remove their archives.",
            AllowMultipleArgumentsPerToken = true,
        };

        var clear = new Option<bool>("--clear")
        {
            Description = "Take every finished entry off the list, and remove their archives.",
        };

        var prune = new Option<bool>("--prune")
        {
            Description = "Remove only what has expired, as the application does at start-up.",
        };

        var command = new Command(
            "downloads", "Show what has been downloaded from GameBanana, and what became of it.")
        {
            remove, clear, prune,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var downloads = provider.GetRequiredService<IDownloadManager>();
            var store = provider.GetRequiredService<IDownloadStore>();
            var settings = await provider
                .GetRequiredService<IAppSettingsStore>()
                .ReadAsync(cancellationToken)
                .ConfigureAwait(false);

            var jobs = await downloads.LoadAsync(cancellationToken).ConfigureAwait(false);
            var removed = 0;

            if (parse.GetValue(prune))
            {
                // LoadAsync has already pruned; reporting what it removed is the whole answer.
                removed = jobs.Count - downloads.Jobs.Count;
            }

            foreach (var id in parse.GetValue(remove) ?? [])
            {
                if (await downloads.ForgetAsync(id, cancellationToken).ConfigureAwait(false))
                {
                    removed++;
                }
                else
                {
                    CliOutput.WriteError($"There is no download '{id}' on the list.");
                }
            }

            if (parse.GetValue(clear))
            {
                foreach (var job in downloads.Jobs.Where(entry => !entry.IsRunning).ToList())
                {
                    if (await downloads.ForgetAsync(job.Id, cancellationToken).ConfigureAwait(false))
                    {
                        removed++;
                    }
                }
            }

            var report = new ModDownloadsReport(
                store.Path,
                settings.GameBanana.KeepDownloads.TotalDays,
                removed,
                [.. downloads.Jobs.Select(Describe)]);

            if (parse.GetValue(GlobalOptions.Json))
            {
                Console.Out.WriteLine(CliJson.Serialize(report));

                return 0;
            }

            Write(report);

            return 0;
        });

        return command;
    }

    /// <summary>Projects a download into what the report prints.</summary>
    internal static ModDownloadReport Describe(DownloadJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        var record = job.Record;

        return new ModDownloadReport(
            record.Id,
            record.ModId,
            record.Name,
            record.Author,
            record.Version,
            record.PageUrl ?? GameBananaUrl.ForMod(record.ModId),
            record.FileName,
            CliOutput.Camel(record.State.ToString()),
            job.Bytes,
            record.TotalBytes,
            record.ArchivePath,
            record.InstalledPath,
            record.GameId,
            record.StartedAt,
            record.FinishedAt,
            record.Error);
    }

    private static void Write(ModDownloadsReport report)
    {
        if (report.Removed > 0)
        {
            Console.Out.WriteLine(
                $"Removed {EnglishCount.Plural(report.Removed, "entry", "entries")} from the list.");
            Console.Out.WriteLine();
        }

        if (report.Downloads.Count == 0)
        {
            Console.Out.WriteLine("Nothing has been downloaded from GameBanana.");
            Console.Out.WriteLine($"The list lives at {PathDisplay.Show(report.Path)}.");

            return;
        }

        Console.Out.WriteLine(
            $"{EnglishCount.Plural(report.Downloads.Count, "download", "downloads")}, newest first. "
            + $"A finished one is kept for {report.KeptForDays.ToString("0.#", CultureInfo.InvariantCulture)} day(s).");
        Console.Out.WriteLine();

        foreach (var entry in report.Downloads)
        {
            Console.Out.WriteLine($"{entry.Id}  {entry.State,-9}  {entry.Name ?? entry.FileName ?? "(unnamed)"}");

            var size = entry.TotalBytes is > 0
                ? $"{CliOutput.Bytes(entry.Bytes)} of {CliOutput.Bytes(entry.TotalBytes.Value)}"
                : CliOutput.Bytes(entry.Bytes);

            Console.Out.WriteLine(
                $"  {size}  {entry.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}"
                + (entry.GameId is { Length: > 0 } game ? $"  {game}" : string.Empty));

            if (entry.ArchivePath is { Length: > 0 } archive)
            {
                Console.Out.WriteLine($"  {archive}");
            }

            if (entry.InstalledPath is { Length: > 0 } installed)
            {
                Console.Out.WriteLine($"  installed to {installed}");
            }

            if (entry.Error is { Length: > 0 } error)
            {
                Console.Out.WriteLine($"  {error}");
            }

            Console.Out.WriteLine();
        }

        Console.Out.WriteLine(
            "'xxsm mod add <archive> --apply' installs one; "
            + "'xxsm mod downloads --remove <id>' takes it off the list.");
    }
}
