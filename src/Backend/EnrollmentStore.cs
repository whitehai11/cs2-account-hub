using System.Text.Json;
using CS2AccountHub.Contracts;

internal sealed class EnrollmentStore
{
    public const int DailyLimit = 2;
    private const string SignupUrl = "https://store.steampowered.com/join/";
    private readonly object _gate = new();
    private readonly List<EnrollmentAttempt> _attempts = new();
    private readonly string _path;

    public EnrollmentStore()
    {
        var dataDirectory = Environment.GetEnvironmentVariable("CS2_HUB_DATA_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "enrollment-activity.json");

        try
        {
            if (File.Exists(_path))
            {
                _attempts.AddRange(JsonSerializer.Deserialize<List<EnrollmentAttempt>>(File.ReadAllText(_path)) ?? []);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not load enrollment activity ({exception.GetType().Name}).");
        }
    }

    public EnrollmentStatusDto GetStatus()
    {
        lock (_gate)
        {
            return StatusUnsafe(DateTimeOffset.UtcNow);
        }
    }

    public bool TryStart(string? domain, out EnrollmentSessionDto session, out EnrollmentStatusDto status)
    {
        lock (_gate)
        {
            var startedUtc = DateTimeOffset.UtcNow;
            status = StatusUnsafe(startedUtc);
            if (status.Remaining <= 0)
            {
                session = null!;
                return false;
            }

            var normalizedDomain = NormalizeDomain(domain);
            var attempt = new EnrollmentAttempt
            {
                Id = Guid.NewGuid(),
                Domain = normalizedDomain,
                StartedUtc = startedUtc,
            };
            _attempts.Add(attempt);
            SaveUnsafe();
            status = StatusUnsafe(startedUtc);
            session = new EnrollmentSessionDto(attempt.Id, SignupUrl, normalizedDomain, startedUtc, status);
            return true;
        }
    }

    private EnrollmentStatusDto StatusUnsafe(DateTimeOffset now)
    {
        var today = now.UtcDateTime.Date;
        _attempts.RemoveAll(attempt => attempt.StartedUtc.UtcDateTime.Date < today.AddDays(-30));
        var used = _attempts.Count(attempt => attempt.StartedUtc.UtcDateTime.Date == today);
        return new EnrollmentStatusDto(new DateTimeOffset(today, TimeSpan.Zero), DailyLimit, used, Math.Max(0, DailyLimit - used));
    }

    private void SaveUnsafe()
    {
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_attempts, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, _path, overwrite: true);
    }

    private static string? NormalizeDomain(string? domain)
    {
        var normalized = domain?.Trim().TrimStart('@').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 253) return null;
        return normalized.All(character => char.IsLetterOrDigit(character) || character is '.' or '-') ? normalized : null;
    }
}

internal sealed class EnrollmentAttempt
{
    public Guid Id { get; set; }
    public string? Domain { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
}
