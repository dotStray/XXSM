using Xxsm.Core.Settings;

namespace Xxsm.Packs.Sorting;

/// <summary>Turns the user's saved sort thresholds into the tuning the sorter actually runs with.</summary>
public static class SortThresholdMapping
{
    /// <summary>Applies the user's overrides on top of the defaults; an unset value keeps the default.</summary>
    public static SortSettings ToSortSettings(this SortThresholdSettings? thresholds)
    {
        if (thresholds is null || thresholds.IsDefault)
        {
            return SortSettings.Default;
        }

        var defaults = SortSettings.Default;

        return defaults with
        {
            AmbiguityThreshold = thresholds.AmbiguityThreshold ?? defaults.AmbiguityThreshold,
            MinScore = thresholds.MinScore ?? defaults.MinScore,
            MarginRatio = thresholds.MarginRatio ?? defaults.MarginRatio,
            DefaultVariantConfidenceCeiling =
                thresholds.DefaultVariantConfidenceCeiling ?? defaults.DefaultVariantConfidenceCeiling,
        };
    }

    /// <summary>Reduces a tuning back to only what differs from the defaults, for saving.</summary>
    /// <param name="settings">The tuning the user has arrived at.</param>
    public static SortThresholdSettings ToThresholdSettings(this SortSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var defaults = SortSettings.Default;

        return new SortThresholdSettings
        {
            AmbiguityThreshold = settings.AmbiguityThreshold == defaults.AmbiguityThreshold
                ? null
                : settings.AmbiguityThreshold,
            MinScore = settings.MinScore == defaults.MinScore ? null : settings.MinScore,
            MarginRatio = settings.MarginRatio.Equals(defaults.MarginRatio)
                ? null
                : settings.MarginRatio,
            DefaultVariantConfidenceCeiling =
                settings.DefaultVariantConfidenceCeiling.Equals(defaults.DefaultVariantConfidenceCeiling)
                    ? null
                    : settings.DefaultVariantConfidenceCeiling,
        };
    }
}
