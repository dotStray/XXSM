using System.CommandLine;
using Xxsm.Core.Text;

namespace Xxsm.Cli.Commands;

/// <summary>Builds the <c>xxsm</c> command tree: the one place that knows its whole shape.</summary>
internal static class RootCommandFactory
{
    /// <summary>Creates the fully-populated root command.</summary>
    public static RootCommand Create()
    {
        var root = new RootCommand("XXSM — mod manager for 3DMigoto-based anime games.");

        root.Options.Add(GlobalOptions.Json);
        root.Options.Add(GlobalOptions.Verbose);

        root.Subcommands.Add(ConfigCommand.Create());
        root.Subcommands.Add(PackCommand.Create());
        root.Subcommands.Add(ScanCommand.Create());
        root.Subcommands.Add(IniCommand.Create());
        root.Subcommands.Add(ModCommand.Create());
        root.Subcommands.Add(CharacterCommand.Create());
        root.Subcommands.Add(StudioCommand.Create());
        root.Subcommands.Add(SortCommand.Create());
        root.Subcommands.Add(ProfileCommand.Create());
        root.Subcommands.Add(SwitchesCommand.Create());
        if (TextEditing.ForThisBuild.IsEnabled)
        {
            root.Subcommands.Add(TextCommand.Create());
        }

        root.Subcommands.Add(DiagnosticsCommand.Create());
        root.Subcommands.Add(VersionCommand.Create());

        return root;
    }
}
