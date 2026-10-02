using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Xxsm.Desktop.ViewModels;

namespace Xxsm.Desktop.Views;

/// <summary>The application shell window.</summary>
public partial class MainWindow : Window
{
    /// <summary>Creates the window.</summary>
    public MainWindow()
    {
        InitializeComponent();

        // Tunnelled, so it sees which entry was selected before the rail changes it.
        Rail.AddHandler(PointerPressedEvent, OnRailPressed, RoutingStrategies.Tunnel);

        // Tunnelled, so a page's own key handling cannot take Esc first.
        MenuScrim.PointerPressed += OnScrimPressed;
        AddHandler(KeyDownEvent, OnMenuKeyDown, RoutingStrategies.Tunnel);

        // Bubbled, so a control that uses the key itself keeps it.
        AddHandler(KeyDownEvent, OnShortcutKeyDown, RoutingStrategies.Bubble);
    }

    /// <inheritdoc />
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        (DataContext as MainWindowViewModel)?.SetWindowWidth(e.NewSize.Width);
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (Bounds.Width > 0)
        {
            (DataContext as MainWindowViewModel)?.SetWindowWidth(Bounds.Width);
        }
    }

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        (DataContext as MainWindowViewModel)?.CloseMenuOver();
        e.Handled = true;
    }

    private void OnMenuKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is MainWindowViewModel { IsMenuOver: true } shell)
        {
            shell.CloseMenuOver();
            e.Handled = true;
        }
    }

    /// <summary>F5 presses the page's Refresh button and Ctrl+F focuses its search box, unless something covers the
    /// page.</summary>
    private void OnShortcutKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || DataContext is not MainWindowViewModel shell)
        {
            return;
        }

        var refresh = e.Key == Key.F5 && e.KeyModifiers == KeyModifiers.None;
        var search = e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control;

        if ((!refresh && !search) || IsPageCovered())
        {
            return;
        }

        e.Handled = refresh
            ? shell.RefreshCurrentPage()
            : shell.OpenSearchOnCurrentPage() && FocusPageSearch();
    }

    /// <summary>Whether a panel or the menu lies over the page: any scrim on screen, as a click would find.</summary>
    private bool IsPageCovered() =>
        this.GetVisualDescendants()
            .OfType<Border>()
            .Any(border => border.Classes.Contains("scrim") && border.IsEffectivelyVisible);

    /// <summary>Focuses the page's search box and selects what is in it, so typing replaces it.</summary>
    private bool FocusPageSearch()
    {
        var box = PageHost.GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(textBox => textBox.Classes.Contains("toolbarSearch") && textBox.IsEffectivelyVisible);

        if (box is null || !box.Focus(NavigationMethod.Tab))
        {
            return false;
        }

        box.SelectAll();
        return true;
    }

    /// <summary>A press on the selected rail entry takes it back to its first screen.</summary>
    private void OnRailPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel shell
            && e.GetCurrentPoint(Rail).Properties.IsLeftButtonPressed
            && (e.Source as Control)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()
                is { DataContext: NavigationItemViewModel item }
            && ReferenceEquals(item, shell.SelectedNavigationItem))
        {
            shell.ReturnToTop(item);
        }
    }
}
