using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AutoMapping.Reporting;

/// <summary>Remembers when each schedule last ran so a restart neither repeats nor silently skips a report.</summary>
public interface IReportStateStore
{
    DateTimeOffset? GetLastRun(string key);
    void SetLastRun(string key, DateTimeOffset value);
}

public sealed class FileReportStateStore : IReportStateStore
{
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private Dictionary<string, DateTimeOffset> _state = new();

    public FileReportStateStore(string directory, ILogger logger)
    {
        _path = Path.Combine(directory, "state.json");
        _logger = logger;
        try
        {
            if (File.Exists(_path)) _state = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // a corrupt state file must not stop reporting: start fresh and say so
            _logger.LogWarning(ex, "The reporting state file '{Path}' could not be read; starting without history.", _path);
        }
    }

    public DateTimeOffset? GetLastRun(string key)
    {
        lock (_lock) return _state.TryGetValue(key, out var value) ? value : null;
    }

    public void SetLastRun(string key, DateTimeOffset value)
    {
        lock (_lock)
        {
            _state[key] = value;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_state));
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Could not persist the reporting state to '{Path}'. After a restart the last report may be repeated or missed.", _path);
            }
        }
    }
}

public sealed class InMemoryReportStateStore : IReportStateStore
{
    private readonly Dictionary<string, DateTimeOffset> _state = new();
    public DateTimeOffset? GetLastRun(string key) => _state.TryGetValue(key, out var v) ? v : null;
    public void SetLastRun(string key, DateTimeOffset value) => _state[key] = value;
}
