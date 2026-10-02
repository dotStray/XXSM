using CommunityToolkit.Mvvm.ComponentModel;

namespace Xxsm.Desktop.ViewModels;

/// <summary>Whether Pack Studio is part of the app now; every way into it follows this, without a restart.</summary>
public sealed partial class StudioVisibility : ObservableObject
{
    /// <summary>Whether Studio is shown. False until the settings say otherwise.</summary>
    [ObservableProperty]
    private bool _isShown;
}
