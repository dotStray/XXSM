using CommunityToolkit.Mvvm.Input;

namespace Xxsm.Desktop.ViewModels;

/// <summary>One thing the last scan found in the Mods folder, and what it offers; it returns until fixed.</summary>
public sealed class ModsFolderFindingViewModel
{
    /// <summary>The scan's code for what was found; several of one kind can share a finding.</summary>
    public required string Code { get; init; }

    /// <summary>A short headline.</summary>
    public required string Title { get; init; }

    /// <summary>What was found, naming the folders.</summary>
    public required string Message { get; init; }

    /// <summary>What the button says, or null when there is nothing to offer.</summary>
    public string? ActionText { get; init; }

    /// <summary>What the button does, or null when there is nothing to offer.</summary>
    public IAsyncRelayCommand? ActionCommand { get; init; }

    /// <summary>Whether the button throws something away, so it is drawn with red words.</summary>
    public bool IsDestructive { get; init; }

    /// <summary>Whether there is a button to draw.</summary>
    public bool HasAction => ActionCommand is not null && ActionText is { Length: > 0 };
}
