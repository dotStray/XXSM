using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace Xxsm.Desktop.Services;

/// <summary>Reads text off the clipboard.</summary>
public interface IClipboardTextReader
{
    /// <summary>What the clipboard holds as text.</summary>
    /// <returns>The text, or null when the clipboard holds none.</returns>
    Task<string?> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>The real reader, over the window's Avalonia clipboard.</summary>
public sealed class AvaloniaClipboardTextReader(Func<TopLevel?> topLevel) : IClipboardTextReader
{
    private readonly Func<TopLevel?> _topLevel = topLevel;

    /// <inheritdoc />
    public async Task<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (_topLevel()?.Clipboard is not { } clipboard)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        return await clipboard.TryGetTextAsync().ConfigureAwait(true);
    }
}
