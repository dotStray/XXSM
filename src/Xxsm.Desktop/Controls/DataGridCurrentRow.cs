using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Xxsm.Desktop.Controls;

/// <summary>Keeps the row the user is on in sight when a bar opening under the table takes its height.</summary>
public sealed class DataGridCurrentRow
{
    private readonly DataGrid _grid;
    private HashSet<object>? _onScreen;

    /// <summary>Watches a grid, for as long as it lives.</summary>
    public DataGridCurrentRow(DataGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);

        _grid = grid;
        grid.SizeChanged += OnSizeChanged;
    }

    /// <summary>Call just before the grid loses height: the current row, if on screen now, stays on screen.</summary>
    public void KeepInSight()
    {
        var presenter = _grid.GetVisualDescendants().OfType<DataGridRowsPresenter>().FirstOrDefault();

        if (presenter is null)
        {
            _onScreen = null;
            return;
        }

        _onScreen = [];

        foreach (var row in presenter.GetVisualChildren().OfType<DataGridRow>())
        {
            if (row is { IsVisible: true, DataContext: { } item }
                && row.TranslatePoint(default, presenter) is { } at
                && at.Y < presenter.Bounds.Height
                && at.Y + row.Bounds.Height > 0)
            {
                _onScreen.Add(item);
            }
        }
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Height >= e.PreviousSize.Height || _onScreen is not { } onScreen)
        {
            return;
        }

        _onScreen = null;

        if (_grid.SelectedItem is { } current && onScreen.Contains(current))
        {
            // After this layout pass: scrolling asks the grid to lay itself out again.
            Dispatcher.UIThread.Post(() => _grid.ScrollIntoView(current, null));
        }
    }
}
