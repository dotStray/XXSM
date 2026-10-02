using Avalonia;
using Avalonia.Media;

namespace Xxsm.Desktop;

/// <summary>The desktop application entry point.</summary>
internal static class Program
{
    /// <summary>The window class on Linux, which the desktop entry's <c>StartupWMClass</c> names.</summary>
    public const string WindowClass = "xxsm";

    /// <summary>The font bundled with the app, used when the system has none.</summary>
    public const string BundledFont = "fonts:Inter#Inter";

    /// <summary>Starts the Avalonia application.</summary>
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>Builds the Avalonia application; public and parameterless for the headless test host.</summary>
    /// <remarks>An explicit Skia setup in place of <c>UsePlatformDetect</c> must also call
    /// <c>UseHarfBuzz()</c>.</remarks>
    /// <returns>The configured builder.</returns>
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new X11PlatformOptions { WmClass = WindowClass })
            .WithInterFont()
            .LogToTrace();

        return FontOptionsFor(SkiaSharp.SKTypeface.Default.FamilyName) is { } fonts ? builder.With(fonts) : builder;
    }

    /// <summary>The bundled font as the default when the system reports none; null keeps the system's.</summary>
    /// <param name="systemDefault">The system's default font family, empty when it has no fonts at all.</param>
    public static FontManagerOptions? FontOptionsFor(string? systemDefault) =>
        string.IsNullOrWhiteSpace(systemDefault) ? new FontManagerOptions { DefaultFamilyName = BundledFont } : null;
}
