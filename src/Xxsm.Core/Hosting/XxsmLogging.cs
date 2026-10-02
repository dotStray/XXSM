using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xxsm.Core.Io;

namespace Xxsm.Core.Hosting;

/// <summary>Configures Serilog the same way for the CLI and the desktop app.</summary>
public static class XxsmLogging
{
    /// <summary>The rolling log file name pattern under <see cref="IAppPaths.LogsDirectory"/>.</summary>
    public const string LogFileName = "xxsm-.log";

    private const string FileTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}";

    private const string ConsoleTemplate = "[{Level:u3}] {Message:lj}{NewLine}{Exception}";

    /// <summary>Builds the logger: a daily file in the logs directory, and a console sink if asked.</summary>
    /// <param name="paths">Supplies the logs directory.</param>
    /// <param name="minimumLevel">The floor for what is written to the file.</param>
    /// <param name="consoleLevel">The floor for the console sink, written to standard error; null for none.</param>
    /// <returns>The logger. The caller disposes it.</returns>
    public static Logger Create(
        IAppPaths paths,
        LogEventLevel minimumLevel = LogEventLevel.Information,
        LogEventLevel? consoleLevel = LogEventLevel.Warning)
    {
        ArgumentNullException.ThrowIfNull(paths);

        Directory.CreateDirectory(paths.LogsDirectory);

        var configuration = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .Enrich.WithProperty("Version", AppInfo.Version)
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, LogFileName),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                outputTemplate: FileTemplate);

        if (consoleLevel is { } level)
        {
            configuration = configuration.WriteTo.Console(
                restrictedToMinimumLevel: level,
                outputTemplate: ConsoleTemplate,
                standardErrorFromLevel: LogEventLevel.Verbose);
        }

        return configuration.CreateLogger();
    }

    /// <summary>Logs an uncaught error at <c>Fatal</c> with its stack trace, then closes the logger.</summary>
    /// <param name="logger">The application logger. Closed by this call.</param>
    /// <param name="error">What was thrown: usually an <see cref="Exception"/>, but native code can throw
    /// anything.</param>
    public static void RecordCrash(Logger logger, object error)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (error is Exception exception)
        {
            logger.Fatal(exception, "XXSM stopped on an error nothing handled");
        }
        else
        {
            logger.Fatal("XXSM stopped on an error nothing handled: {Error}", error);
        }

        logger.Dispose();
    }

    /// <summary>Logs every crash in this process, and every failed background task nothing awaited.</summary>
    /// <param name="logger">The application logger.</param>
    /// <returns>The recorder, for a host with another place errors surface.</returns>
    public static CrashRecorder RecordCrashes(Logger logger)
    {
        var recorder = new CrashRecorder(logger);

        AppDomain.CurrentDomain.UnhandledException += (_, e) => recorder.RecordCrash(e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => recorder.RecordUnobserved(e);

        return recorder;
    }
}

/// <summary>Writes down what nothing else caught: a crash once, and each background failure as it is found.</summary>
public sealed class CrashRecorder
{
    private readonly Logger _logger;
    private int _crashed;

    /// <summary>Creates the recorder.</summary>
    /// <param name="logger">The application logger. Closed by the first crash recorded.</param>
    public CrashRecorder(Logger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
    }

    /// <summary>Records the error the process is about to end on, once, then closes the log.</summary>
    /// <param name="error">What was thrown: usually an <see cref="Exception"/>, but native code can throw
    /// anything.</param>
    public void RecordCrash(object error)
    {
        if (Interlocked.Exchange(ref _crashed, 1) != 0)
        {
            return;
        }

        XxsmLogging.RecordCrash(_logger, error);
    }

    /// <summary>Records a failed background task nothing awaited, and marks it observed. The log stays open.</summary>
    /// <param name="args">What the runtime reported.</param>
    public void RecordUnobserved(UnobservedTaskExceptionEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (Volatile.Read(ref _crashed) == 0)
        {
            _logger.Error(args.Exception, "A background task failed and nothing was waiting for it");
        }

        args.SetObserved();
    }
}
