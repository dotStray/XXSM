namespace Xxsm.Packs.Registry;

/// <summary>Where a pack install is up to, for a progress bar.</summary>
/// <param name="Stage">What is happening now.</param>
/// <param name="BytesDone">How much of the pack has been downloaded.</param>
/// <param name="BytesTotal">How big the pack is, from the server or the registry; null when neither says.</param>
public sealed record PackInstallProgress(PackInstallStage Stage, long BytesDone, long? BytesTotal)
{
    /// <summary>How far through the download, from 0 to 1, or null when the size is not known.</summary>
    public double? Fraction => BytesTotal is > 0 and var total ? Math.Clamp((double)BytesDone / total, 0, 1) : null;
}

/// <summary>The steps of a pack install, in order.</summary>
public enum PackInstallStage
{
    /// <summary>Fetching the pack.</summary>
    Downloading,

    /// <summary>Comparing the download with the checksum its registry publishes.</summary>
    Checking,

    /// <summary>Unpacking it and carrying the user's corrections over.</summary>
    Installing,
}
