using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Xxsm.Desktop.Services;
using Xxsm.Desktop.ViewModels;
using Xxsm.Desktop.ViewModels.Pages;

namespace Xxsm.Desktop.Views.Pages;

/// <summary>The character grid, with drag-and-drop install; what a drop means is the view model's.</summary>
public partial class CharactersPage : UserControl
{
    /// <summary>Creates the view.</summary>
    public CharactersPage()
    {
        InitializeComponent();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
    }

    private CharactersPageViewModel? Model => DataContext as CharactersPageViewModel;

    /// <inheritdoc />
    /// <remarks>The paste handler goes on the window, so the key is seen with nothing on the grid focused.</remarks>
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

    /// <summary>Ctrl+V with no text box focused: a GameBanana address opens the prompt; auto-sort files it.</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Model is not { } model || !PagePaste.IsForPage(this, e))
        {
            return;
        }

        e.Handled = true;
        _ = model.PasteAddressAsync();
    }

    /// <summary>Clicking the editor's picture focuses it for Ctrl+V; not handled, so the menu still opens.</summary>
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e) =>
        (e.Source as Control)
            ?.GetSelfAndVisualAncestors()
            .OfType<Control>()
            .FirstOrDefault(control => control.Classes.Contains("imageTarget"))
            ?.Focus();

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var path = PathOf(e);

        // With the editor open a drop can only mean its picture: two panels never stack.
        if (Model is { CharacterManager.IsEditorShowing: true } editing)
        {
            e.DragEffects = DroppedPictures.MightBePicture(e.DataTransfer) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            editing.IsDragOver = false;
            return;
        }

        e.DragEffects = path is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;

        if (Model is { } model)
        {
            model.IsDragOver = path is not null;
        }
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

        if (Model is { CharacterManager.IsEditorShowing: true } editing)
        {
            editing.IsDragOver = false;

            if (DroppedPictures.Read(e.DataTransfer) is { } picture)
            {
                _ = editing.CharacterManager.DropPortraitAsync(picture);
            }

            return;
        }

        if (Model is not { } model || PathOf(e) is not { } path)
        {
            if (Model is { } cancelled)
            {
                cancelled.IsDragOver = false;
            }

            return;
        }

        // Dropped on a tile overrides the sorter; dropped on the grid lets it decide.
        var tile = (e.Source as Control)
            ?.GetSelfAndVisualAncestors()
            .OfType<Control>()
            .Select(control => control.DataContext)
            .OfType<CharacterTileViewModel>()
            .FirstOrDefault();

        _ = model.DropAsync(path, tile);
    }

    /// <summary>The one local path a drag carries, or null; a multi-file drop is refused as ambiguous.</summary>
    private static string? PathOf(DragEventArgs e)
    {
        var items = e.DataTransfer.TryGetFiles()?.ToList();

        return items is { Count: 1 } ? items[0].TryGetLocalPath() : null;
    }
}
