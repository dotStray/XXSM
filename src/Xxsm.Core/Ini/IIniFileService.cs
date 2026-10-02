namespace Xxsm.Core.Ini;

/// <summary>Reads and writes 3DMigoto INI files on disk.</summary>
public interface IIniFileService
{
    /// <summary>Reads and parses an INI file.</summary>
    /// <param name="path">The file to read.</param>
    /// <param name="maxBytes">Stop after this many bytes; a longer file is read that far and marked truncated.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The parsed document.</returns>
    /// <exception cref="ModOperationException">The file could not be read; with the operating system's
    /// words.</exception>
    Task<IniDocument> ReadAsync(string path, int maxBytes = int.MaxValue, CancellationToken cancellationToken = default);

    /// <summary>Replaces a file's contents atomically, through a temporary file beside it.</summary>
    /// <param name="path">The file to replace.</param>
    /// <param name="bytes">The complete new contents, normally from <see cref="IniEditor"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ModOperationException">The file could not be written; with the operating system's
    /// words.</exception>
    Task WriteAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);
}
