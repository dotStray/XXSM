using CommunityToolkit.Mvvm.ComponentModel;
using Xxsm.Desktop.ViewModels.Pages;

namespace Xxsm.Desktop.ViewModels;

/// <summary>One entry in the navigation rail.</summary>
public sealed partial class NavigationItemViewModel : ObservableObject
{
    /// <summary>Creates the entry.</summary>
    /// <param name="label">The word shown beside the icon.</param>
    /// <param name="icon">Which icon to draw; see <c>Styles/Xxsm.axaml</c>.</param>
    /// <param name="page">The page it selects.</param>
    public NavigationItemViewModel(string label, string icon, PageViewModel page)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(icon);
        ArgumentNullException.ThrowIfNull(page);

        Label = label;
        Icon = icon;
        Page = page;
    }

    /// <summary>The word shown beside the icon.</summary>
    public string Label { get; }

    /// <summary>Which rail icon to draw, by name; one with no icon behind it draws nothing.</summary>
    public string Icon { get; }

    /// <summary>The page this entry selects.</summary>
    public PageViewModel Page { get; }

    /// <summary>A count shown beside the label, or zero for none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadge))]
    private int _badgeCount;

    /// <summary>Whether there is a badge to draw.</summary>
    public bool HasBadge => BadgeCount > 0;
}

/// <summary>One installed game in the selector at the top of the rail.</summary>
public sealed record GameOptionViewModel(string GameId, string DisplayName, string? PackVersion)
{
    /// <summary>The game's icon, shown before its name.</summary>
    public GameIconViewModel? Icon { get; init; }

    /// <summary>What the selector shows.</summary>
    public override string ToString() => DisplayName;
}
