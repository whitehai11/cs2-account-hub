using System.Text.Json;

internal sealed class AccountEventStore
{
    private const int MaxEvents = 5000;
    private readonly object _gate = new();
    private readonly List<AccountEventRecord> _events = new();
    private readonly string _path;

    public AccountEventStore()
    {
        var dataDirectory = Environment.GetEnvironmentVariable("CS2_HUB_DATA_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "account-events.json");

        try
        {
            var json = File.Exists(_path) ? File.ReadAllText(_path) : "[]";
            _events.AddRange(JsonSerializer.Deserialize<List<AccountEventRecord>>(json) ?? []);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not load account timeline ({exception.GetType().Name}).");
        }
    }

    public AccountEventRecord Append(Guid accountId, string type, string title, string? detail = null, string severity = "info")
    {
        var item = new AccountEventRecord
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Type = type,
            Title = title,
            Detail = detail,
            Severity = severity,
            OccurredUtc = DateTimeOffset.UtcNow,
        };

        lock (_gate)
        {
            _events.Add(item);
            if (_events.Count > MaxEvents)
            {
                _events.RemoveRange(0, _events.Count - MaxEvents);
            }
            SaveUnsafe();
        }

        return item;
    }

    public IReadOnlyList<AccountEventRecord> List(int limit, Guid? accountId = null, string? type = null)
    {
        lock (_gate)
        {
            return _events
                .Where(item => accountId is null || item.AccountId == accountId)
                .Where(item => string.IsNullOrWhiteSpace(type) || string.Equals(item.Type, type, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.OccurredUtc)
                .Take(Math.Clamp(limit, 1, 500))
                .ToArray();
        }
    }

    private void SaveUnsafe()
    {
        var temporaryPath = _path + ".tmp";
        var json = JsonSerializer.Serialize(_events, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _path, overwrite: true);
    }
}

internal sealed class AccountEventRecord
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string Type { get; set; } = "status";
    public string Title { get; set; } = "Status aktualisiert";
    public string? Detail { get; set; }
    public string Severity { get; set; } = "info";
    public DateTimeOffset OccurredUtc { get; set; }
}
