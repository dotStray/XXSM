using Avalonia;

namespace Xxsm.Desktop;

/// <summary>The desktop application entry point.</summary>
internal static class Program
{
    /// <summary>The window class on Linux, which the desktop entry's <c>StartupWMClass</c> names.</summary>
    public const string WindowClass = "xxsm";

    /// <summary>Starts the Avalonia application.</summary>
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>Builds the Avalonia application; public and parameterless for the headless test host.</summary>
    /// <remarks>An explicit Skia setup in place of <c>UsePlatformDetect</c> must also call
    /// <c>UseHarfBuzz()</c>.</remarks>
    /// <returns>The configured builder.</returns>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new X11PlatformOptions { WmClass = WindowClass })
            .WithInterFont()
            .LogToTrace();
}
