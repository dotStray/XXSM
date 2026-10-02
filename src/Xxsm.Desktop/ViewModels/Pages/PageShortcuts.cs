using CommunityToolkit.Mvvm.Input;

namespace Xxsm.Desktop.ViewModels.Pages;

/// <summary>A page with a Refresh button, which F5 presses.</summary>
public interface IRefreshablePage
{
    /// <summary>The command the page's Refresh button runs.</summary>
    IAsyncRelayCommand RefreshCommand { get; }
}

/// <summary>A page with a search box, which Ctrl+F focuses.</summary>
public interface ISearchablePage
{
    /// <summary>Shows the search box, on a page that keeps it hidden until asked.</summary>
    void OpenSearch();
}
