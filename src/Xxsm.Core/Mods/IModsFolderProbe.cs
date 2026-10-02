namespace Xxsm.Core.Mods;

/// <summary>What a candidate Mods folder turned out to be.</summary>
public enum ModsFolderVerdict
{
    /// <summary>Nothing is there.</summary>
    DoesNotExist,

    /// <summary>Something is there, but it is a file.</summary>
    NotADirectory,

    /// <summary>It is a directory, but XXSM cannot write into it, so no mod operation can work.</summary>
    NotWritable,

    /// <summary>It is usable and empty. A perfectly good starting point.</summary>
    Empty,

    /// <summary>It is usable and already holds mods.</summary>
    Usable,
}

/// <summary>What probing a candidate Mods folder found.</summary>
public sealed record ModsFolderProbeResult
{
    /// <summary>The folder that was probed, as it was given.</summary>
    public required string Path { get; init; }

    /// <summary>What it turned out to be.</summary>
    public required ModsFolderVerdict Verdict { get; init; }

    /// <summary>A sentence describing what was found, for the user, with the system's words for a failure.</summary>
    public required string Summary { get; init; }

    /// <summary>What was found inside; null for a folder that is missing or could not be read.</summary>
    public ModsInventory? Inventory { get; init; }

    /// <summary>Whether XXSM can be pointed at this folder.</summary>
    public bool IsUsable => Verdict is ModsFolderVerdict.Empty or ModsFolderVerdict.Usable;
}

/// <summary>Answers "can XXSM use this folder for this game's mods?" without throwing.</summary>
public interface IModsFolderProbe
{
    /// <summary>Probes a candidate Mods folder.</summary>
    /// <param name="modsDirectory">The folder to consider.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>The verdict; never null, and an unusable folder is a verdict, not an exception.</returns>
    Task<ModsFolderProbeResult> ProbeAsync(
        string modsDirectory, CancellationToken cancellationToken = default);
}
