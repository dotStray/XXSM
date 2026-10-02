namespace Xxsm.Core.Io;

/// <summary>Reports which mounted filesystem a path lives on, by its longest mount-point prefix.</summary>
public interface IVolumeResolver
{
    /// <summary>Returns the mount point of the filesystem containing <paramref name="path"/>.</summary>
    /// <param name="path">An absolute path. It need not exist.</param>
    /// <returns>The mount point, for example <c>/</c> or <c>/mnt/games</c>.</returns>
    string GetVolumeRoot(string path);
}
