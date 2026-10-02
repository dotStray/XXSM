using System.CommandLine;
using Xxsm.Cli.Commands;
using Xxsm.Core;

namespace Xxsm.Cli;

/// <summary>The <c>xxsm</c> command-line entry point.</summary>
internal static class Program
{
    /// <summary>The exit code for an operation XXSM refused or could not complete.</summary>
    private const int FailureExitCode = 1;

    /// <summary>The conventional exit code for a run stopped with Ctrl-C.</summary>
    private const int CancelledExitCode = 130;

    /// <summary>Parses the command line and runs the selected command.</summary>
    /// <remarks>An <see cref="XxsmException"/> prints its message alone; the stack trace only under
    /// <c>--verbose</c>.</remarks>
    /// <param name="args">Raw process arguments.</param>
    /// <returns>The process exit code. 0 on success.</returns>
    private static async Task<int> Main(string[] args)
    {
        var parse = RootCommandFactory.Create().Parse(args);

        // Off on purpose: the default handler prints a stack trace over the one line the user needs.
        var invocation = new InvocationConfiguration { EnableDefaultExceptionHandler = false };

        try
        {
            return await parse.InvokeAsync(invocation).ConfigureAwait(false);
        }
        catch (XxsmException ex)
        {
            // Standard error, so --json output on standard out stays clean.
            Console.Error.WriteLine(ex.Message);

            if (ex is Xxsm.Core.GameBanana.GameBananaFileChoiceException)
            {
                Console.Error.WriteLine("Choose one with --file <id>.");
            }

            if (parse.GetValue(GlobalOptions.Verbose))
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine(ex);
            }

            return FailureExitCode;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled. Nothing further has been changed.");
            return CancelledExitCode;
        }
    }
}
