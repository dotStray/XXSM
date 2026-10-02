using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Xxsm.Cli.Output;
using Xxsm.Core.GameBanana;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Core.Settings;
using Xxsm.Core.Text;
using Xxsm.Packs.Downloads;
using Xxsm.Packs.GameBanana;
using Xxsm.Packs.Installation;
using Xxsm.Packs.Merge;

namespace Xxsm.Cli.Commands;

/// <summary><c>xxsm mod add</c>: the install flow, from a folder, an archive or a GameBanana address.</summary>
/// <remarks>Dry by default; <c>--apply</c> installs.</remarks>
internal static class ModAddCommand
{
    /// <summary>Builds the command.</summary>
    /// <param name="load">Loads the merged pack data for a game.</param>
    public static Command Create(
        Func<ServiceProvider, string, string?, CancellationToken, Task<GameData>> load)
    {
        ArgumentNullException.ThrowIfNull(load);

        var source = new Argument<string>("source")
        {
            Description =
                "A mod folder, a folder of mods, an archive, or a GameBanana mod address. "
                + "A folder or archive is left untouched.",
        };

        var game = new Option<string>("--game")
        {
            Description = "Which game to install into, and to sort against.",
            Required = true,
        };

        var pack = new Option<string?>("--pack")
        {
            Description = "Use a pack directory directly instead of the installed one.",
        };

        var mods = ModsFolderOptions.Mods();

        var character = new Option<string?>("--character")
        {
            Description =
                "Install everything under this character, overriding the sorter. "
                + "Takes an internal name.",
        };

        var only = new Option<string[]>("--only")
        {
            Description =
                "Install only the mods at these paths inside the source, as 'source' reports "
                + "them. May be given more than once. Omit it to install everything found.",
            AllowMultipleArgumentsPerToken = true,
        };

        var name = new Option<string?>("--name")
        {
            Description = "The folder name to give the installed mod. Only meaningful for a single mod.",
        };

        var displayName = new Option<string?>("--display-name")
        {
            Description =
                "What to call the mod in the interface, written as customName. The folder "
                + "name is what shows without it. Only meaningful for a single mod.",
        };

        var author = new Option<string?>("--author")
        {
            Description = "Who made the mod. Only meaningful for a single mod.",
        };

        var url = new Option<string?>("--url")
        {
            Description = "The mod's own page address. Only meaningful for a single mod.",
        };

        var notes = new Option<string?>("--notes")
        {
            Description = "Free text to keep with the mod. Only meaningful for a single mod.",
        };

        var preview = new Option<string?>("--preview")
        {
            Description = "A picture to show for the mod, copied into it. Only meaningful for a single mod.",
        };

        var files = new Option<bool>("--files")
        {
            Description = "List the files each mod holds, as the install screen does.",
        };

        var apply = new Option<bool>("--apply")
        {
            Description = "Actually copy the mods in. Without it nothing is written.",
        };

        var dryRun = new Option<bool>("--dry-run")
        {
            Description = "Show what would happen and write nothing. The default.",
        };

        var fileId = new Option<long?>("--file")
        {
            Description =
                "For a GameBanana address, which of the mod's files to download, by its id. Needed "
                + "when the page has several; without it they are listed and nothing is downloaded.",
        };

        var again = new Option<bool>("--again")
        {
            Description =
                "For a GameBanana address, download it even when it is already installed or already "
                + "on the download list. Without it an installed copy stops the run, and a download "
                + "waiting on the list is installed instead of fetching the file a second time.",
        };

        var command = new Command(
            "add", "Install a folder or an archive, working out where each mod in it belongs.")
        {
            source, game, pack, mods, character, only, name, displayName, author, url, notes,
            preview, files, apply, dryRun, fileId, again,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var shouldApply = parse.GetValue(apply) && !parse.GetValue(dryRun);

            var modsDirectory = await ModsFolderOptions
                .ResolveAsync(provider, parse, mods, parse.GetValue(game)!, cancellationToken)
                .ConfigureAwait(false);

            var data = await load(provider, parse.GetValue(game)!, parse.GetValue(pack), cancellationToken)
                .ConfigureAwait(false);

            var installer = provider.GetRequiredService<IModInstaller>();
            var given = parse.GetValue(source)!;

            var isAddress = GameBananaUrl.TryParseModId(given, out var modId);
            DownloadContents? existing = null;

            if (isAddress && !parse.GetValue(again))
            {
                var copies = await provider
                    .GetRequiredService<IModCopies>()
                    .FindAsync(modId, parse.GetValue(game), modsDirectory, cancellationToken)
                    .ConfigureAwait(false);

                if (copies.IsInstalled)
                {
                    CliOutput.WriteError(
                        "You already have this mod, installed as "
                        + string.Join(", ", copies.Installed.Select(copy => $"'{copy.ModFolder}'"))
                        + ". Add --again to download and install another copy.");

                    return 1;
                }

                existing = await OpenExistingAsync(provider, copies, parse.GetValue(fileId), cancellationToken)
                    .ConfigureAwait(false);

                if (existing is not null && parse.GetValue(GlobalOptions.Json) is false)
                {
                    Console.Out.WriteLine(
                        $"Using download {existing.Record.Id}, which is already on the list. "
                        + "Nothing was downloaded; --again fetches the file afresh.");
                }
            }

            var fetched = existing ?? (isAddress
                ? await FetchAsync(
                        provider,
                        modId,
                        parse.GetValue(fileId),
                        parse.GetValue(game),
                        parse.GetValue(GlobalOptions.Json),
                        cancellationToken)
                    .ConfigureAwait(false)
                : null);

            if (fetched is not null && existing is null && parse.GetValue(GlobalOptions.Json) is false)
            {
                Console.Out.WriteLine(
                    $"GameBanana  {fetched.Mod.Name ?? "(unnamed)"} by {fetched.Mod.Author ?? "(unknown)"}"
                    + $"  {PathDisplay.Show(fetched.File.File)}  {CliOutput.Bytes(fetched.Record.Bytes)}"
                    + (fetched.Record.Md5Verified ? "  (checksum verified)" : string.Empty));
            }

            using var plan = await installer
                .PlanAsync(
                    fetched?.ArchivePath ?? given,
                    data,
                    modsDirectory,
                    parse.GetValue(character),
                    settings: null,
                    cancellationToken)
                .ConfigureAwait(false);

            var wanted = parse.GetValue(only) ?? [];

            var chosen = plan.Candidates
                .Where(candidate => wanted.Length == 0
                                    || wanted.Any(path => PathComparer.AreEqual(path, candidate.RelativePath)))
                .ToList();

            if (wanted.Length > 0 && chosen.Count == 0)
            {
                CliOutput.WriteError(
                    "None of the paths given by --only is in the source. " +
                    "Run without --apply to see what it contains.");

                return 1;
            }

            // The metadata options describe one mod, so they apply only when the source held one.
            var single = chosen.Count == 1;

            // Author, version, description and address go on every mod; title and screenshot only on a single one.
            var fill = fetched is not null
                && (await provider.GetRequiredService<IAppSettingsStore>()
                        .ReadAsync(cancellationToken).ConfigureAwait(false))
                    .GameBanana.FetchOnInstallOrDefault;

            var choices = chosen
                .Select(candidate => new InstallChoice(
                    candidate,
                    Name: single ? parse.GetValue(name) : null,
                    DisplayName: single
                        ? parse.GetValue(displayName) ?? (fill ? fetched!.Mod.Name : null)
                        : null,
                    Author: (single ? parse.GetValue(author) : null) ?? (fill ? fetched!.Mod.Author : null),
                    ModUrl: (single ? parse.GetValue(url) : null) ?? fetched?.Mod.PageUrl.AbsoluteUri,
                    Notes: single ? parse.GetValue(notes) : null,
                    PreviewImage: single && parse.GetValue(preview) is { Length: > 0 } picture
                        ? PreviewImageSource.FromFile(picture)
                        : single && fill ? fetched?.Picture : null,
                    Version: fill ? fetched!.Mod.Version : null,
                    Description: fill ? GameBananaText.ForStorage(fetched!.Mod.Description) : null,
                    GameBanana: fetched is null
                        ? null
                        : provider
                            .GetRequiredService<IGameBananaInstallSource>()
                            .ProvenanceOf(fetched.Mod, fetched.File)))
                .ToList();

            InstallResult? result = null;

            if (shouldApply && choices.Count > 0)
            {
                result = await installer.ApplyAsync(plan, choices, data, cancellationToken).ConfigureAwait(false);

                if (fetched is not null && result.InstalledCount > 0)
                {
                    await provider
                        .GetRequiredService<IDownloadManager>()
                        .MarkInstalledAsync(
                            fetched.Record.Id,
                            result.Outcomes.FirstOrDefault(outcome => outcome.Succeeded)?.InstalledPath,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            else if (fetched is not null && parse.GetValue(GlobalOptions.Json) is false)
            {
                Console.Out.WriteLine(
                    $"The archive is kept as download {fetched.Record.Id}. "
                    + "'xxsm mod downloads' lists it; add --apply to install it.");
            }

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(Describe(plan, choices, result, shouldApply), result?.Failures.Count > 0 ? 1 : 0);
            }

            Write(plan, choices, result, shouldApply, parse.GetValue(files));
            return result?.Failures.Count > 0 ? 1 : 0;
        });

        return command;
    }

    /// <summary>The archive of a finished download already on the list, when it is the file this run fetches.</summary>
    private static async Task<DownloadContents?> OpenExistingAsync(
        ServiceProvider provider, ModCopies copies, long? fileId, CancellationToken cancellationToken)
    {
        if (copies.Download is not { IsRunning: false } job
            || (fileId is { } wanted && job.Record.FileId != wanted))
        {
            return null;
        }

        return await provider
            .GetRequiredService<IDownloadManager>()
            .OpenAsync(job.Id, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Looks a GameBanana mod up and downloads the file through the download list.</summary>
    private static async Task<DownloadContents> FetchAsync(
        ServiceProvider provider,
        long modId,
        long? fileId,
        string? gameId,
        bool json,
        CancellationToken cancellationToken)
    {
        var page = await provider
            .GetRequiredService<IGameBananaClient>()
            .GetModAsync(modId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var chosen = fileId is { } wanted
            ? page.Files.FirstOrDefault(file => file.IdRow == wanted)
              ?? throw new GameBananaException(
                  $"That mod has no file {wanted.ToString(CultureInfo.InvariantCulture)}. Its files are: "
                  + string.Join("; ", page.Files.Select(GameBananaMod.Describe)),
                  modId,
                  page.PageUrl)
            : null;

        var downloads = provider.GetRequiredService<IDownloadManager>();

        await downloads.LoadAsync(cancellationToken).ConfigureAwait(false);

        var job = downloads.Start(page, chosen, new DownloadRequest(gameId));
        var progress = new DownloadProgressWriter(json);

        downloads.Progress += progress.OnProgress;

        try
        {
            await using (cancellationToken.Register(() => downloads.Cancel(job.Id)).ConfigureAwait(false))
            {
                await job.Completion.ConfigureAwait(false);
            }
        }
        finally
        {
            downloads.Progress -= progress.OnProgress;
            progress.Done();
        }

        switch (job.State)
        {
            case DownloadState.Ready:
                break;

            case DownloadState.Blocked:
                throw new GameBananaDownloadBlockedException(
                    $"{job.Record.Error} Open {job.Record.PageUrl}, download the file, then run this "
                    + "again with the archive instead of the address.",
                    new Uri(job.Record.PageUrl ?? GameBananaUrl.ForMod(modId)),
                    modId);

            case DownloadState.Cancelled:
                throw new OperationCanceledException();

            default:
                throw new GameBananaException(
                    job.Record.Error ?? "The download did not finish.", modId, page.PageUrl);
        }

        if (job.Record.Error is { Length: > 0 } pictureError)
        {
            CliOutput.WriteError($"The preview picture was not fetched: {pictureError}");
        }

        return await downloads.OpenAsync(job.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new GameBananaException(
                "The download finished but its archive is no longer there.", modId, page.PageUrl);
    }

    /// <summary>Prints a download's progress on one line; silent under <c>--json</c> or when redirected.</summary>
    private sealed class DownloadProgressWriter(bool json)
    {
        private readonly bool _quiet = json || Console.IsOutputRedirected;
        private DateTimeOffset _last = DateTimeOffset.MinValue;
        private bool _wrote;

        public void OnProgress(object? sender, DownloadChange change)
        {
            var now = DateTimeOffset.UtcNow;

            if (_quiet || now - _last < TimeSpan.FromSeconds(1))
            {
                return;
            }

            _last = now;
            _wrote = true;

            var job = change.Job;

            var size = job.TotalBytes is > 0
                ? $"{CliOutput.Bytes(job.Bytes)} of {CliOutput.Bytes(job.TotalBytes.Value)}"
                : CliOutput.Bytes(job.Bytes);

            var rate = job.BytesPerSecond is { } speed ? $"  {CliOutput.Bytes((long)speed)}/s" : string.Empty;

            var left = job.Remaining is { } remaining
                ? $"  {Xxsm.Core.Text.ByteSize.DescribeDuration(remaining)} left"
                : string.Empty;

            Console.Out.Write($"\rDownloading  {size}{rate}{left}        ");
        }

        public void Done()
        {
            if (_wrote)
            {
                Console.Out.WriteLine();
            }
        }
    }

    private static void Write(
        InstallPlan plan,
        List<InstallChoice> choices,
        InstallResult? result,
        bool applied,
        bool listFiles)
    {
        Console.Out.WriteLine($"Source  {PathDisplay.Show(plan.Source)}{(plan.IsArchive ? "  (archive)" : string.Empty)}");
        Console.Out.WriteLine($"Mods    {PathDisplay.Show(plan.ModsDirectory)}");
        Console.Out.WriteLine();

        if (!plan.HasCandidates)
        {
            Console.Out.WriteLine("Nothing in it looks like a mod.");
            WriteDiagnostics(plan);
            return;
        }

        Console.Out.WriteLine($"{EnglishCount.Plural(plan.Candidates.Count, "mod", "mods")} found:");
        Console.Out.WriteLine();

        foreach (var candidate in plan.Candidates)
        {
            var selected = choices.Any(choice =>
                ReferenceEquals(choice.Candidate, candidate));

            Console.Out.WriteLine(
                $"  [{(selected ? "x" : " ")}] {candidate.Name}  ->  {candidate.SuggestedFolderName}/");

            Console.Out.WriteLine($"        {candidate.Reason}");

            Console.Out.WriteLine(
                $"        {EnglishCount.Plural(candidate.Learned.Hashes.Count, "hash", "hashes")}, " +
                $"{EnglishCount.Plural(candidate.FileCount, "file", "files")}, " +
                $"{CliOutput.Bytes(candidate.Bytes)}" +
                (candidate.PreviewPath is null ? string.Empty : ", has a preview image"));

            if (candidate.VariantIsUncertain)
            {
                Console.Out.WriteLine("        the outfit is a guess, not a hash match");
            }

            if (candidate.RelativePath is { Length: > 0 } relative)
            {
                Console.Out.WriteLine($"        at {PathDisplay.Show(relative)}");
            }

            if (listFiles)
            {
                foreach (var file in candidate.Files)
                {
                    Console.Out.WriteLine($"          {PathDisplay.Show(file)}");
                }

                if (candidate.FileListIsTruncated)
                {
                    Console.Out.WriteLine(
                        $"          … and {EnglishCount.Plural(candidate.FileCount - candidate.Files.Count, "file", "files")} more");
                }
            }

            Console.Out.WriteLine();
        }

        if (plan.StrandedFiles.Count > 0)
        {
            Console.Out.WriteLine(
                $"{EnglishCount.Plural(plan.StrandedFiles.Count, "file", "files")} in the source belong to no " +
                "mod and will not be installed:");

            foreach (var file in plan.StrandedFiles)
            {
                Console.Out.WriteLine($"  {PathDisplay.Show(file)}");
            }

            Console.Out.WriteLine();
        }

        WriteDiagnostics(plan);

        if (result is null)
        {
            Console.Out.WriteLine(
                applied
                    ? "Nothing was selected, so nothing was installed."
                    : $"Dry run — nothing was written. Add --apply to install " +
                      $"{EnglishCount.Plural(choices.Count, "mod", "mods")}.");

            return;
        }

        Console.Out.WriteLine($"Installed {EnglishCount.Plural(result.InstalledCount, "mod", "mods")}.");

        foreach (var outcome in result.Outcomes.Where(outcome => outcome.Succeeded))
        {
            Console.Out.WriteLine($"  {outcome.DestinationFolderName}/{outcome.Result!.ToName}");
        }

        foreach (var failure in result.Failures)
        {
            CliOutput.WriteError($"  failed: {failure.Choice.Candidate.Name} — {failure.Error}");
        }

        // Installed, but the metadata did not reach the disk: its own line, not a failure.
        foreach (var partial in result.MetadataFailures)
        {
            CliOutput.WriteError(
                $"  installed, but its details could not be saved: " +
                $"{partial.Choice.Candidate.Name} — {partial.MetadataError}");
        }
    }

    private static void WriteDiagnostics(InstallPlan plan)
    {
        foreach (var diagnostic in plan.Diagnostics)
        {
            Console.Out.WriteLine($"note: {diagnostic.Message}");
        }

        if (plan.Diagnostics.Count > 0)
        {
            Console.Out.WriteLine();
        }
    }

    private static ModAddReport Describe(
        InstallPlan plan, IReadOnlyList<InstallChoice> choices, InstallResult? result, bool applied) => new(
        plan.Source,
        plan.IsArchive,
        plan.ModsDirectory,
        applied && result is not null,
        [
            .. plan.Candidates.Select(candidate => new InstallCandidateReport(
                candidate.Name,
                candidate.RelativePath,
                candidate.SuggestedVariantId,
                candidate.SuggestedFolderName,
                candidate.Reason,
                candidate.Learned.Hashes.Count,
                candidate.FileCount,
                candidate.Bytes,
                candidate.WasIdentified,
                candidate.VariantIsUncertain,
                candidate.PreviewPath,
                choices.Any(choice => ReferenceEquals(choice.Candidate, candidate)),
                candidate.Files)),
        ],
        plan.StrandedFiles,
        [
            .. (result?.Outcomes ?? []).Select(outcome => new InstallOutcomeReport(
                outcome.Choice.Candidate.Name,
                outcome.DestinationFolderName,
                outcome.InstalledPath,
                outcome.Succeeded,
                outcome.Error,
                outcome.MetadataError)),
        ],
        [.. plan.Diagnostics.Select(diagnostic => diagnostic.Message)]);
}
