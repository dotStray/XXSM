using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Xxsm.Desktop.Controls;

/// <summary>Spreadsheet columns for a <see cref="DataGrid"/>: drag an edge to size, double-click to fit.</summary>
/// <remarks>A column is reported by its string <see cref="DataGridColumn.Tag"/> once a drag changes it.</remarks>
public sealed class DataGridColumnSizer
{
    /// <summary>How close to a header's edge a double-click has to be, in pixels, to fit that column.</summary>
    internal const double EdgeReach = 6;

    private readonly DataGrid _grid;
    private readonly Panel _measureHost;
    private Dictionary<DataGridColumn, double>? _atPress;

    /// <summary>Watches a grid.</summary>
    /// <param name="grid">The grid.</param>
    /// <param name="measureHost">An invisible panel in the same page, where cells are measured.</param>
    public DataGridColumnSizer(DataGrid grid, Panel measureHost)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(measureHost);

        _grid = grid;
        _measureHost = measureHost;

        grid.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        grid.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>Raised when the user has sized or fitted columns: their ids and new widths.</summary>
    public event EventHandler<IReadOnlyDictionary<string, double>>? Resized;

    /// <summary>Gives each column that has a remembered width that width.</summary>
    public void Apply(IReadOnlyDictionary<string, double> widths)
    {
        ArgumentNullException.ThrowIfNull(widths);

        foreach (var column in _grid.Columns)
        {
            if (Id(column) is { } id && widths.TryGetValue(id, out var width) && width > 0)
            {
                column.Width = new DataGridLength(Math.Max(width, column.MinWidth));
            }
        }
    }

    /// <summary>Sizes a column to its widest entry or its header, whichever is wider.</summary>
    /// <returns>The new width, or null when the column cannot be fitted.</returns>
    public double? Fit(DataGridColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);

        if (Measure(column) is not { } width)
        {
            return null;
        }

        column.Width = new DataGridLength(width);
        return width;
    }

    /// <summary>Fits every column the user has not sized, up to a maximum; nothing is remembered.</summary>
    /// <param name="remembered">The widths the user set, by column id; those columns are left alone.</param>
    /// <param name="maxWidth">The widest a fitted column is made.</param>
    public void FitUnsized(IReadOnlyDictionary<string, double> remembered, double maxWidth)
    {
        ArgumentNullException.ThrowIfNull(remembered);

        foreach (var column in _grid.Columns.Where(c => c.IsVisible))
        {
            if (Id(column) is { } id && !remembered.ContainsKey(id) && Measure(column) is { } width)
            {
                column.Width = new DataGridLength(Math.Max(Math.Min(width, maxWidth), column.MinWidth));
            }
        }
    }

    /// <summary>Whether the grid is on screen with rows drawn, which fitting needs.</summary>
    public bool CanMeasure =>
        _grid.IsEffectivelyVisible
        && _grid.GetVisualDescendants().OfType<DataGridRow>().Any(row => row.IsVisible)
        && VisibleHeaders().Count > 0;

    /// <summary>The width that fits a column's widest entry or its header, whichever is wider.</summary>
    private double? Measure(DataGridColumn column)
    {
        if (column is not DataGridTemplateColumn { CellTemplate: { } template } || !column.CanUserResize)
        {
            return null;
        }

        // The cells draw in the grid's own font, which need not be the page's: measure in it too.
        var sample = _grid.GetVisualDescendants().OfType<DataGridCell>().FirstOrDefault();
        _measureHost.SetValue(TextElement.FontSizeProperty, sample?.FontSize ?? _grid.FontSize);
        _measureHost.SetValue(TextElement.FontFamilyProperty, sample?.FontFamily ?? _grid.FontFamily);
        _measureHost.SetValue(TextElement.FontWeightProperty, sample?.FontWeight ?? _grid.FontWeight);

        var widest = 0d;

        foreach (var item in _grid.ItemsSource ?? Array.Empty<object>())
        {
            if (template.Build(item) is not { } cell)
            {
                continue;
            }

            cell.DataContext = item;
            _measureHost.Children.Add(cell);

            try
            {
                cell.Measure(Size.Infinity);
                widest = Math.Max(widest, cell.DesiredSize.Width);
            }
            finally
            {
                _measureHost.Children.Remove(cell);
            }
        }

        var width = Math.Ceiling(Math.Max(widest + CellPadding(column), HeaderWidth(column)));
        return Math.Max(width, column.MinWidth);
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_grid).Properties.IsLeftButtonPressed
            || (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<DataGridColumnHeader>().FirstOrDefault() is not { } header)
        {
            _atPress = null;
            return;
        }
        if (e.ClickCount == 2 && EdgeColumn(header, e.GetPosition(header)) is { } column && Id(column) is { } id)
        {
            // Handled, so the second click neither sorts the column nor starts another drag.
            e.Handled = true;
            _atPress = null;

            if (Fit(column) is { } width)
            {
                Resized?.Invoke(this, new Dictionary<string, double> { [id] = width });
            }

            return;
        }

        _atPress = _grid.Columns.Where(c => Id(c) is not null).ToDictionary(c => c, c => c.ActualWidth);
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_atPress is not { } before)
        {
            return;
        }

        _atPress = null;

        var changed = before
            .Where(pair => pair.Key.Width.IsAbsolute && Math.Abs(pair.Key.ActualWidth - pair.Value) >= 0.5)
            .ToDictionary(pair => Id(pair.Key)!, pair => Math.Round(pair.Key.ActualWidth, 1));

        if (changed.Count > 0)
        {
            Resized?.Invoke(this, changed);
        }
    }

    /// <summary>The column whose edge a header point is at: this one at its right, the previous at its left.</summary>
    private DataGridColumn? EdgeColumn(DataGridColumnHeader header, Point at)
    {
        var columns = VisibleColumns();
        var headers = VisibleHeaders();
        var index = headers.IndexOf(header);

        if (index < 0)
        {
            return null;
        }

        if (at.X >= header.Bounds.Width - EdgeReach)
        {
            return index < columns.Count ? columns[index] : null;
        }

        if (at.X <= EdgeReach && index > 0)
        {
            return index - 1 < columns.Count ? columns[index - 1] : null;
        }

        return null;
    }

    /// <summary>The drawn columns, left to right, paired with headers and cells by position.</summary>
    private List<DataGridColumn> VisibleColumns() =>
        [.. _grid.Columns.Where(c => c.IsVisible).OrderBy(c => c.DisplayIndex)];

    /// <summary>The column headers on screen, left to right; filler and corner headers left out.</summary>
    private List<DataGridColumnHeader> VisibleHeaders() =>
        [.. _grid.GetVisualDescendants()
            .OfType<DataGridColumnHeader>()
            .Where(h => h.IsVisible && h.Bounds.Width > 0)
            .OrderBy(h => h.TranslatePoint(default, _grid)?.X ?? 0)];

    /// <summary>What a cell of the column adds around its content, read from one on screen.</summary>
    private double CellPadding(DataGridColumn column)
    {
        var index = VisibleColumns().IndexOf(column);

        foreach (var row in _grid.GetVisualDescendants().OfType<DataGridRow>())
        {
            var cells = row.GetVisualDescendants()
                .OfType<DataGridCell>()
                .Where(c => c.IsVisible)
                .OrderBy(c => c.TranslatePoint(default, row)?.X ?? 0)
                .ToList();

            if (index >= 0 && index < cells.Count
                && cells[index].GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault() is { } presenter)
            {
                return Math.Max(0, cells[index].Bounds.Width - presenter.Bounds.Width);
            }
        }

        return 2;
    }

    private double HeaderWidth(DataGridColumn column)
    {
        var index = VisibleColumns().IndexOf(column);
        var headers = VisibleHeaders();

        if (index < 0 || index >= headers.Count)
        {
            return 0;
        }

        var header = headers[index];
        header.Measure(Size.Infinity);
        var width = header.DesiredSize.Width;
        header.InvalidateMeasure();
        return width;
    }

    private static string? Id(DataGridColumn column) =>
        column.Tag is string { Length: > 0 } id && column.CanUserResize ? id : null;
}
