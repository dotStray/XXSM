namespace Xxsm.Core.Io;

/// <summary>Opens a folder in the desktop's own file manager.</summary>
public interface IFolderLauncher
{
    /// <summary>Opens <paramref name="path"/> in whatever the desktop uses to browse files.</summary>
    /// <param name="path">The folder to show.</param>
    /// <param name="cancellationToken">Cancels before the handler is started.</param>
    /// <returns>A task that completes once the handler has been started.</returns>
    /// <exception cref="ModOperationException">The folder is missing, or nothing is registered to open one; with the
    /// operating system's words.</exception>
    Task OpenAsync(string path, CancellationToken cancellationToken = default);
}
