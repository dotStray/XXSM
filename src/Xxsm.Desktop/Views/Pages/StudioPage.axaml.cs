using System.Collections;
using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xxsm.Desktop.Controls;
using Xxsm.Desktop.Services;
using Xxsm.Desktop.ViewModels;
using Xxsm.Desktop.ViewModels.Pages;

namespace Xxsm.Desktop.Views.Pages;

/// <summary>Pack Studio: only the table's plumbing, carrying the selection both ways across rebuilds.</summary>
public partial class StudioPage : UserControl
{
    /// <summary>The <see cref="DataGridColumn.Tag"/> of the Name column.</summary>
    public const string NameTag = "name";

    /// <summary>The <see cref="DataGridColumn.Tag"/> of the Internal name column.</summary>
    public const string InternalNameTag = "internalName";

    /// <summary>The <see cref="DataGridColumn.Tag"/> of the Aliases column.</summary>
    public const string AliasesTag = "aliases";

    /// <summary>The <see cref="DataGridColumn.Tag"/> of the Outfit of column.</summary>
    public const string OutfitOfTag = "outfitOf";

    /// <summary>The <see cref="DataGridColumn.Tag"/> of the Hashes column.</summary>
    public const string HashesTag = "hashes";

    /// <summary>The <see cref="DataGridColumn.Tag"/> of the Picture column.</summary>
    public const string PictureTag = "picture";

    /// <summary>The tag of the empty column that keeps Hashes and Problems at the right-hand edge.</summary>
    public const string SpacerTag = "spacer";

    /// <summary>What an attribute column's tag starts with; the attribute's id follows.</summary>
    public const string AttributeTagPrefix = "attribute:";

    /// <summary>The table's name among the remembered column widths.</summary>
    public const string WidthsTable = StudioPageViewModel.ColumnWidthsTable;

    /// <summary>The widest a column is made when it is fitted to what it holds rather than dragged.</summary>
    public const double MaxFittedWidth = 260;

    /// <summary>An attribute column's width before it is fitted, and in a draft with no characters yet.</summary>
    private const double AttributeWidth = 110;

    /// <summary>The narrowest the spacer goes: not zero, or the sizer loses its header and cells.</summary>
    private const double SpacerMinimum = 1;

    /// <summary>The Problems panel's width when the page has the room for it.</summary>
    private const double ProblemsWide = 320;

    /// <summary>The narrowest the Problems panel goes before the table gives up width instead.</summary>
    private const double ProblemsNarrow = 300;

    /// <summary>The width the table is left with before the Problems panel starts to give any up.</summary>
    private const double TableRoom = 320;

    private readonly List<DataGridColumn> _attributeColumns = [];
    private readonly Dictionary<string, double> _designWidths = new(StringComparer.Ordinal);
    private readonly DataGridColumnSizer _sizer;
    private bool _isFitPending;
    private bool _isSpacing;
    private readonly DataGridCurrentRow _currentRow;
    private bool _isBarShown;
    private StudioAttributeEditorViewModel? _dragging;
    private StudioPageViewModel? _viewModel;
    private bool _syncing;

    /// <summary>Creates the view.</summary>
    public StudioPage()
    {
        InitializeComponent();

        CharacterGrid.AddHandler(PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel);
        CharacterGrid.AddHandler(KeyDownEvent, OnGridKeyDown);

        _sizer = new DataGridColumnSizer(CharacterGrid, MeasureHost);
        _sizer.Resized += (_, widths) => _viewModel?.ColumnWidths.Remember(WidthsTable, widths);

        foreach (var column in CharacterGrid.Columns)
        {
            if (column.Tag is string { Length: > 0 } tag && column.Width.IsAbsolute)
            {
                _designWidths[tag] = column.Width.Value;
            }
        }

        CharacterGrid.LayoutUpdated += (_, _) => UpdateSpacer();

        SizeChanged += (_, _) => SizeProblemsPanel();

        _currentRow = new DataGridCurrentRow(CharacterGrid);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // The selection can arrive before the grid has its rows; it is put back once they come.
        CharacterGrid.PropertyChanged += (_, e) =>
        {
            if (e.Property == ItemsControl.ItemsSourceProperty || e.Property == DataGrid.ItemsSourceProperty)
            {
                SyncSelection();
            }
        };
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Attach(DataContext as StudioPageViewModel);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Attach(DataContext as StudioPageViewModel);
    }

    /// <inheritdoc />
    /// <remarks>Unsubscribes: the page outlives this view.</remarks>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Attach(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void Attach(StudioPageViewModel? viewModel)
    {
        if (ReferenceEquals(viewModel, _viewModel))
        {
            return;
        }

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel.ColumnWidths.Loaded -= OnWidthsLoaded;
            _viewModel.ColumnWidths.Forgotten -= OnWidthsForgotten;
            _viewModel.CellEditRequested -= OnCellEditRequested;
            CharacterGrid.LayoutUpdated -= OnGridLayoutUpdated;
            _isFitPending = false;
        }

        _viewModel = viewModel;

        if (_viewModel is not null)
        {
            _isBarShown = _viewModel.IsSelectionBarVisible;
            _viewModel.PropertyChanged += OnViewModelChanged;
            _viewModel.ColumnWidths.Loaded += OnWidthsLoaded;
            _viewModel.ColumnWidths.Forgotten += OnWidthsForgotten;
            _viewModel.CellEditRequested += OnCellEditRequested;
            SyncAttributeColumns();
            ApplyWidths();
            SyncSelection();
            ShowTickColumn(_viewModel.IsSelectMode);

            if (Bounds.Width > 0)
            {
                _viewModel.SetPageWidth(Bounds.Width);
            }
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StudioPageViewModel.SelectedRows))
        {
            SyncSelection();
        }
        else if (e.PropertyName == nameof(StudioPageViewModel.AttributeColumns))
        {
            SyncAttributeColumns();
        }
        else if (e.PropertyName == nameof(StudioPageViewModel.IsDraftOpen) && _viewModel is { IsDraftOpen: true })
        {
            ApplyWidths();
        }
        else if (e.PropertyName == nameof(StudioPageViewModel.IsSelectMode) && _viewModel is { } page)
        {
            ShowTickColumn(page.IsSelectMode);
        }
        else if (e.PropertyName == nameof(StudioPageViewModel.IsSelectionBarVisible) && _viewModel is { } owner)
        {
            if (owner.IsSelectionBarVisible && !_isBarShown)
            {
                _currentRow.KeepInSight();
            }

            _isBarShown = owner.IsSelectionBarVisible;
        }
    }

    /// <summary>One column per attribute the game defines, before Hashes; built without bindings.</summary>
    private void SyncAttributeColumns()
    {
        if (_viewModel is null)
        {
            return;
        }

        var grid = CharacterGrid;

        foreach (var column in _attributeColumns)
        {
            grid.Columns.Remove(column);
        }

        _attributeColumns.Clear();

        // Before the spacer, so it joins the columns about the character.
        var at = grid.Columns.IndexOf(grid.Columns.First(c => Equals(c.Tag, SpacerTag)));

        foreach (var attribute in _viewModel.AttributeColumns)
        {
            var id = attribute.Id;
            var column = new DataGridTemplateColumn
            {
                Header = attribute.Header,
                Tag = AttributeTagPrefix + id,
                Width = new DataGridLength(AttributeWidth),
                MinWidth = 60,
                CustomSortComparer = new AttributeComparer(id),
                CellTemplate = new FuncDataTemplate<StudioCharacterRowViewModel>((_, _) => RowText(row => row.AttributeText(id))),
                CellEditingTemplate = new FuncDataTemplate<StudioCharacterRowViewModel>((row, _) =>
                {
                    var box = new AutoCompleteBox
                    {
                        Text = row?.AttributeText(id) ?? string.Empty,
                        ItemsSource = attribute.Suggestions,
                    };
                    box.Classes.Add("cell");
                    AutomationProperties.SetName(box, attribute.Header);
                    return box;
                }),
            };

            grid.Columns.Insert(at++, column);
            _attributeColumns.Add(column);
        }

        ApplyWidths();
    }

    private void OnWidthsLoaded(object? sender, EventArgs e) => ApplyWidths();

    /// <summary>Starts editing the cell a Problems panel button names, once the grid has caught up.</summary>
    private void OnCellEditRequested(object? sender, StudioCellEditRequest request)
    {
        var tag = request.Field switch
        {
            Xxsm.Packs.Loading.PackDiagnosticFields.InternalName => InternalNameTag,
            Xxsm.Packs.Loading.PackDiagnosticFields.BaseCharacterId => OutfitOfTag,
            Xxsm.Packs.Loading.PackDiagnosticFields.DisplayName => NameTag,
            Xxsm.Packs.Loading.PackDiagnosticFields.AttributeValue when request.Attribute is { } attribute => AttributeTagPrefix + attribute,
            _ => null,
        };

        if (tag is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                var grid = CharacterGrid;

                if (grid.Columns.FirstOrDefault(c => string.Equals(c.Tag as string, tag, StringComparison.OrdinalIgnoreCase)) is not { } column
                    || grid.ItemsSource is not IList rows
                    || !rows.Contains(request.Row))
                {
                    return;
                }

                // Only selecting a row makes it current: select the one at fault, then restore the rest.
                var highlighted = _viewModel?.SelectedRows.Where(r => !ReferenceEquals(r, request.Row)).ToList() ?? [];

                grid.SelectedItem = request.Row;

                foreach (var row in highlighted.Where(rows.Contains))
                {
                    grid.SelectedItems.Add(row);
                }

                grid.ScrollIntoView(request.Row, column);
                grid.CurrentColumn = column;
                grid.Focus();
                grid.BeginEdit();
            },
            DispatcherPriority.Background);
    }

    private void OnWidthsForgotten(object? sender, string table)
    {
        if (table == WidthsTable)
        {
            ApplyWidths();
        }
    }

    /// <summary>Gives each column its remembered width and fits the others to what they hold, once drawn.</summary>
    private void ApplyWidths()
    {
        if (_viewModel is not { } page)
        {
            return;
        }

        var remembered = page.ColumnWidths.For(WidthsTable);

        foreach (var column in CharacterGrid.Columns)
        {
            if (column.Tag is string { Length: > 0 } tag && column.CanUserResize && !remembered.ContainsKey(tag))
            {
                column.Width = new DataGridLength(_designWidths.TryGetValue(tag, out var width) ? width : AttributeWidth);
            }
        }

        _sizer.Apply(remembered);

        if (!_isFitPending)
        {
            _isFitPending = true;
            CharacterGrid.LayoutUpdated += OnGridLayoutUpdated;
        }

        Dispatcher.UIThread.Post(FitUnsizedColumns, DispatcherPriority.Loaded);
    }

    private void OnGridLayoutUpdated(object? sender, EventArgs e) => FitUnsizedColumns();

    /// <summary>Gives the spacer column the width the others leave; written only when a pixel out.</summary>
    private void UpdateSpacer()
    {
        if (_isSpacing
            || CharacterGrid.Columns.FirstOrDefault(c => Equals(c.Tag, SpacerTag)) is not { } spacer)
        {
            return;
        }

        var viewport = ColumnsWidth();

        if (viewport <= 0)
        {
            return;
        }

        var used = CharacterGrid.Columns
            .Where(column => column.IsVisible && !ReferenceEquals(column, spacer))
            .Sum(column => column.ActualWidth);

        var slack = Math.Max(SpacerMinimum, viewport - used);

        if (Math.Abs(spacer.ActualWidth - slack) < 1)
        {
            return;
        }

        _isSpacing = true;

        try
        {
            spacer.Width = new DataGridLength(slack);
        }
        finally
        {
            _isSpacing = false;
        }
    }

    /// <summary>Keeps the Problems panel at full width while there is room, narrowing it before the table.</summary>
    private void SizeProblemsPanel()
    {
        var page = Bounds.Width;

        if (page <= 0)
        {
            return;
        }

        _viewModel?.SetPageWidth(page);

        var width = Math.Clamp(Math.Round(page - TableRoom), ProblemsNarrow, ProblemsWide);

        if (Math.Abs(ProblemsPanel.Width - width) >= 1)
        {
            ProblemsPanel.Width = width;
        }
    }

    /// <summary>The width the table has for its columns: its own, less the scroll bar drawn over the headers.</summary>
    private double ColumnsWidth()
    {
        var headers = CharacterGrid.GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(c => c.Name == "PART_ColumnHeadersPresenter");

        if (headers is null)
        {
            return 0;
        }

        var bar = CharacterGrid.GetVisualDescendants()
            .OfType<ScrollBar>()
            .FirstOrDefault(b => b.Name == "PART_VerticalScrollbar");

        return headers.Bounds.Width - (bar is { IsVisible: true } ? bar.Bounds.Width : 0);
    }

    private void FitUnsizedColumns()
    {
        if (!_isFitPending || _viewModel is not { } page)
        {
            return;
        }

        if (page.Rows.Count > 0 && !_sizer.CanMeasure)
        {
            return;
        }

        _isFitPending = false;
        CharacterGrid.LayoutUpdated -= OnGridLayoutUpdated;

        if (page.Rows.Count > 0)
        {
            _sizer.FitUnsized(page.ColumnWidths.For(WidthsTable), MaxFittedWidth);
        }
    }

    private static TextBlock RowText(Func<StudioCharacterRowViewModel, string> read)
    {
        var text = new TextBlock();
        text.Classes.Add("cell");
        text.DataContextChanged += (_, _) =>
            text.Text = text.DataContext is StudioCharacterRowViewModel row ? read(row) : string.Empty;
        return text;
    }

    /// <summary>Puts the caret in a chooser or a box as its cell starts editing, with the whole list open.</summary>
    private void OnPreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
    {
        if (Equals(e.Column.Tag, PictureTag))
        {
            // A picture is chosen from a file, not typed: the cell's edit becomes the picker.
            if (e.Row.DataContext is StudioCharacterRowViewModel pictureRow && _viewModel is { } owner)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    CharacterGrid.CancelEdit();
                    owner.ChoosePictureCommand.Execute(pictureRow);
                });
            }

            return;
        }

        if (Equals(e.Column.Tag, HashesTag))
        {
            // The hashes are edited in a panel, not in the cell.
            if (e.Row.DataContext is StudioCharacterRowViewModel hashRow && _viewModel is { } page)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    CharacterGrid.CancelEdit();
                    page.OpenHashes(hashRow);
                });
            }

            return;
        }

        switch (Editor(e.EditingElement))
        {
            case AutoCompleteBox box:
                // Every choice until something is typed: filtering by the current value would show only itself.
                var original = box.Text ?? string.Empty;
                var self = (e.Row.DataContext as StudioCharacterRowViewModel)?.InternalName;
                box.FilterMode = AutoCompleteFilterMode.Custom;
                box.ItemFilter = (search, item) =>
                    !IsSelf(item, self)
                    && (string.IsNullOrEmpty(search)
                    || string.Equals(search, original, StringComparison.Ordinal)
                        || (item?.ToString() ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase));

                Dispatcher.UIThread.Post(() =>
                {
                    box.Focus();
                    if (box.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { } inner)
                    {
                        inner.SelectAll();
                    }

                    box.IsDropDownOpen = true;
                });
                break;

            case TextBox text:
                Dispatcher.UIThread.Post(() =>
                {
                    text.Focus();
                    text.SelectAll();
                });
                break;
        }
    }

    /// <summary>Hands what a chooser or the hashes box ended up holding to the page.</summary>
    /// <remarks>After the grid's own edit ends: the page rebuilds every row.</remarks>
    private void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit
            || _viewModel is not { } page
            || e.Row.DataContext is not StudioCharacterRowViewModel row
            || e.Column.Tag is not string tag)
        {
            return;
        }

        var text = Editor(e.EditingElement) switch
        {
            AutoCompleteBox box => box.Text,
            TextBox box => box.Text,
            _ => null,
        };

        if (text is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => Commit(page, row, tag, text));
    }

    /// <summary>What a cell's edit means, by its column.</summary>
    /// <param name="page">The page.</param>
    /// <param name="row">The row edited.</param>
    /// <param name="tag">The column's tag.</param>
    /// <param name="text">What the cell held when the edit ended.</param>
    internal static void Commit(IStudioTableEdits page, StudioCharacterRowViewModel row, string tag, string text)
    {
        switch (tag)
        {
            case NameTag:
                if (!string.Equals(text, row.DisplayName, StringComparison.Ordinal))
                {
                    page.EditName(row, text);
                }

                break;
            case InternalNameTag:
                if (!string.Equals(text, row.InternalName, StringComparison.Ordinal))
                {
                    page.EditInternalName(row, text);
                }

                break;
            case AliasesTag:
                if (!string.Equals(text, row.AliasesText, StringComparison.Ordinal))
                {
                    page.EditAliases(row, text);
                }

                break;
            case OutfitOfTag:
                page.EditOutfitOf(row, text);
                break;
            case HashesTag:
                page.EditHashes(row, text);
                break;
            case not null when tag.StartsWith(AttributeTagPrefix, StringComparison.Ordinal):
                page.EditAttribute(row, tag[AttributeTagPrefix.Length..], text);
                break;
        }
    }

    /// <summary>Whether a choice is the row being edited, which cannot be an outfit of itself.</summary>
    private static bool IsSelf(object? item, string? internalName) =>
        item is StudioOutfitChoice choice
        && string.Equals(choice.InternalName, internalName, StringComparison.OrdinalIgnoreCase);

    private static Control? Editor(Control? element) =>
        element switch
        {
            AutoCompleteBox or TextBox => element,
            null => null,
            _ => element.GetVisualDescendants().FirstOrDefault(c => c is AutoCompleteBox or TextBox) as Control,
        };

    private sealed class AttributeComparer(string attributeId) : IComparer
    {
        public int Compare(object? x, object? y) =>
            StringComparer.OrdinalIgnoreCase.Compare(
                (x as StudioCharacterRowViewModel)?.AttributeText(attributeId),
                (y as StudioCharacterRowViewModel)?.AttributeText(attributeId));
    }

    /// <summary>A right-click selects the row under it, so <em>Edit…</em> acts on that row.</summary>
    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TryToggleRowPick(e))
        {
            return;
        }

        if (!e.GetCurrentPoint(CharacterGrid).Properties.IsRightButtonPressed
            || (e.Source as Control)?.GetSelfAndVisualAncestors().OfType<DataGridRow>().FirstOrDefault()
                is not { DataContext: StudioCharacterRowViewModel row }
            || CharacterGrid.SelectedItems.Contains(row))
        {
            return;
        }

        CharacterGrid.SelectedItem = row;
    }

    /// <summary>While <em>Select</em> is on, a left click anywhere on a row ticks or unticks it.</summary>
    /// <remarks>Tunnelled and handled, before the grid reads a plain click as "edit this cell".</remarks>
    private bool TryToggleRowPick(PointerPressedEventArgs e)
    {
        if (_viewModel is not { IsSelectMode: true }
            || !e.GetCurrentPoint(CharacterGrid).Properties.IsLeftButtonPressed
            || (e.Source as Control)?.GetSelfAndVisualAncestors().OfType<DataGridRow>().FirstOrDefault()
                is not { DataContext: StudioCharacterRowViewModel row }
            || (e.Source as Control)?.GetSelfAndVisualAncestors().OfType<Button>().Any() == true)
        {
            return false;
        }

        e.Handled = true;

        if (CharacterGrid.SelectedItems.Contains(row))
        {
            CharacterGrid.SelectedItems.Remove(row);
        }
        else
        {
            CharacterGrid.SelectedItems.Add(row);
        }

        return true;
    }

    /// <summary>Shows or hides the tick-box column, found by its tag, since its visibility cannot be bound.</summary>
    private void ShowTickColumn(bool visible)
    {
        foreach (var column in CharacterGrid.Columns.Where(column => column.Tag is "tick"))
        {
            column.IsVisible = visible;
        }
    }

    /// <summary>Delete removes the selected characters, unless a cell is being typed in.</summary>
    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled
            || e.Key != Key.Delete
            || e.KeyModifiers != KeyModifiers.None
            || _viewModel is not { HasSelection: true } page
            || (e.Source as Control)?.GetSelfAndVisualAncestors().Any(c => c is TextBox or AutoCompleteBox or ComboBox) == true)
        {
            return;
        }

        e.Handled = true;
        page.DeleteSelected();
    }

    /// <summary>While the Character Manager is open, a picture dropped anywhere is its portrait.</summary>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (_viewModel is { CharacterManager.IsEditorShowing: true })
        {
            e.DragEffects = DroppedPictures.MightBePicture(e.DataTransfer) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (_viewModel is { CharacterManager.IsEditorShowing: true } page)
        {
            e.Handled = true;

            if (DroppedPictures.Read(e.DataTransfer) is { } picture)
            {
                _ = page.CharacterManager.DropPortraitAsync(picture);
            }
        }
    }

    private void SyncSelection()
    {
        if (_viewModel is null)
        {
            return;
        }

        var grid = CharacterGrid;

        // A grid may only select what it holds, and throws on anything else.
        var held = grid.ItemsSource as IList;
        IReadOnlyList<StudioCharacterRowViewModel> wanted = [.. _viewModel.SelectedRows.Where(row => held?.Contains(row) == true)];

        if (grid.SelectedItems.Count == wanted.Count && wanted.All(row => grid.SelectedItems.Contains(row)))
        {
            return;
        }

        _syncing = true;

        try
        {
            grid.SelectedItems.Clear();

            foreach (var row in wanted)
            {
                grid.SelectedItems.Add(row);
            }

            if (wanted.Count > 0)
            {
                grid.ScrollIntoView(wanted[0], null);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnRowsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _viewModel is null || sender is not DataGrid grid)
        {
            return;
        }

        _viewModel.UpdateSelection([.. grid.SelectedItems.Cast<StudioCharacterRowViewModel>()]);
    }

    // Reordering attributes in Game settings

    /// <summary>Starts dragging an attribute by its handle, captured by the list since the row can change.</summary>
    private void OnAttributeHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: StudioAttributeEditorViewModel attribute } handle
            || !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragging = attribute;
        handle.Focus();
        e.Pointer.Capture(AttributeList);
        MarkDragging(true);
        e.Handled = true;
    }

    private void OnAttributeListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging is not { } attribute || _viewModel?.GameSettings is not { } settings)
        {
            return;
        }

        var from = settings.Attributes.IndexOf(attribute);
        var to = AttributeIndexAt(e.GetPosition(AttributeList).Y);

        if (from >= 0 && to >= 0 && from != to)
        {
            MarkDragging(false);
            settings.MoveAttribute(from, to);
            MarkDragging(true);
        }
    }

    private void OnAttributeListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging is not null)
        {
            e.Pointer.Capture(null);
            EndAttributeDrag();
            e.Handled = true;
        }
    }

    private void OnAttributeListCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndAttributeDrag();

    private void EndAttributeDrag()
    {
        MarkDragging(false);
        _dragging = null;
    }

    /// <summary>Up and Down move the attribute whose handle has the focus, and the focus goes with it.</summary>
    private void OnAttributeHandleKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Control { DataContext: StudioAttributeEditorViewModel attribute }
            || _viewModel?.GameSettings is not { } settings
            || e.Key is not (Key.Up or Key.Down))
        {
            return;
        }

        if (e.Key == Key.Up)
        {
            settings.MoveAttributeUp(attribute);
        }
        else
        {
            settings.MoveAttributeDown(attribute);
        }

        e.Handled = true;
        Dispatcher.UIThread.Post(() => HandleOf(attribute)?.Focus());
    }

    /// <summary>Which place in the list a point is over: the first row whose middle is below it, or the last.</summary>
    private int AttributeIndexAt(double y)
    {
        var count = AttributeList.ItemCount;

        for (var i = 0; i < count; i++)
        {
            if (AttributeList.ContainerFromIndex(i) is { } container
                && y < container.Bounds.Top + (container.Bounds.Height / 2))
            {
                return i;
            }
        }

        return count - 1;
    }

    private void MarkDragging(bool on)
    {
        if (_dragging is { } attribute
            && AttributeList.ContainerFromItem(attribute) is { } container
            && container.GetVisualDescendants().OfType<Grid>().FirstOrDefault(g => g.Classes.Contains("attributeRow")) is { } row)
        {
            row.Classes.Set("dragging", on);
        }
    }

    private Border? HandleOf(StudioAttributeEditorViewModel attribute) =>
        AttributeList.ContainerFromItem(attribute) is { } container
            ? container.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("dragHandle"))
            : null;
}
