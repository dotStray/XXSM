using System.Text;

namespace Xxsm.Core.Text;

/// <summary>A table read from CSV or TSV text: its header and the rows under it.</summary>
/// <param name="Delimiter">The separator that was detected: a comma, a tab or a semicolon.</param>
/// <param name="Header">The first non-blank row's cells, trimmed.</param>
/// <param name="Rows">Every later row that is not entirely blank.</param>
/// <param name="UnterminatedQuote">Whether a quoted cell was still open at the end, so the rest was read as that
/// cell.</param>
public sealed record DelimitedTable(
    char Delimiter,
    IReadOnlyList<string> Header,
    IReadOnlyList<DelimitedRow> Rows,
    bool UnterminatedQuote);

/// <summary>One row of a <see cref="DelimitedTable"/>.</summary>
/// <param name="Line">The 1-based line the row starts on, as a spreadsheet program would number it.</param>
/// <param name="Cells">The row's cells, untrimmed. A row may have fewer or more cells than the header.</param>
public sealed record DelimitedRow(int Line, IReadOnlyList<string> Cells)
{
    /// <summary>A cell by column, or an empty string when the row is shorter than that.</summary>
    /// <param name="column">The 0-based column.</param>
    /// <returns>The cell's text.</returns>
    public string Cell(int column) => column >= 0 && column < Cells.Count ? Cells[column] : string.Empty;
}

/// <summary>Reads the CSV and TSV spreadsheet programs write, tolerantly. Never throws for content.</summary>
/// <remarks>The separator is whichever of tab, semicolon and comma the first row has most of, in that order on a
/// tie.</remarks>
public static class DelimitedText
{
    private const char Quote = '"';
    private const char ByteOrderMark = '﻿';

    /// <summary>Reads a table.</summary>
    /// <returns>The table; empty when there is nothing but blank lines.</returns>
    public static DelimitedTable Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length > 0 && text[0] == ByteOrderMark)
        {
            text = text[1..];
        }

        var delimiter = DetectDelimiter(text);
        var (records, unterminated) = ReadRecords(text, delimiter);

        records.RemoveAll(r => r.Cells.TrueForAll(string.IsNullOrWhiteSpace));

        if (records.Count == 0)
        {
            return new DelimitedTable(delimiter, [], [], unterminated);
        }

        return new DelimitedTable(
            delimiter,
            [.. records[0].Cells.Select(c => c.Trim())],
            [.. records.Skip(1).Select(r => new DelimitedRow(r.Line, r.Cells))],
            unterminated);
    }

    /// <summary>Works out the separator from the first row.</summary>
    /// <returns>A tab, a semicolon or a comma.</returns>
    public static char DetectDelimiter(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        int tabs = 0, semicolons = 0, commas = 0;
        var quoted = false;

        foreach (var character in text)
        {
            if (character == Quote)
            {
                quoted = !quoted;
                continue;
            }

            if (quoted)
            {
                continue;
            }

            if (character is '\n' or '\r')
            {
                break;
            }

            switch (character)
            {
                case '\t':
                    tabs++;
                    break;
                case ';':
                    semicolons++;
                    break;
                case ',':
                    commas++;
                    break;
            }
        }

        if (tabs > 0 && tabs >= semicolons && tabs >= commas)
        {
            return '\t';
        }

        return semicolons > 0 && semicolons >= commas ? ';' : ',';
    }

    private static (List<(int Line, List<string> Cells)> Records, bool Unterminated) ReadRecords(string text, char delimiter)
    {
        var records = new List<(int Line, List<string> Cells)>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var cellWasQuoted = false;
        var line = 1;
        var recordLine = 1;

        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];

            if (quoted)
            {
                if (character == Quote)
                {
                    if (i + 1 < text.Length && text[i + 1] == Quote)
                    {
                        cell.Append(Quote);
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }

                    continue;
                }

                if (character == '\n')
                {
                    line++;
                }

                cell.Append(character);
                continue;
            }

            // A quote is special only at the start of a cell; elsewhere it is text: 5" Disc.
            if (character == Quote && cell.Length == 0 && !cellWasQuoted)
            {
                quoted = true;
                cellWasQuoted = true;
                continue;
            }

            if (character == delimiter)
            {
                cells.Add(cell.ToString());
                cell.Clear();
                cellWasQuoted = false;
                continue;
            }

            if (character is '\r' or '\n')
            {
                cells.Add(cell.ToString());
                cell.Clear();
                cellWasQuoted = false;
                records.Add((recordLine, cells));
                cells = [];

                if (character == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                line++;
                recordLine = line;
                continue;
            }

            cell.Append(character);
        }

        if (cell.Length > 0 || cells.Count > 0 || cellWasQuoted)
        {
            cells.Add(cell.ToString());
            records.Add((recordLine, cells));
        }

        return (records, quoted);
    }
}
