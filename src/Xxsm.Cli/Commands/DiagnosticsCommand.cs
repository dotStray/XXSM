using System.CommandLine;
using System.Runtime.InteropServices;
using Xxsm.Cli.Output;
using Xxsm.Core;
using Xxsm.Core.Io;

namespace Xxsm.Cli.Commands;

/// <summary><c>xxsm doctor</c>: where XXSM reads and writes, and what it detected. Touches nothing.</summary>
internal static class DiagnosticsCommand
{
    /// <summary>Builds the command.</summary>
    public static Command Create()
    {
        var command = new Command("doctor", "Show resolved paths and environment detection.");

        command.SetAction(parse =>
        {
            var json = parse.GetValue(GlobalOptions.Json);
            var report = Build();

            if (json)
            {
                Console.Out.WriteLine(CliJson.Serialize(report));
            }
            else
            {
                WriteHuman(report);
            }

            return 0;
        });

        return command;
    }

    private static DiagnosticsReport Build()
    {
        var paths = new AppPaths();
        var volumes = new DriveInfoVolumeResolver();
        var userIds = new ProcUserIdProvider();

        var homeVolume = volumes.GetVolumeRoot(paths.HomeTrashDirectory);

        return new DiagnosticsReport(
            AppInfo.Version,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription.Trim(),
            RuntimeInformation.RuntimeIdentifier,
            paths.ConfigDirectory,
            paths.DataDirectory,
            paths.CacheDirectory,
            paths.StateDirectory,
            paths.LogsDirectory,
            paths.PacksDirectory,
            paths.OverlaysDirectory,
            paths.StudioDirectory,
            paths.SettingsFile,
            paths.HomeTrashDirectory,
            homeVolume,
            userIds.TryGetUserId(),
            AppInfo.DefaultRegistryUrl);
    }

    private static void WriteHuman(DiagnosticsReport r)
    {
        var rows = new (string Label, string Value)[]
        {
            ("XXSM version", r.Version),
            ("Runtime", r.Runtime),
            ("OS", r.OperatingSystem),
            ("Runtime identifier", r.RuntimeIdentifier),
            (string.Empty, string.Empty),
            ("Config", PathDisplay.Show(r.ConfigDirectory)),
            ("Data", PathDisplay.Show(r.DataDirectory)),
            ("Cache", PathDisplay.Show(r.CacheDirectory)),
            ("State", PathDisplay.Show(r.StateDirectory)),
            ("Logs", PathDisplay.Show(r.LogsDirectory)),
            (string.Empty, string.Empty),
            ("Packs", PathDisplay.Show(r.PacksDirectory)),
            ("Overlays", PathDisplay.Show(r.OverlaysDirectory)),
            ("Studio drafts", PathDisplay.Show(r.StudioDirectory)),
            ("Settings file", PathDisplay.Show(r.SettingsFile)),
            (string.Empty, string.Empty),
            ("Trash", PathDisplay.Show(r.HomeTrashDirectory)),
            ("Home volume", PathDisplay.Show(r.HomeVolumeRoot)),
            ("User id", r.UserId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(unknown)"),
            (string.Empty, string.Empty),
            ("Default registry", r.DefaultRegistryUrl),
        };

        var width = rows.Max(row => row.Label.Length);

        foreach (var (label, value) in rows)
        {
            if (label.Length == 0)
            {
                Console.Out.WriteLine();
                continue;
            }

            Console.Out.WriteLine($"{label.PadRight(width)}  {value}");
        }
    }
}
