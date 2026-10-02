using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Xxsm.Desktop.Views.Pages;

/// <summary>Whether a Ctrl+V on a page is the page's (a GameBanana address) or the focused box's.</summary>
internal static class PagePaste
{
    /// <summary>Whether a key press is Ctrl+V the page should handle.</summary>
    /// <param name="page">The page whose handler is asking.</param>
    /// <param name="e">The key press.</param>
    /// <returns>True when it is Ctrl+V, nothing handled it already, and no text box has the focus.</returns>
    public static bool IsForPage(Visual page, KeyEventArgs e) =>
        !e.Handled
        && e.Key == Key.V
        && e.KeyModifiers.HasFlag(KeyModifiers.Control)
        && TopLevel.GetTopLevel(page)?.FocusManager?.GetFocusedElement() is not (TextBox or AutoCompleteBox);
}
