using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Xxsm.Cli.Output;
using Xxsm.Core;
using Xxsm.Packs.Updates;

namespace Xxsm.Cli.Commands;

/// <summary><c>xxsm version</c>: the running version, and with <c>--check</c> whether a newer one is out.</summary>
internal static class VersionCommand
{
    /// <summary>Builds the command.</summary>
    public static Command Create()
    {
        var check = new Option<bool>("--check")
        {
            Description = "Ask GitHub whether a newer XXSM is out. Nothing is downloaded; the release page is printed.",
        };

        var command = new Command("version", "Show XXSM's version, and whether a newer one is out.") { check };

        command.SetAction(async (parse, cancellationToken) =>
        {
            AppUpdateResult? result = null;

            if (parse.GetValue(check))
            {
                await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
                result = await provider.GetRequiredService<IAppUpdateChecker>()
                    .CheckAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            var report = new VersionReport(
                AppInfo.ShortVersion, result?.Latest, result?.IsNewer, result?.Page.AbsoluteUri);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report);
            }

            Console.Out.WriteLine($"XXSM {report.Version}");

            if (result is not null)
            {
                Console.Out.WriteLine(result.IsNewer
                    ? $"XXSM {result.Latest} is out. Download it from {report.Page}"
                    : $"This is the newest: {result.Latest} is the latest release.");
            }

            return 0;
        });

        return command;
    }
}
