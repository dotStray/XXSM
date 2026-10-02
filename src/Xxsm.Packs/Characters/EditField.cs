namespace Xxsm.Packs.Characters;

/// <summary>One field of a character edit: left alone, or set to a value, where null clears it.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1000:Do not declare static members on generic types",
    Justification = "EditField<T>.To(x) and .Unchanged are the type's whole vocabulary. " +
                    "Moving them to a non-generic helper would force the caller to spell the " +
                    "type argument at every call site, which is exactly the noise this exists " +
                    "to avoid.")]
public readonly record struct EditField<T>
{
    private EditField(T? value, bool isSet)
    {
        Value = value;
        IsSet = isSet;
    }

    /// <summary>An edit that leaves the field alone. The default.</summary>
    public static EditField<T> Unchanged => default;

    /// <summary>Whether the edit has something to say about this field.</summary>
    public bool IsSet { get; }

    /// <summary>The value to set when <see cref="IsSet"/>; null with it set clears the field.</summary>
    public T? Value { get; }

    /// <summary>Sets the field to a value, or clears it when the value is null.</summary>
    /// <param name="value">The new value, or null to clear the field.</param>
    public static EditField<T> To(T? value) => new(value, isSet: true);

    /// <summary>Clears the field, so the merge falls back to the derived default.</summary>
    public static EditField<T> Cleared() => new(default, isSet: true);
}
