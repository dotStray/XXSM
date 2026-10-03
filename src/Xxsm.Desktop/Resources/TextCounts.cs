namespace Xxsm.Desktop.Resources;

/// <summary>Counted nouns, in the singular when there is one of them.</summary>
internal static class TextCounts
{
    /// <summary>A count of mods.</summary>
    /// <returns>For example <c>1 mod</c> or <c>26 mods</c>.</returns>
    public static string Mods(this ITextCatalogue text, int count) =>
        Of(text, count, nameof(Strings.Count_Mod_One), nameof(Strings.Count_Mod_Many));

    /// <summary>A count of days.</summary>
    /// <returns>For example <c>1 day</c> or <c>30 days</c>.</returns>
    public static string Days(this ITextCatalogue text, int count) =>
        Of(text, count, nameof(Strings.Count_Day_One), nameof(Strings.Count_Day_Many));

    /// <summary>A count of old pack versions.</summary>
    /// <returns>For example <c>1 old version</c> or <c>2 old versions</c>.</returns>
    public static string OldVersions(this ITextCatalogue text, int count) =>
        Of(text, count, nameof(Strings.Count_OldVersion_One), nameof(Strings.Count_OldVersion_Many));

    /// <summary>A count of key bindings.</summary>
    /// <returns>For example <c>1 key</c> or <c>7 keys</c>.</returns>
    public static string Keys(this ITextCatalogue text, int count) =>
        Of(text, count, nameof(Strings.Count_Key_One), nameof(Strings.Count_Key_Many));

    /// <summary>A count of downloads.</summary>
    /// <returns>For example <c>1 download</c> or <c>2 downloads</c>.</returns>
    public static string Downloads(this ITextCatalogue text, int count) =>
        Of(text, count, nameof(Strings.Count_Download_One), nameof(Strings.Count_Download_Many));

    /// <summary>A count of hashes.</summary>
    /// <returns>For example <c>1 hash</c> or <c>5 hashes</c>.</returns>
    public static string Hashes(this ITextCatalogue text, int count) =>
        Of(text, count, nameof(Strings.Count_Hash_One), nameof(Strings.Count_Hash_Many));

    /// <summary>A count of files.</summary>
    /// <returns>For example <c>1 file</c> or <c>4 files</c>.</returns>
    public static string Files(this ITextCatalogue text, int count) =>
        Of(text, count, nameof(Strings.Count_File_One), nameof(Strings.Count_File_Many));

    /// <summary>A count of overlay fields.</summary>
    /// <returns>For example <c>1 field</c> or <c>3 fields</c>.</returns>
    public static string Fields(this ITextCatalogue text, int count) =>
        Of(text, count, nameof(Strings.Count_Field_One), nameof(Strings.Count_Field_Many));

    /// <summary>A count of characters.</summary>
    /// <returns>For example <c>1 character</c> or <c>4 characters</c>.</returns>
    public static string Characters(this ITextCatalogue text, int count) =>
        Of(text, count, nameof(Strings.Count_Character_One), nameof(Strings.Count_Character_Many));

    /// <summary>A count of skins.</summary>
    /// <returns>For example <c>1 skin</c> or <c>28 skins</c>.</returns>
    public static string Skins(this ITextCatalogue text, int count) =>
        Of(text, count, nameof(Strings.Count_Skin_One), nameof(Strings.Count_Skin_Many));

    /// <summary>What a pack holds, as its card says it.</summary>
    /// <returns>For example <c>124 characters and 28 skins, 19 still waiting for hashes.</c></returns>
    public static string PackContents(this ITextCatalogue text, Xxsm.Packs.Loading.PackContents contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        var line = text.Characters(contents.Characters);

        if (contents.Skins > 0)
        {
            line = text.Format(nameof(Strings.Packs_Contents_Skins), line, text.Skins(contents.Skins));
        }

        if (contents.WaitingForHashes > 0)
        {
            line = text.Format(nameof(Strings.Packs_Contents_Waiting), line, contents.WaitingForHashes);
        }

        return text.Format(nameof(Strings.Packs_Contents), line);
    }

    /// <summary>A count of folders.</summary>
    /// <returns>For example <c>1 folder</c> or <c>10 folders</c>.</returns>
    public static string Folders(this ITextCatalogue text, int count) =>
        Of(text, count, nameof(Strings.Count_Folder_One), nameof(Strings.Count_Folder_Many));

    /// <summary>A count of things inside a folder, whatever they are.</summary>
    /// <returns>For example <c>1 item</c> or <c>3 items</c>.</returns>
    public static string Items(this ITextCatalogue text, int count) =>
        Of(text, count, nameof(Strings.Count_Item_One), nameof(Strings.Count_Item_Many));

    /// <summary>A count of errors.</summary>
    /// <returns>For example <c>1 error</c> or <c>3 errors</c>.</returns>
    public static string Errors(this ITextCatalogue text, int count) =>
        Of(text, count, nameof(Strings.Count_Error_One), nameof(Strings.Count_Error_Many));

    /// <summary>A count of warnings.</summary>
    /// <returns>For example <c>1 warning</c> or <c>14 warnings</c>.</returns>
    public static string Warnings(this ITextCatalogue text, int count) =>
        Of(text, count, nameof(Strings.Count_Warning_One), nameof(Strings.Count_Warning_Many));

    /// <summary>A sentence with a singular and a plural entry, for when more than the noun changes.</summary>
    /// <param name="text">The interface's wording.</param>
    /// <param name="count">How many the sentence is about.</param>
    /// <param name="oneKey">The entry for exactly one.</param>
    /// <param name="manyKey">The entry for any other number.</param>
    /// <param name="arguments">What goes in the sentence's gaps, in both entries alike.</param>
    public static string ForCount(
        this ITextCatalogue text, int count, string oneKey, string manyKey, params object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(text);

        return text.Format(count == 1 ? oneKey : manyKey, arguments);
    }

    private static string Of(ITextCatalogue text, int count, string oneKey, string manyKey)
    {
        ArgumentNullException.ThrowIfNull(text);

        return count == 1 ? text[oneKey] : text.Format(manyKey, count);
    }
}
