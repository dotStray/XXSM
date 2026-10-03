using Avalonia;
using Avalonia.Controls;

namespace Xxsm.Desktop.Views.Panels;

/// <summary>One download: its name, its progress, and what can be done with it.</summary>
public partial class DownloadRowView : UserControl
{
    /// <summary>Whether a running download offers <em>Show</em>, which turns the install panel to it.</summary>
    public static readonly StyledProperty<bool> OffersShowProperty =
        AvaloniaProperty.Register<DownloadRowView, bool>(nameof(OffersShow), defaultValue: true);

    /// <summary>Creates the view.</summary>
    public DownloadRowView() => InitializeComponent();

    /// <summary>Whether a running download offers <em>Show</em>; off inside the panel that lists them.</summary>
    public bool OffersShow
    {
        get => GetValue(OffersShowProperty);
        set => SetValue(OffersShowProperty, value);
    }
}
