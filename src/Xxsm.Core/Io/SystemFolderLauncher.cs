using System.ComponentModel;
using System.Diagnostics;
using Serilog;

namespace Xxsm.Core.Io;

/// <summary>The default <see cref="IFolderLauncher"/>: hands the path to the desktop environment.</summary>
public sealed class SystemFolderLauncher(ILogger logger) : IFolderLauncher
{
    private readonly ILogger _logger = logger.ForContext<SystemFolderLauncher>();

    /// <inheritdoc />
    public async Task OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        var full = PathComparer.Normalize(Path.GetFullPath(path));

        if (!Directory.Exists(full))
        {
            throw new ModOperationException(
                $"There is no folder at '{PathDisplay.Show(full)}' to open.",
                full);
        }

        try
        {
            await Task.Run(
                () =>
                {
                    using var process = Process.Start(
                        new ProcessStartInfo(full) { UseShellExecute = true });
                },
                cancellationToken).ConfigureAwait(false);

            _logger.Information("Opened {Path} in the file manager", full);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            throw new ModOperationException(
                $"'{PathDisplay.Show(full)}' could not be opened: {ex.Message}",
                full,
                ex);
        }
    }
}
