using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xxsm.Desktop.Services;
using Xxsm.Desktop.ViewModels;

namespace Xxsm.Desktop.Views.Panels;

/// <summary>The mod installer's confirm step, shared by the character grid and the character detail view.</summary>
public partial class ModInstallView : UserControl
{
    /// <summary>Creates the view.</summary>
    public ModInstallView()
    {
        InitializeComponent();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);

        DataContextChanged += (_, _) => Watch();
        Watch();
    }

    private ModInstallViewModel? _watching;

    /// <summary>Follows the panel's state so the address box can take the caret the moment it appears.</summary>
    private void Watch()
    {
        if (_watching is { } previous)
        {
            previous.PropertyChanged -= OnModelChanged;
        }

        _watching = DataContext as ModInstallViewModel;

        if (_watching is { } current)
        {
            current.PropertyChanged += OnModelChanged;
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ModInstallViewModel.IsAskingForAddress)
            || _watching is not { IsAskingForAddress: true })
        {
            return;
        }

        // After the layout pass that shows the box: focusing a control not there yet does nothing.
        Dispatcher.UIThread.Post(() => AddressBox.Focus(), DispatcherPriority.Input);
    }

    private static Control? ImageTargetOf(object? source) =>
        (source as Control)
            ?.GetSelfAndVisualAncestors()
            .OfType<Control>()
            .FirstOrDefault(control => control.Classes.Contains("imageTarget"));

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e) => ImageTargetOf(e.Source)?.Focus();

    // Handled only over a card's picture; anything else goes on to the page.
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (ImageTargetOf(e.Source) is not null && DroppedPictures.MightBePicture(e.DataTransfer))
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (ImageTargetOf(e.Source) is { DataContext: InstallRowViewModel row }
            && DroppedPictures.Read(e.DataTransfer) is { } picture)
        {
            e.Handled = true;
            _ = row.DropPictureAsync(picture);
        }
    }
}
