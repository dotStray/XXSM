namespace Xxsm.Desktop.Services;

/// <summary>Closes the window and opens XXSM again, to finish a scheduled reset.</summary>
public interface IApplicationRestarter
{
    /// <summary>Starts closing. Returns at once; the window closes once its work is written.</summary>
    void Restart();
}

/// <summary>The real restarter, over the application object's own restart.</summary>
public sealed class ApplicationRestarter(Action restart) : IApplicationRestarter
{
    private readonly Action _restart = restart;

    /// <inheritdoc />
    public void Restart() => _restart();
}
