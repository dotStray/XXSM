namespace Xxsm.Core.Io;

/// <summary>Reads the real user id from <c>/proc/self/status</c>, once per process.</summary>
public sealed class ProcUserIdProvider : IUserIdProvider
{
    private const string StatusPath = "/proc/self/status";

    private readonly Lazy<int?> _userId;

    /// <summary>Creates a provider that reads the real <c>/proc/self/status</c>.</summary>
    public ProcUserIdProvider() : this(StatusPath)
    {
    }

    /// <summary>Creates a provider that reads a specific status file. For tests.</summary>
    /// <param name="statusFilePath">Path to a file in <c>/proc/self/status</c> format.</param>
    public ProcUserIdProvider(string statusFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statusFilePath);
        _userId = new Lazy<int?>(() => Read(statusFilePath), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public int? TryGetUserId() => _userId.Value;

    private static int? Read(string statusFilePath)
    {
        try
        {
            if (!File.Exists(statusFilePath))
            {
                return null;
            }

            foreach (var line in File.ReadLines(statusFilePath))
            {
                if (!line.StartsWith("Uid:", StringComparison.Ordinal))
                {
                    continue;
                }

                // "Uid:\treal\teffective\tsaved\tfilesystem"
                var fields = line[4..].Split(
                    ['\t', ' '],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (fields.Length > 0 && int.TryParse(fields[0], out var uid))
                {
                    return uid;
                }

                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }
}
