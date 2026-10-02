using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Xxsm.Desktop.Services;

/// <summary>A file type a file picker offers to filter by.</summary>
/// <param name="Name">What to call it, for example <c>Images</c>.</param>
/// <param name="Extensions">The extensions, with their leading dot. Empty means every file.</param>
public sealed record FileTypeFilter(string Name, IReadOnlyList<string> Extensions);

/// <summary>Asks the user to choose a folder or a file, returning a local path.</summary>
public interface IStoragePicker
{
    /// <summary>Shows the platform folder picker.</summary>
    /// <param name="title">The dialog title.</param>
    /// <param name="startAt">Where to open, or null for the platform's own default.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The chosen folder's local path, or null when cancelled or it has no local path.</returns>
    Task<string?> PickFolderAsync(
        string title, string? startAt = null, CancellationToken cancellationToken = default);

    /// <summary>Shows the platform file picker.</summary>
    /// <param name="title">The dialog title.</param>
    /// <param name="filters">The file types to offer; "All files" is always added after them.</param>
    /// <param name="startAt">Where to open, or null for the platform's own default.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The chosen file's local path, or null when cancelled or it has no local path.</returns>
    Task<string?> PickFileAsync(
        string title,
        IReadOnlyList<FileTypeFilter>? filters = null,
        string? startAt = null,
        CancellationToken cancellationToken = default);
}

/// <summary>The real picker, over Avalonia's storage provider.</summary>
public sealed class StorageProviderPicker(Func<TopLevel?> topLevel) : IStoragePicker
{
    private readonly Func<TopLevel?> _topLevel = topLevel;

    /// <inheritdoc />
    public async Task<string?> PickFolderAsync(
        string title, string? startAt = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_topLevel() is not { } window)
        {
            return null;
        }

        var options = new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        };

        if (startAt is { Length: > 0 })
        {
            options.SuggestedStartLocation =
                await window.StorageProvider.TryGetFolderFromPathAsync(startAt).ConfigureAwait(true);
        }

        var chosen = await window.StorageProvider.OpenFolderPickerAsync(options).ConfigureAwait(true);

        return chosen.Count == 0 ? null : chosen[0].TryGetLocalPath();
    }

    /// <inheritdoc />
    public async Task<string?> PickFileAsync(
        string title,
        IReadOnlyList<FileTypeFilter>? filters = null,
        string? startAt = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_topLevel() is not { } window)
        {
            return null;
        }

        var types = new List<FilePickerFileType>();

        foreach (var filter in filters ?? [])
        {
            types.Add(new FilePickerFileType(filter.Name)
            {
                Patterns = [.. filter.Extensions.Select(extension => "*" + extension)],
            });
        }

        types.Add(FilePickerFileTypes.All);

        var options = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = types,
        };

        if (startAt is { Length: > 0 })
        {
            options.SuggestedStartLocation =
                await window.StorageProvider.TryGetFolderFromPathAsync(startAt).ConfigureAwait(true);
        }

        var chosen = await window.StorageProvider.OpenFilePickerAsync(options).ConfigureAwait(true);

        return chosen.Count == 0 ? null : chosen[0].TryGetLocalPath();
    }
}
