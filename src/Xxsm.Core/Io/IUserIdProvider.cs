namespace Xxsm.Core.Io;

/// <summary>Supplies the process's Unix user id, which names a volume trash directory (<c>.Trash-1000</c>).</summary>
public interface IUserIdProvider
{
    /// <summary>The real user id, or null when it cannot be read; then no volume trash can be named.</summary>
    /// <returns>The user id, or null.</returns>
    int? TryGetUserId();
}
