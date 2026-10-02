using System.ComponentModel;
using System.Diagnostics;
using Xxsm.Core.Io;

namespace Xxsm.Core.Hosting;

/// <summary>Starts a new copy of the running program with its arguments and environment.</summary>
public interface IAppRelauncher
{
    /// <summary>Starts the new copy. The caller exits afterwards.</summary>
    /// <exception cref="ModOperationException">The program could not be found or started, with the operating system's
    /// own words.</exception>
    void StartAgain();
}

/// <summary>The default <see cref="IAppRelauncher"/>, over the current process.</summary>
public sealed class ProcessRelauncher : IAppRelauncher
{
    /// <inheritdoc />
    public void StartAgain()
    {
        var start = StartInfoFor(
            Environment.ProcessPath, Environment.GetCommandLineArgs(), Environment.GetEnvironmentVariable("APPIMAGE"));

        try
        {
            using var process = Process.Start(start);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            throw new ModOperationException(
                $"XXSM could not start itself again from '{PathDisplay.Show(start.FileName)}': {ex.Message} Open it again by hand.",
                start.FileName,
                ex);
        }
    }

    /// <summary>Works out how to start the program again.</summary>
    /// <param name="processPath">The running executable, as <see cref="Environment.ProcessPath"/> gives it.</param>
    /// <param name="commandLine">The command line, as <see cref="Environment.GetCommandLineArgs"/> gives it.</param>
    /// <param name="appImage">The AppImage file the program runs from (its <c>APPIMAGE</c> variable), or null. The
    /// running executable is then inside a mount that goes away with this process, so the AppImage is started.</param>
    /// <exception cref="ModOperationException">The running executable is not known.</exception>
    public static ProcessStartInfo StartInfoFor(string? processPath, IReadOnlyList<string> commandLine, string? appImage)
    {
        ArgumentNullException.ThrowIfNull(commandLine);

        if (!string.IsNullOrWhiteSpace(appImage))
        {
            processPath = appImage;
        }

        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new ModOperationException(
                "XXSM could not start itself again: the operating system did not say which program is running. Open it again by hand.");
        }

        var start = new ProcessStartInfo(processPath) { UseShellExecute = false };

        var isHost = string.Equals(
            Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);

        foreach (var argument in commandLine.Skip(isHost ? 0 : 1))
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }
}
