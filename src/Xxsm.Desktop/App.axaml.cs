using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Xxsm.Core;
using Xxsm.Core.Hosting;
using Xxsm.Core.Io;
using Xxsm.Core.Settings;
using Xxsm.Core.Text;
using Xxsm.Desktop.ViewModels;
using Xxsm.Desktop.Views;

namespace Xxsm.Desktop;

/// <summary>The Avalonia application object.</summary>
public partial class App : Application
{
    private readonly Stopwatch _startup = Stopwatch.StartNew();

    private ServiceProvider? _services;
    private MainWindow? _window;
    private bool _studioFlushed;

    /// <inheritdoc />
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        TextCatalogue.PublishShipped(Resources);
    }

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = DesktopHost.Build(this, () => _window, Restart);

            // A scheduled reset runs before anything reads a file it is about to move.
            var reset = _services.GetRequiredService<IFactoryReset>();

            if (reset.IsScheduled)
            {
                _ = ResetThenStartAsync(desktop, _services, reset);
            }
            else
            {
                StartShell(desktop, _services);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Builds the window and starts the shell.</summary>
    private MainWindowViewModel StartShell(IClassicDesktopStyleApplicationLifetime desktop, ServiceProvider services)
    {
        services.GetRequiredService<TextCatalogue>().PublishTo(Resources);

        var shell = services.GetRequiredService<MainWindowViewModel>();

        _window = new MainWindow { DataContext = shell };
        desktop.MainWindow = _window;
        _window.Closing += OnWindowClosing;

        // The window is shown before this completes: the disk work comes after the first frame.
        shell.Activate();
        _ = StartAsync(shell);

        desktop.ShutdownRequested += (_, e) => OnShutdownRequested(desktop, shell, e);

        return shell;
    }

    /// <summary>Carries out a scheduled reset, then starts as though for the first time.</summary>
    private async Task ResetThenStartAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        ServiceProvider services,
        IFactoryReset reset)
    {
        FactoryResetResult? result = null;
        string? problem = null;
        var logger = services.GetService<ILogger>()?.ForContext<App>();

        try
        {
            result = await reset.RunAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (XxsmException ex)
        {
            logger?.Error(ex, "The reset could not run");
            problem = ex.Message;
        }
        catch (Exception ex)
        {
            // The window opens with the words rather than XXSM running unseen.
            logger?.Fatal(ex, "The reset failed on an error nothing expected");
            problem = ex.Message;
        }

        try
        {
            // The reset may have taken the directories start-up made.
            services.GetRequiredService<IAppPaths>().EnsureCreated();

            var shell = StartShell(desktop, services);
            shell.ReportReset(result, problem);

            _window?.Show();
        }
        catch (Exception ex)
        {
            // No window can be shown, so nothing may be left running without one.
            logger?.Fatal(ex, "XXSM could not open its window after the reset");
            await Console.Error.WriteLineAsync($"XXSM could not open its window after the reset: {ex.Message}")
                .ConfigureAwait(true);
            desktop.Shutdown(1);
        }
    }

    /// <summary>Closes the window and opens XXSM again, after Studio's last save and every service's release.</summary>
    private void Restart() => _ = RestartGuardedAsync();

    /// <summary>The restart, logging anything unexpected and ending the process rather than running unseen.</summary>
    private async Task RestartGuardedAsync()
    {
        try
        {
            await RestartAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // The log may already be closed by the restart; the terminal is the one place left.
            await Console.Error.WriteLineAsync($"XXSM could not restart: {ex}").ConfigureAwait(true);

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown(1);
            }
        }
    }

    private async Task RestartAsync()
    {
        if (_services is not { } services
            || _window?.DataContext is not MainWindowViewModel shell
            || ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        var relauncher = services.GetRequiredService<IAppRelauncher>();
        services.GetService<ILogger>()?.ForContext<App>().Information("Restarting XXSM");

        await FlushStudioAsync(shell).ConfigureAwait(true);
        Shutdown(shell);

        try
        {
            relauncher.StartAgain();
        }
        catch (ModOperationException ex)
        {
            // The log is closed; the reset is still scheduled, so opening XXSM by hand finishes it.
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(true);
        }

        desktop.Shutdown();
    }

    /// <summary>Loads the shell and logs how long the whole start-up took.</summary>
    private async Task StartAsync(MainWindowViewModel shell)
    {
        try
        {
            await shell.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Started and not awaited, so logged here: the window stays up.
            _services?.GetService<ILogger>()?.ForContext<App>().Error(ex, "Loading the window's contents failed");
            return;
        }

        _services?.GetService<ILogger>()?
            .ForContext<App>()
            .Information(
                "Shell ready in {ElapsedMs} ms", _startup.ElapsedMilliseconds);

        await WriteTextReferenceAsync().ConfigureAwait(true);
    }

    /// <summary>Rewrites <c>text.reference.json</c> after the shell is up; a failure is logged and dropped.</summary>
    private async Task WriteTextReferenceAsync()
    {
        if (_services is null)
        {
            return;
        }

        var logger = _services.GetService<ILogger>()?.ForContext<App>();

        try
        {
            await _services.GetRequiredService<ITextOverrideStore>()
                .WriteReferenceAsync(_services.GetRequiredService<ITextCatalogue>().BuiltIn)
                .ConfigureAwait(true);
        }
        catch (XxsmException ex)
        {
            logger?.Warning(ex, "Could not write the interface-text reference file");
        }
    }

    /// <summary>Holds the window open until Pack Studio has written its last change.</summary>
    private async void OnWindowClosing(object? sender, Avalonia.Controls.WindowClosingEventArgs e)
    {
        if (_studioFlushed
            || sender is not Avalonia.Controls.Window { DataContext: MainWindowViewModel shell } window
            || !shell.HasUnsavedWork)
        {
            return;
        }

        e.Cancel = true;
        await FlushStudioAsync(shell).ConfigureAwait(true);
        window.Close();
    }

    /// <summary>The same, when the session is ended without the window being closed first.</summary>
    private async void OnShutdownRequested(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindowViewModel shell,
        ShutdownRequestedEventArgs e)
    {
        if (_studioFlushed || !shell.HasUnsavedWork)
        {
            Shutdown(shell);
            return;
        }

        e.Cancel = true;
        await FlushStudioAsync(shell).ConfigureAwait(true);
        Shutdown(shell);
        desktop.Shutdown();
    }

    /// <summary>Waits for Studio's save, once; a failed save is logged and shown in its header.</summary>
    private async Task FlushStudioAsync(MainWindowViewModel shell)
    {
        try
        {
            await shell.PrepareToCloseAsync(CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            _studioFlushed = true;
        }
    }

    /// <summary>Releases everything the shell holds, the Mods folder watch and the log's buffer among them.</summary>
    private void Shutdown(MainWindowViewModel shell)
    {
        shell.Deactivate();
        _services?.Dispose();
        _services = null;
    }
}
