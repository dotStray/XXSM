using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Xxsm.Desktop.ViewModels;
using Xxsm.Packs.Loading;

namespace Xxsm.Desktop.Views.Panels;

/// <summary>The Character Manager's editor and delete question, over whichever page opened them.</summary>
public partial class CharacterManagerView : UserControl
{
    private CharacterManagerViewModel? _manager;
    private CharacterEditorViewModel? _editor;

    /// <summary>Creates the view.</summary>
    public CharacterManagerView() => InitializeComponent();

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Attach(DataContext as CharacterManagerViewModel);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Attach(DataContext as CharacterManagerViewModel);
    }

    /// <inheritdoc />
    /// <remarks>Unsubscribes: the manager outlives this view.</remarks>
    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        Attach(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void Attach(CharacterManagerViewModel? manager)
    {
        if (ReferenceEquals(manager, _manager))
        {
            return;
        }

        if (_manager is not null)
        {
            _manager.PropertyChanged -= OnManagerChanged;
        }

        _manager = manager;

        if (_manager is not null)
        {
            _manager.PropertyChanged += OnManagerChanged;
        }

        WatchEditor(_manager?.Editor);
    }

    private void OnManagerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CharacterManagerViewModel.Editor))
        {
            WatchEditor(_manager?.Editor);
        }
    }

    private void WatchEditor(CharacterEditorViewModel? editor)
    {
        if (_editor is not null)
        {
            _editor.PropertyChanged -= OnEditorChanged;
        }

        _editor = editor;

        if (_editor is not null)
        {
            _editor.PropertyChanged += OnEditorChanged;
            FocusRequested(_editor);
        }
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CharacterEditorViewModel.FocusField) && sender is CharacterEditorViewModel editor)
        {
            FocusRequested(editor);
        }
    }

    /// <summary>Puts the caret in the field a Problems panel button asked for, once it is laid out.</summary>
    private void FocusRequested(CharacterEditorViewModel editor)
    {
        if (editor.FocusField is not { } field)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                if (!ReferenceEquals(editor, _editor) || editor.FocusField != field)
                {
                    return;
                }

                // The portrait is not a text box: focusing it lets Ctrl+V and the menu reach it.
                if (field == PackDiagnosticFields.Image)
                {
                    PortraitArea.Focus();
                }
                else
                {
                    TextBox box = field switch
                    {
                        PackDiagnosticFields.InternalName => InternalNameBox,
                        PackDiagnosticFields.ModFilesName => ModFilesNameBox,
                        _ => NameBox,
                    };

                    box.Focus();
                    box.SelectAll();
                }

                editor.FocusField = null;
            },
            DispatcherPriority.Loaded);
    }
}
