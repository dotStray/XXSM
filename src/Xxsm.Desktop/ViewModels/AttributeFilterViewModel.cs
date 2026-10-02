using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Xxsm.Desktop.ViewModels;

/// <summary>One filter chip in the character grid's toolbar: one value of a declared attribute.</summary>
public sealed partial class AttributeFilterValueViewModel : ObservableObject
{
    private readonly Action _onChanged;

    /// <summary>Creates a chip.</summary>
    /// <param name="id">The value's stable id, as stored on a variant.</param>
    /// <param name="displayName">The label on the chip.</param>
    /// <param name="onChanged">Called whenever <see cref="IsSelected"/> is toggled.</param>
    public AttributeFilterValueViewModel(string id, string displayName, Action onChanged)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(onChanged);

        Id = id;
        DisplayName = displayName;
        _onChanged = onChanged;
    }

    /// <summary>The value's stable id.</summary>
    public string Id { get; }

    /// <summary>The label on the chip.</summary>
    public string DisplayName { get; }

    /// <summary>Whether this value is part of the active filter.</summary>
    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _onChanged();
}

/// <summary>One declared attribute and its chips — a "Element" or "Weapon" group in the filter toolbar.</summary>
public sealed class AttributeFilterGroupViewModel
{
    /// <summary>Creates a group.</summary>
    /// <param name="id">The attribute's stable id.</param>
    /// <param name="displayName">The group's label.</param>
    /// <param name="values">Its chips, in declaration or observed order.</param>
    public AttributeFilterGroupViewModel(
        string id, string displayName, IReadOnlyList<AttributeFilterValueViewModel> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(values);

        Id = id;
        DisplayName = displayName;
        Values = new ObservableCollection<AttributeFilterValueViewModel>(values);
    }

    /// <summary>The attribute's stable id.</summary>
    public string Id { get; }

    /// <summary>The group's label.</summary>
    public string DisplayName { get; }

    /// <summary>Its chips.</summary>
    public ObservableCollection<AttributeFilterValueViewModel> Values { get; }

    /// <summary>The currently selected value ids in this group.</summary>
    public IReadOnlySet<string> SelectedIds =>
        new HashSet<string>(
            Values.Where(value => value.IsSelected).Select(value => value.Id),
            StringComparer.Ordinal);
}
