using System.Net;
using System.Text;

namespace Xxsm.Core.GameBanana;

/// <summary>Turns a mod's <c>_sText</c> HTML into plain text, keeping no markup at all.</summary>
/// <remarks>
/// The HTML is hostile. <c>script</c>, <c>style</c> and the like lose their contents too; entities are decoded
/// only after the tags are gone, so an encoded tag stays text.
/// </remarks>
public static class GameBananaText
{
    /// <summary>The most HTML this will look at, in characters. Longer input is cut before scanning.</summary>
    public const int InputLimit = 256 * 1024;

    /// <summary>How much plain text is kept when a description is written into <c>.xxsm/mod.json</c>.</summary>
    public const int StoredLimit = 4000;

    /// <summary>How long a summary runs before it is cut at a word boundary.</summary>
    public const int SummaryLimit = 220;

    private static readonly string[] OpaqueElements = ["script", "style", "iframe", "noscript", "svg"];

    private static readonly string[] BreakElements =
    [
        "br", "p", "div", "li", "ul", "ol", "hr", "tr", "table", "blockquote", "pre",
        "h1", "h2", "h3", "h4", "h5", "h6", "section", "article", "header", "footer",
    ];

    /// <summary>Strips a mod description's HTML down to plain text.</summary>
    /// <param name="html">The <c>_sText</c> body, or null.</param>
    /// <returns>Plain text with paragraphs and list items as line breaks, or null when there was only markup.</returns>
    public static string? ToPlainText(string? html)
    {
        if (html is null || html.Length == 0)
        {
            return null;
        }

        var source = html.Length > InputLimit ? html.AsSpan(0, InputLimit) : html.AsSpan();
        var stripped = Scan(source);
        var decoded = WebUtility.HtmlDecode(stripped);
        var text = Tidy(decoded);

        return text.Length == 0 ? null : text;
    }

    /// <summary>Shortens plain text to its first paragraph, on one line, cut at a word with an ellipsis.</summary>
    /// <param name="text">Plain text, as <see cref="ToPlainText"/> returns.</param>
    /// <param name="limit">How many characters to allow before cutting.</param>
    /// <returns>The shortened text, or null when there is nothing to show.</returns>
    public static string? Summarise(string? text, int limit = SummaryLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        if (text is null)
        {
            return null;
        }

        var end = text.IndexOf("\n\n", StringComparison.Ordinal);
        var paragraph = (end >= 0 ? text[..end] : text).Replace('\n', ' ');

        var builder = new StringBuilder(paragraph.Length);
        var space = false;

        foreach (var c in paragraph)
        {
            if (c == ' ')
            {
                space = true;
                continue;
            }

            if (space && builder.Length > 0)
            {
                builder.Append(' ');
            }

            space = false;
            builder.Append(c);
        }

        var single = builder.ToString();

        if (single.Length == 0)
        {
            return null;
        }

        if (single.Length <= limit)
        {
            return single;
        }

        var cut = single.LastIndexOf(' ', Math.Min(limit, single.Length - 1));

        return (cut > limit / 2 ? single[..cut] : single[..limit]).TrimEnd() + "…";
    }

    /// <summary>Cuts plain text down to what is kept in <c>.xxsm/mod.json</c>.</summary>
    /// <param name="text">Plain text, as <see cref="ToPlainText"/> returns.</param>
    /// <returns>The text, or its first <see cref="StoredLimit"/> characters with an ellipsis.</returns>
    public static string? ForStorage(string? text)
    {
        if (text is null || text.Length <= StoredLimit)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', StoredLimit - 1);

        return (cut > StoredLimit / 2 ? text[..cut] : text[..(StoredLimit - 1)]).TrimEnd() + "…";
    }

    private static string Scan(ReadOnlySpan<char> html)
    {
        var builder = new StringBuilder(html.Length);
        var index = 0;

        while (index < html.Length)
        {
            var c = html[index];

            if (c != '<')
            {
                builder.Append(c);
                index++;
                continue;
            }

            if (html[index..].StartsWith("<!--", StringComparison.Ordinal))
            {
                var close = html[index..].IndexOf("-->", StringComparison.Ordinal);
                index = close < 0 ? html.Length : index + close + 3;
                continue;
            }

            var tag = ReadTagName(html[index..], out var length, out var closing);

            if (length == 0)
            {
                // A bare '<' that starts no tag is text: "a < b".
                builder.Append(c);
                index++;
                continue;
            }

            if (!closing && OpaqueElements.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                index = SkipElement(html, index + length, tag);
                continue;
            }

            var isItem = tag.Equals("li", StringComparison.OrdinalIgnoreCase);

            // A closing </li> gets no break: the next <li> or the list's end is the break.
            if (BreakElements.Contains(tag, StringComparer.OrdinalIgnoreCase) && !(closing && isItem))
            {
                builder.Append('\n');

                if (!closing && isItem)
                {
                    builder.Append("- ");
                }
            }

            index += length;
        }

        return builder.ToString();
    }

    /// <summary>Reads "&lt;name" or "&lt;/name"; <paramref name="length"/> 0 means no tag.</summary>
    private static string ReadTagName(ReadOnlySpan<char> from, out int length, out bool closing)
    {
        length = 0;
        closing = false;

        var at = 1;

        if (at < from.Length && from[at] == '/')
        {
            closing = true;
            at++;
        }

        var start = at;

        while (at < from.Length && (char.IsAsciiLetterOrDigit(from[at]) || from[at] == '-'))
        {
            at++;
        }

        if (at == start)
        {
            return string.Empty;
        }

        var name = from[start..at].ToString();
        var close = from[at..].IndexOf('>');

        length = close < 0 ? from.Length : at + close + 1;

        return name;
    }

    private static int SkipElement(ReadOnlySpan<char> html, int from, string tag)
    {
        var rest = html[from..];
        var index = 0;

        while (index < rest.Length)
        {
            var next = rest[index..].IndexOf('<');

            if (next < 0)
            {
                return html.Length;
            }

            index += next;
            var name = ReadTagName(rest[index..], out var length, out var closing);

            if (length == 0)
            {
                index++;
                continue;
            }

            if (closing && name.Equals(tag, StringComparison.OrdinalIgnoreCase))
            {
                return from + index + length;
            }

            index += length;
        }

        return html.Length;
    }

    private static string Tidy(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        var output = new StringBuilder(text.Length);
        var blank = 0;

        foreach (var line in lines)
        {
            var collapsed = CollapseSpaces(line);

            if (collapsed.Length == 0)
            {
                blank++;
                continue;
            }

            if (output.Length > 0)
            {
                output.Append(blank > 0 ? "\n\n" : "\n");
            }

            blank = 0;
            output.Append(collapsed);
        }

        return output.ToString();
    }

    private static string CollapseSpaces(string line)
    {
        var builder = new StringBuilder(line.Length);
        var space = false;

        foreach (var c in line)
        {
            // Non-breaking spaces too: &nbsp; must not survive as U+00A0, which no layout wraps on.
            if (char.IsWhiteSpace(c))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
