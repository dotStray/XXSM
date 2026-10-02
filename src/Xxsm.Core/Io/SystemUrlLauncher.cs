using System.ComponentModel;
using System.Diagnostics;
using Serilog;

namespace Xxsm.Core.Io;

/// <summary>The default <see cref="IUrlLauncher"/>: hands an http or https address to the desktop.</summary>
public sealed class SystemUrlLauncher(ILogger logger) : IUrlLauncher
{
    private readonly ILogger _logger = logger.ForContext<SystemUrlLauncher>();

    /// <inheritdoc />
    public async Task OpenAsync(string url, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        cancellationToken.ThrowIfCancellationRequested();

        var trimmed = url.Trim();

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var address) ||
            (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
        {
            throw new ModOperationException(
                $"'{trimmed}' is not a web address XXSM will open. " +
                "Only http and https links can be opened from here.",
                trimmed);
        }

        try
        {
            await Task.Run(
                () =>
                {
                    using var process = Process.Start(
                        new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
                },
                cancellationToken).ConfigureAwait(false);

            _logger.Information("Opened {Url} in the browser", address.AbsoluteUri);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            throw new ModOperationException(
                $"'{address.AbsoluteUri}' could not be opened: {ex.Message}",
                address.AbsoluteUri,
                ex);
        }
    }
}
