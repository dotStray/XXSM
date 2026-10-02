using System.CommandLine;

namespace Xxsm.Cli.Commands;

/// <summary>Options every command understands.</summary>
internal static class GlobalOptions
{
    /// <summary>Emit machine-readable JSON on standard out instead of a human table.</summary>
    public static Option<bool> Json { get; } =
        new("--json") { Description = "Emit machine-readable JSON on standard output.", Recursive = true };

    /// <summary>Raise console log verbosity. Logs always go to standard error.</summary>
    public static Option<bool> Verbose { get; } =
        new("--verbose", "-v")
        {
            Description = "Write debug-level logs to standard error.",
            Recursive = true,
        };
}
