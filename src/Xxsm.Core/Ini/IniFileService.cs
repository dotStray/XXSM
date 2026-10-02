using Serilog;
using Xxsm.Core.Io;

namespace Xxsm.Core.Ini;

/// <summary>The default <see cref="IIniFileService"/>, reading and writing the real filesystem.</summary>
public sealed class IniFileService(ILogger logger) : IIniFileService
{
    private readonly ILogger _logger = logger.ForContext<IniFileService>();

    /// <inheritdoc />
    public async Task<IniDocument> ReadAsync(
        string path, int maxBytes = int.MaxValue, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);

        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 64 * 1024, useAsync: true);

            var length = stream.Length;
            var wanted = (int)Math.Min(length, maxBytes);
            var buffer = new byte[wanted];
            await stream.ReadExactlyAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);

            return IniParser.Parse(buffer, path, truncated: length > wanted);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new ModOperationException(
                $"Could not read the INI file at {PathDisplay.Show(path)}: {exception.Message}", path, exception);
        }
    }

    /// <inheritdoc />
    public async Task WriteAsync(
        string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);

        var byteCount = bytes.Length;

        try
        {
            await AtomicFile.WriteAllBytesAsync(path, bytes, AtomicWriteOptions.DurableOnly, cancellationToken)
                .ConfigureAwait(false);

            _logger.Information("Rewrote INI {Path} ({ByteCount} bytes)", path, byteCount);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new ModOperationException(
                $"Could not write the INI file at {PathDisplay.Show(path)}: {exception.Message}", path, exception);
        }
    }
}
