namespace Xxsm.Desktop.ViewModels.Pages;

/// <summary>One destination in the navigation rail, made once and reused, so it releases all on leaving.</summary>
public abstract class PageViewModel : ViewModelBase
{
    /// <summary>Creates the page.</summary>
    /// <param name="text">The interface's wording, with the user's <c>text.json</c> applied.</param>
    protected PageViewModel(ITextCatalogue text)
    {
        ArgumentNullException.ThrowIfNull(text);

        Text = text;
    }

    /// <summary>The heading shown at the top of the page.</summary>
    public abstract string Heading { get; }

    /// <summary>Goes back to the page's own first screen, when its rail entry is pressed again.</summary>
    public virtual void ReturnToTop()
    {
    }

    /// <summary>The interface's wording.</summary>
    protected ITextCatalogue Text { get; }
}
