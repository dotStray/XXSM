using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Xxsm.Cli.Output;
using Xxsm.Core.Io;
using Xxsm.Packs.Installation;

namespace Xxsm.Cli.Commands;

/// <summary><c>xxsm pack use | remove | restore</c>: the installed versions of a game's pack, by hand.</summary>
internal static class PackInstalledCommands
{
    /// <summary>Builds <c>xxsm pack use</c>.</summary>
    public static Command CreateUse()
    {
        var game = new Argument<string>("game") { Description = "The game id." };
        var version = new Argument<string>("version") { Description = "An installed version of its pack." };

        var command = new Command(
            "use",
            "Make one installed version of a game's pack the one in use. The newest follows updates; an older one is held there.")
        {
            game,
            version,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var gameId = parse.GetValue(game)!;
            var wanted = parse.GetValue(version)!;

            var preference = await provider.GetRequiredService<IInstalledPacks>()
                .UseVersionAsync(gameId, wanted, cancellationToken)
                .ConfigureAwait(false);

            var message = preference.PinnedVersion is { Length: > 0 } pinned
                ? $"{gameId} now uses {pinned}, held there: updates leave it alone until you choose another."
                : $"{gameId} now uses {wanted}, the newest installed, and follows updates.";

            Console.Out.WriteLine(parse.GetValue(GlobalOptions.Json)
                ? CliJson.Serialize(new PackUseReport(gameId, preference.PinnedVersion ?? wanted, preference.PinnedVersion, message))
                : message);

            return 0;
        });

        return command;
    }

    /// <summary>Builds <c>xxsm pack remove</c>.</summary>
    public static Command CreateRemove()
    {
        var game = new Argument<string>("game") { Description = "The game id." };

        var version = new Option<string?>("--version")
        {
            Description = "Remove this one installed version only. Without it, every version goes.",
        };

        var withCorrections = new Option<bool>("--with-corrections")
        {
            Description = "Also remove your own corrections and characters for the game. Kept otherwise.",
        };

        var command = new Command(
            "remove",
            "Move a game's installed pack, or one version of it, to the trash. 'xxsm pack restore' puts it back.")
        {
            game,
            version,
            withCorrections,
        };

        command.Validators.Add(result =>
        {
            if (result.GetValue(version) is { Length: > 0 } && result.GetValue(withCorrections))
            {
                result.AddError("--with-corrections goes with removing the whole pack, not one version of it.");
            }
        });

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var installed = provider.GetRequiredService<IInstalledPacks>();
            var gameId = parse.GetValue(game)!;

            var removal = parse.GetValue(version) is { Length: > 0 } one
                ? await installed.RemoveVersionAsync(gameId, one, cancellationToken).ConfigureAwait(false)
                : await installed.RemoveAsync(gameId, parse.GetValue(withCorrections), cancellationToken).ConfigureAwait(false);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(new PackRemovalReport(
                    removal.GameId,
                    removal.PackVersion,
                    removal.CorrectionsRemoved,
                    removal.PinnedVersion,
                    [.. removal.Trashed.Select(t => new PackTrashedReport(t.OriginalPath, t.TrashedPath))],
                    removal.RecordPath));
            }

            CliOutput.WriteRows(
            [
                ("Removed", removal.PackVersion is { Length: > 0 } v ? $"{gameId} {v}" : $"{gameId}, every version"),
                ("Corrections", removal.CorrectionsRemoved ? "moved to the trash too" : "kept"),
                .. removal.Trashed.Select(t => ("Now at", PathDisplay.Show(t.TrashedPath))),
                ("Undo with", removal.RecordPath is { Length: > 0 } record
                    ? $"xxsm pack restore \"{record}\""
                    : "the trash: the record of this removal could not be written (see the log)"),
            ]);

            return 0;
        });

        return command;
    }

    /// <summary>Builds <c>xxsm pack keep</c>: the Game Packs page's <em>Keep</em> box.</summary>
    public static Command CreateKeep()
    {
        var game = new Argument<string>("game") { Description = "The game id." };
        var version = new Argument<string>("version") { Description = "An installed version of its pack." };
        var off = new Option<bool>("--off") { Description = "Take the mark off, so the version goes once it is old." };

        var command = new Command(
            "keep",
            "Mark an installed version to keep, so it is never removed for being old. 'xxsm pack prune' removes the rest.")
        {
            game,
            version,
            off,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var gameId = parse.GetValue(game)!;
            var wanted = parse.GetValue(version)!;
            var keep = !parse.GetValue(off);

            var preference = await provider.GetRequiredService<IInstalledPacks>()
                .KeepVersionAsync(gameId, wanted, keep, cancellationToken)
                .ConfigureAwait(false);

            var message = keep
                ? $"{gameId} {wanted} is kept: it is never removed for being old."
                : $"{gameId} {wanted} is no longer kept: it goes to the trash once it is old.";

            Console.Out.WriteLine(parse.GetValue(GlobalOptions.Json)
                ? CliJson.Serialize(new PackKeepReport(gameId, wanted, keep, preference.KeptVersions, message))
                : message);

            return 0;
        });

        return command;
    }

    /// <summary>Builds <c>xxsm pack prune</c>: what the app does at start-up and after an install.</summary>
    public static Command CreatePrune()
    {
        var game = new Option<string?>("--game") { Description = "Only this game. Without it, every game." };
        var dryRun = new Option<bool>("--dry-run") { Description = "List the versions that would go, and move nothing." };

        var command = new Command(
            "prune",
            "Move old installed versions to the trash: those a newer version replaced more than the days in packs.json " +
            "(removeOldVersionsAfterDays, 30 unless set; 0 keeps them all) ago. The version in use, the newest and kept ones stay. " +
            "Without --game, also the folders an install cut off by a crash left behind, untouched for an hour.")
        {
            game,
            dryRun,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var installed = provider.GetRequiredService<IInstalledPacks>();
            var gameId = parse.GetValue(game);

            List<PackPruneItemReport> items;
            List<PackLeftoverReport> leftovers = [];

            if (parse.GetValue(dryRun))
            {
                var due = await installed.FindOldVersionsAsync(gameId, cancellationToken).ConfigureAwait(false);
                items = [.. due.Select(old => new PackPruneItemReport(old.GameId, old.PackVersion, "wouldRemove", null, null))];

                if (gameId is null)
                {
                    leftovers = [.. installed.FindLeftovers().Select(path => new PackLeftoverReport(path, "wouldRemove", null))];
                }
            }
            else
            {
                var result = await installed.RemoveOldVersionsAsync(gameId, cancellationToken).ConfigureAwait(false);
                items =
                [
                    .. result.Removed.Select(r => new PackPruneItemReport(r.GameId, r.PackVersion ?? "", "removed", r.RecordPath, null)),
                    .. result.Failed.Select(f => new PackPruneItemReport(f.GameId, f.PackVersion, "failed", null, f.Message)),
                ];

                if (gameId is null)
                {
                    var swept = await installed.RemoveLeftoversAsync(cancellationToken).ConfigureAwait(false);
                    leftovers =
                    [
                        .. swept.Removed.Select(path => new PackLeftoverReport(path, "removed", null)),
                        .. swept.Failed.Select(f => new PackLeftoverReport(f.Path, "failed", f.Message)),
                    ];
                }
            }

            var failures = items.Count(i => i.Status == "failed") + leftovers.Count(l => l.Status == "failed");

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(new PackPruneReport(parse.GetValue(dryRun), items, leftovers), failures == 0 ? 0 : 1);
            }

            foreach (var leftover in leftovers)
            {
                Console.Out.WriteLine(leftover.Status switch
                {
                    "wouldRemove" => $"  would remove  {PathDisplay.Show(leftover.Path)}  (left by an interrupted install)",
                    "removed" => $"  removed       {PathDisplay.Show(leftover.Path)}  (left by an interrupted install; it is in the trash)",
                    _ => $"  NOT REMOVED   {PathDisplay.Show(leftover.Path)}: {leftover.Message}",
                });
            }

            if (items.Count == 0)
            {
                Console.Out.WriteLine("No installed version is old enough to go.");
                return failures == 0 ? 0 : 1;
            }

            foreach (var item in items)
            {
                Console.Out.WriteLine(item.Status switch
                {
                    "wouldRemove" => $"  would remove  {item.GameId} {item.PackVersion}",
                    "removed" => $"  removed       {item.GameId} {item.PackVersion}" +
                                 (item.RecordPath is { Length: > 0 } record ? $"  (undo: xxsm pack restore \"{record}\")" : ""),
                    _ => $"  NOT REMOVED   {item.GameId} {item.PackVersion}: {item.Message}",
                });
            }

            return failures == 0 ? 0 : 1;
        });

        return command;
    }

    /// <summary>Builds <c>xxsm pack restore</c>.</summary>
    public static Command CreateRestore()
    {
        var record = new Argument<string>("record")
        {
            Description = "The record that 'xxsm pack remove' reported.",
        };

        var command = new Command(
            "restore", "Put back a pack removed with 'xxsm pack remove'. Never goes over anything installed since.")
        {
            record,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var installed = provider.GetRequiredService<IInstalledPacks>();

            var removal = await installed.ReadRemovalAsync(parse.GetValue(record)!, cancellationToken).ConfigureAwait(false);
            var result = await installed.RestoreAsync(removal, cancellationToken).ConfigureAwait(false);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(new PackRestoreReport(
                    result.GameId,
                    result.Restored,
                    [.. result.Skipped.Select(s => new PackRestoreSkipReport(s.Path, s.Reason))],
                    result.IsComplete), result.IsComplete ? 0 : 1);
            }

            CliOutput.WriteRows(
            [
                .. result.Restored.Select(path => ("Restored", path)),
                .. result.Skipped.Select(skip => ("Left in the trash", $"{PathDisplay.Show(skip.Path)}: {skip.Reason}")),
            ]);

            return result.IsComplete ? 0 : 1;
        });

        return command;
    }
}
