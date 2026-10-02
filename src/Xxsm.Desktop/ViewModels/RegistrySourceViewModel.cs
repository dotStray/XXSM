using CommunityToolkit.Mvvm.ComponentModel;

namespace Xxsm.Desktop.ViewModels;

/// <summary>One configured place packs are downloaded from.</summary>
public sealed partial class RegistrySourceViewModel : ObservableObject
{
    /// <summary>Creates the row.</summary>
    /// <param name="location">The URL, folder or <c>index.json</c> path.</param>
    public RegistrySourceViewModel(string location)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        Location = location;
    }

    /// <summary>The URL, folder or <c>index.json</c> path, exactly as the user gave it.</summary>
    public string Location { get; }

    /// <summary>Whether this row is currently asking whether to remove itself.</summary>
    [ObservableProperty]
    private bool _isConfirmingRemoval;
}
