using Avalonia;
using Avalonia.Styling;
using Xxsm.Core.Settings;

namespace Xxsm.Desktop.Services;

/// <summary>Applies the user's chosen colour scheme to the running application.</summary>
public interface IThemeApplier
{
    /// <summary>Applies a theme.</summary>
    void Apply(AppTheme theme);
}

/// <summary>The real theme applier; the system theme is no override at all.</summary>
public sealed class ThemeApplier(Application application) : IThemeApplier
{
    private readonly Application _application = application;

    /// <inheritdoc />
    public void Apply(AppTheme theme) =>
        _application.RequestedThemeVariant = theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
}
