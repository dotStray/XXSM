using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Xxsm.Desktop.Controls;
using Xxsm.Desktop.Services;
using Xxsm.Desktop.ViewModels;
using Xxsm.Desktop.ViewModels.Pages;

namespace Xxsm.Desktop.Views.Pages;

/// <summary>The character detail view.</summary>
public partial class CharacterDetailPage : UserControl
{
    /// <summary>The mod list's name among the remembered column widths.</summary>
    public const string WidthsTable = "mods";

    /// <summary>The detail pane's name among the remembered widths, when it has been dragged.</summary>
    public const string PaneTable = "modsPane";

    /// <summary>The pane's one remembered width in <see cref="PaneTable"/>.</summary>
    private const string PaneColumn = "detail";

    /// <summary>The detail pane's width when the page has the room for it.</summary>
    private const double PaneWide = 340;

    /// <summary>The narrowest the detail pane goes: its fields still hold a mod's name.</summary>
    private const double PaneNarrow = 260;

    /// <summary>How far one press of an arrow key moves the line.</summary>
    private const double PaneKeyStep = 10;

    /// <summary>The width the mod list is left with before the pane starts to give any up.</summary>
    private const double ListRoom = 300;

    private readonly DataGridColumnSizer _sizer;
    private readonly DataGridCurrentRow _currentRow;
    private bool _isBarShown;

    /// <summary>The width the user dragged the pane to, or its default; a narrow window can give it less.</summary>
    private double _chosenPane = PaneWide;

    private bool _isDraggingPane;
    private bool _hasPaneMoved;
    private double _dragFrom;
    private double _dragStartWidth;

    /// <summary>Creates the view.</summary>
    public CharacterDetailPage()
    {
        InitializeComponent();

        _sizer = new DataGridColumnSizer(ModsGrid, MeasureHost);
        _sizer.Resized += (_, widths) => Model?.ColumnWidths.Remember(WidthsTable, widths);
        _currentRow = new DataGridCurrentRow(ModsGrid);

        // An empty menu is a blank box: the Unsorted page has no character to edit.
        EditMenu.Opening += (_, e) => e.Cancel = EditMenu.ItemCount == 0;

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);

        AddHandler(ContextRequestedEvent, (_, e) => Model?.PrepareRowMenu(RowOf(e.Source)), RoutingStrategies.Tunnel);

        SizeChanged += (_, _) => SizeDetailPane();

        PaneHandle.PointerPressed += OnPaneHandlePressed;
        PaneHandle.PointerMoved += OnPaneHandleMoved;
        PaneHandle.PointerReleased += OnPaneHandleReleased;
        PaneHandle.PointerCaptureLost += (_, _) => _isDraggingPane = false;
        PaneHandle.KeyDown += OnPaneHandleKeyDown;

        // A DataGridColumn inherits no DataContext, so its visibility is set from here.
        DataContextChanged += (_, _) => BindSelectMode();
    }

    /// <summary>Keeps the detail pane at the chosen width while there is room, narrowing it when not.</summary>
    private void SizeDetailPane()
    {
        if (Bounds.Width <= 0)
        {
            return;
        }

        var width = new GridLength(Math.Clamp(Math.Min(_chosenPane, MostPane()), PaneNarrow, PaneWide));

        foreach (var grid in new[] { ToolsGrid, RowsGrid })
        {
            if (grid.ColumnDefinitions[1].Width != width)
            {
                grid.ColumnDefinitions[1].Width = width;
            }
        }
    }

    /// <summary>The widest the pane can be at the page's width now: the default, less what the list needs.</summary>
    private double MostPane() => Math.Clamp(Math.Round(Bounds.Width - ListRoom), PaneNarrow, PaneWide);

    /// <summary>Takes the pane's width from the remembered widths, or its default when none was kept.</summary>
    private void LoadPaneWidth(CharacterDetailPageViewModel model)
    {
        _chosenPane = model.ColumnWidths.For(PaneTable).TryGetValue(PaneColumn, out var width)
            ? Math.Clamp(width, PaneNarrow, PaneWide)
            : PaneWide;
        SizeDetailPane();
    }

    /// <summary>A press on the line starts a drag; a double-click puts back the default width.</summary>
    private void OnPaneHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(PaneHandle).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;

        if (e.ClickCount == 2)
        {
            _isDraggingPane = false;
            _chosenPane = PaneWide;
            SizeDetailPane();
            Model?.ColumnWidths.Forget(PaneTable);
            return;
        }

        _isDraggingPane = true;
        _hasPaneMoved = false;
        _dragFrom = e.GetPosition(this).X;
        _dragStartWidth = RowsGrid.ColumnDefinitions[1].ActualWidth;
        e.Pointer.Capture(PaneHandle);
    }

    /// <summary>Follows the pointer: the line is the pane's left edge, held to the allowed range.</summary>
    private void OnPaneHandleMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingPane)
        {
            return;
        }

        var moved = e.GetPosition(this).X - _dragFrom;
        _hasPaneMoved |= Math.Abs(moved) >= 1;
        _chosenPane = Math.Clamp(Math.Round(_dragStartWidth - moved), PaneNarrow, MostPane());
        SizeDetailPane();
    }

    /// <summary>Ends a drag and remembers the width; the default is not a choice to keep, so it is forgotten.</summary>
    private void OnPaneHandleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDraggingPane)
        {
            return;
        }

        _isDraggingPane = false;
        e.Pointer.Capture(null);

        if (!_hasPaneMoved || Model is not { } model)
        {
            return;
        }

        RememberPane(model);
    }

    /// <summary>Moves the line from the keyboard: Left and Right a step, Home widest, End narrowest.</summary>
    private void OnPaneHandleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        var shown = RowsGrid.ColumnDefinitions[1].ActualWidth;
        double? wanted = e.Key switch
        {
            Key.Left => shown + PaneKeyStep,
            Key.Right => shown - PaneKeyStep,
            Key.Home => PaneWide,
            Key.End => PaneNarrow,
            _ => null,
        };

        if (wanted is not { } width)
        {
            return;
        }

        e.Handled = true;
        _chosenPane = Math.Clamp(Math.Round(width), PaneNarrow, MostPane());
        SizeDetailPane();

        if (Model is { } model)
        {
            RememberPane(model);
        }
    }

    /// <summary>Keeps the width the user chose; the default is not a choice to keep, so it is forgotten.</summary>
    private void RememberPane(CharacterDetailPageViewModel model)
    {
        if (_chosenPane >= PaneWide)
        {
            model.ColumnWidths.Forget(PaneTable);
        }
        else
        {
            model.ColumnWidths.Remember(PaneTable, new Dictionary<string, double> { [PaneColumn] = _chosenPane });
        }
    }

    /// <summary>Keeps the tick-box column in step with <c>IsSelectMode</c>.</summary>
    private void BindSelectMode()
    {
        if (_watching is { } previous)
        {
            previous.PropertyChanged -= OnModelPropertyChanged;
            previous.SelectionRestoreRequested -= OnSelectionRestoreRequested;
            previous.ColumnWidths.Loaded -= OnWidthsLoaded;
            _watching = null;
        }

        if (Model is not { } model)
        {
            return;
        }

        _watching = model;
        _isBarShown = model.IsSelectionBarVisible;
        model.PropertyChanged += OnModelPropertyChanged;
        model.SelectionRestoreRequested += OnSelectionRestoreRequested;
        model.ColumnWidths.Loaded += OnWidthsLoaded;
        ShowColumn("tick", model.IsSelectMode);
        ShowColumn("update", model.ShowsUpdates);
        _sizer.Apply(model.ColumnWidths.For(WidthsTable));
        LoadPaneWidth(model);
    }

    private void OnWidthsLoaded(object? sender, EventArgs e)
    {
        if (Model is { } model)
        {
            _sizer.Apply(model.ColumnWidths.For(WidthsTable));
            LoadPaneWidth(model);
        }
    }

    /// <summary>Puts a multi-row selection back after a rescan; only the current row is bindable.</summary>
    private void OnSelectionRestoreRequested(object? sender, IReadOnlyList<ModRowViewModel> rows)
    {
        foreach (var row in rows.Where(row => !ModsGrid.SelectedItems.Contains(row)))
        {
            ModsGrid.SelectedItems.Add(row);
        }
    }

    private void OnModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (Model is not { } model)
        {
            return;
        }

        if (e.PropertyName == nameof(CharacterDetailPageViewModel.IsSelectMode))
        {
            ShowColumn("tick", model.IsSelectMode);
        }
        else if (e.PropertyName == nameof(CharacterDetailPageViewModel.ShowsUpdates))
        {
            ShowColumn("update", model.ShowsUpdates);
        }
        else if (e.PropertyName == nameof(CharacterDetailPageViewModel.IsSelectionBarVisible))
        {
            if (model.IsSelectionBarVisible && !_isBarShown)
            {
                _currentRow.KeepInSight();
            }

            _isBarShown = model.IsSelectionBarVisible;
        }
    }

    /// <summary>Shows or hides a column, found by its tag, since a column's visibility cannot be bound.</summary>
    private void ShowColumn(string tag, bool visible)
    {
        foreach (var column in ModsGrid.Columns.Where(column => Equals(column.Tag, tag)))
        {
            column.IsVisible = visible;
        }
    }

    /// <summary>While <em>Select</em> is on, a left click anywhere on a row ticks or unticks it.</summary>
    /// <remarks>Tunnelled and handled, before the grid's own click empties the selection.</remarks>
    private bool TryToggleRowPick(PointerPressedEventArgs e)
    {
        if (Model is not { IsSelectMode: true }
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || RowOf(e.Source) is not { } row
            || IsInside<ToggleSwitch>(e.Source))
        {
            return false;
        }

        e.Handled = true;

        if (ModsGrid.SelectedItems.Contains(row))
        {
            ModsGrid.SelectedItems.Remove(row);
        }
        else
        {
            ModsGrid.SelectedItems.Add(row);
        }

        return true;
    }

    private static bool IsInside<T>(object? source) =>
        (source as Control)?.GetSelfAndVisualAncestors().OfType<T>().Any() == true;

    /// <summary>The mod-picture area a pointer or a drag is over, or null.</summary>
    private static Control? ImageTargetOf(object? source) =>
        (source as Control)
            ?.GetSelfAndVisualAncestors()
            .OfType<Control>()
            .FirstOrDefault(control => control.Classes.Contains("imageTarget"));

    /// <summary>Clicking the picture focuses it for Ctrl+V; a click on a row in <em>Select</em> is a pick.</summary>
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TryToggleRowPick(e))
        {
            return;
        }

        ImageTargetOf(e.Source)?.Focus();
    }

    /// <summary>The mod whose row a pointer or a drag is over, or null.</summary>
    private static ModRowViewModel? RowOf(object? source) =>
        (source as Control)
            ?.GetSelfAndVisualAncestors()
            .OfType<DataGridRow>()
            .FirstOrDefault()
            ?.DataContext as ModRowViewModel;

    private CharacterDetailPageViewModel? Model => DataContext as CharacterDetailPageViewModel;

    private CharacterDetailPageViewModel? _watching;

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (Model is { CharacterManager.IsEditorShowing: true } editing)
        {
            e.DragEffects = DroppedPictures.MightBePicture(e.DataTransfer) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            editing.IsDragOver = false;
            return;
        }

        var path = PathOf(e);

        // An image over a mod's picture or row is that mod's new picture, never a mod to install.
        if ((ImageTargetOf(e.Source) is not null || RowOf(e.Source) is not null)
            && DroppedPictures.MightBePicture(e.DataTransfer))
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;

            if (Model is { } over)
            {
                over.IsDragOver = false;
            }

            return;
        }

        e.DragEffects = path is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;

        if (Model is { } model)
        {
            model.IsDragOver = path is not null;
        }
    }

    /// <inheritdoc />
    /// <remarks>The paste handler goes on the window, so the key is seen with nothing on the page focused.</remarks>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        TopLevel.GetTopLevel(this)?.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        TopLevel.GetTopLevel(this)?.RemoveHandler(KeyDownEvent, OnKeyDown);

        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Ctrl+V with no text box focused: a GameBanana address on the clipboard starts an install.</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Model is not { } model || !PagePaste.IsForPage(this, e))
        {
            return;
        }

        e.Handled = true;
        _ = model.PasteAddressAsync();
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (Model is { } model)
        {
            model.IsDragOver = false;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;

        if (Model is not { } model)
        {
            return;
        }

        if (model.CharacterManager.IsEditorShowing)
        {
            model.IsDragOver = false;

            if (DroppedPictures.Read(e.DataTransfer) is { } portrait)
            {
                _ = model.CharacterManager.DropPortraitAsync(portrait);
            }

            return;
        }

        var row = RowOf(e.Source);

        if ((row is not null || ImageTargetOf(e.Source) is not null) && DroppedPictures.Read(e.DataTransfer) is { } picture)
        {
            model.IsDragOver = false;
            _ = row is not null ? model.DropModImageAsync(row, picture) : model.DropModImageAsync(picture);
            return;
        }

        if (PathOf(e) is { } path)
        {
            _ = model.DropAsync(path);
        }
        else
        {
            model.IsDragOver = false;
        }
    }

    /// <summary>The one local path a drag carries, or null; a multi-file drop is refused as ambiguous.</summary>
    private static string? PathOf(DragEventArgs e)
    {
        var items = e.DataTransfer.TryGetFiles()?.ToList();

        return items is { Count: 1 } ? items[0].TryGetLocalPath() : null;
    }

    /// <summary>Forwards the grid's selection to the view model, since <c>SelectedItems</c> is not bindable.</summary>
    private void OnModsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is CharacterDetailPageViewModel viewModel && sender is DataGrid grid)
        {
            viewModel.UpdateSelection([.. grid.SelectedItems.Cast<ModRowViewModel>()]);
        }
    }

    /// <summary>Opens or closes the search box, and puts the caret in it when it opens.</summary>
    private void OnSearchToggled(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CharacterDetailPageViewModel viewModel)
        {
            return;
        }

        viewModel.ToggleSearchCommand.Execute(null);

        if (viewModel.IsSearchOpen)
        {
            SearchBox.Focus();
        }
    }
}
