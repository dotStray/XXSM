using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;
using Xxsm.Core.Hosting;
using Xxsm.Core.Io;
using Xxsm.Packs.Hosting;

namespace Xxsm.Cli;

/// <summary>Builds the CLI's service container, from the same Core and Packs services as the app.</summary>
internal static class CliHost
{
    /// <summary>Builds a provider. The caller owns it and must dispose it, which also flushes the log.</summary>
    /// <param name="verbose">Whether to write debug-level logs to standard error.</param>
    public static ServiceProvider Build(bool verbose)
    {
        var paths = new AppPaths();
        paths.EnsureCreated();

        // Logs go to standard error, so --json output on standard out stays clean.
        var logger = XxsmLogging.Create(
            paths,
            minimumLevel: verbose ? LogEventLevel.Debug : LogEventLevel.Information,
            consoleLevel: verbose ? LogEventLevel.Debug : LogEventLevel.Warning);

        XxsmLogging.RecordCrashes(logger);

        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(paths);
        services.AddSingleton<ILogger>(logger);
        services.AddSingleton(logger);
        services.AddXxsmCore();
        services.AddXxsmPacks();

        return services.BuildServiceProvider(validateScopes: true);
    }
}
