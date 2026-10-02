using Xxsm.Core.Settings;

namespace Xxsm.Desktop.ViewModels;

/// <summary>The table column widths the user set, kept in <c>settings.json</c>; writes run one at a time.</summary>
public sealed class ColumnWidthMemory(IAppSettingsStore settings, ViewModelWorkRunner runner, ITextCatalogue text)
{
    private readonly IAppSettingsStore _settings = settings;
    private readonly ViewModelWorkRunner _runner = runner;
    private readonly ITextCatalogue _text = text;
    private readonly Dictionary<string, Dictionary<string, double>> _widths = new(StringComparer.Ordinal);
    private Task _writing = Task.CompletedTask;

    /// <summary>Raised when the saved widths have been read, so a table already on screen can take them.</summary>
    public event EventHandler? Loaded;

    /// <summary>Raised when a table's widths are forgotten, with the table's name, to size afresh.</summary>
    public event EventHandler<string>? Forgotten;

    /// <summary>Takes the widths from settings just read.</summary>
    public void Load(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _widths.Clear();

        foreach (var (table, columns) in settings.ColumnWidths)
        {
            _widths[table] = new Dictionary<string, double>(columns, StringComparer.Ordinal);
        }

        Loaded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>One table's remembered widths, by column id. Empty when none have been set.</summary>
    public IReadOnlyDictionary<string, double> For(string table) =>
        _widths.TryGetValue(table, out var columns) ? columns : new Dictionary<string, double>();

    /// <summary>Remembers new widths for some of a table's columns, and writes them to the settings file.</summary>
    /// <param name="table">The table.</param>
    /// <param name="widths">The columns that changed, by column id.</param>
    /// <returns>A task that completes when the file is written, or its failure has been reported.</returns>
    public Task Remember(string table, IReadOnlyDictionary<string, double> widths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentNullException.ThrowIfNull(widths);

        if (widths.Count == 0)
        {
            return _writing;
        }

        if (!_widths.TryGetValue(table, out var columns))
        {
            _widths[table] = columns = new Dictionary<string, double>(StringComparer.Ordinal);
        }

        foreach (var (column, width) in widths)
        {
            columns[column] = width;
        }

        var copy = new Dictionary<string, double>(widths, StringComparer.Ordinal);
        _writing = WriteAfterAsync(_writing, table, copy);
        return _writing;
    }

    /// <summary>Forgets every width the user set in one table, in the settings file too.</summary>
    /// <returns>A task that completes when the file is written, or its failure has been reported.</returns>
    public Task Forget(string table)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        _widths.Remove(table);
        Forgotten?.Invoke(this, table);

        _writing = ForgetAfterAsync(_writing, table);
        return _writing;
    }

    private async Task ForgetAfterAsync(Task previous, string table)
    {
        await previous.ConfigureAwait(true);

        await _runner.RunAsync(
            _text[nameof(Strings.Settings_Heading)],
            ct => _settings.UpdateAsync(current => current.WithoutColumnWidths(table), ct),
            CancellationToken.None).ConfigureAwait(true);
    }

    private async Task WriteAfterAsync(Task previous, string table, IReadOnlyDictionary<string, double> widths)
    {
        await previous.ConfigureAwait(true);

        // Not a page's token: a width the user set is written even if they leave the page.
        await _runner.RunAsync(
            _text[nameof(Strings.Settings_Heading)],
            ct => _settings.UpdateAsync(current => current.WithColumnWidths(table, widths), ct),
            CancellationToken.None).ConfigureAwait(true);
    }
}
