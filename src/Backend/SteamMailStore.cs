using System.Text.Json;
using CS2AccountHub.Contracts;

internal sealed class SteamMailStore
{
    private const int MaxMessages = 500;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, SteamMailRecord> _messages = new();
    private readonly string _path;
    private DateTimeOffset? _lastSyncUtc;
    private string? _lastSyncError;

    public SteamMailStore()
    {
        var dataDirectory = Environment.GetEnvironmentVariable("CS2_HUB_DATA_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "steam-mail.json");

        try
        {
            var state = File.Exists(_path)
                ? JsonSerializer.Deserialize<SteamMailState>(File.ReadAllText(_path))
                : null;
            if (state is not null)
            {
                _lastSyncUtc = state.LastSyncUtc;
                _lastSyncError = state.LastSyncError;
                foreach (var message in state.Messages ?? []) _messages[message.Id] = message;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not load Steam Mail state ({exception.GetType().Name}).");
        }
    }

    public SteamMailSnapshot Snapshot(string? query, bool? unread, string? groupId, int limit)
    {
        lock (_gate)
        {
            var normalizedQuery = query?.Trim();
            var matchingMessages = _messages.Values
                .Where(message => unread is null || message.IsRead != unread.Value)
                .Where(message => string.IsNullOrWhiteSpace(normalizedQuery)
                    || message.Subject.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                    || message.Sender.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                    || message.Preview.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var messages = matchingMessages
                .Where(message => string.IsNullOrWhiteSpace(groupId) || string.Equals(groupId, "all", StringComparison.OrdinalIgnoreCase) || string.Equals(message.GroupId, groupId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(message => message.ReceivedUtc)
                .Take(Math.Clamp(limit, 1, 100))
                .Select(ToDto)
                .ToArray();

            return new SteamMailSnapshot(messages, SteamMailRouting.Summarize(matchingMessages), _lastSyncUtc, _lastSyncError, IsConfigured());
        }
    }

    public SteamMailDetailDto? Get(Guid id)
    {
        lock (_gate)
        {
            var message = _messages.GetValueOrDefault(id);
            return message is null
                ? null
                : new SteamMailDetailDto(message.Id, message.Subject, message.Sender, message.ReceivedUtc, message.IsRead, message.Body, message.GroupId);
        }
    }

    public IReadOnlyList<SteamMailRecord> RecentSteamGuardMessages(DateTimeOffset sinceUtc, IReadOnlyCollection<string> allowedDomains)
    {
        if (allowedDomains.Count == 0) return [];

        lock (_gate)
        {
            return _messages.Values
                .Where(message => message.ReceivedUtc >= sinceUtc)
                .Where(message => SteamMailRouting.ClassifySender(message.Sender, message.GroupId) is not null)
                .Where(message => message.Recipients.Any(recipient => allowedDomains.Any(domain => MatchesDomain(recipient, domain))))
                .OrderByDescending(message => message.ReceivedUtc)
                .ToArray();
        }
    }

    public bool MarkRead(Guid id, bool isRead)
    {
        lock (_gate)
        {
            if (!_messages.TryGetValue(id, out var message)) return false;
            message.IsRead = isRead;
            SaveUnsafe();
            return true;
        }
    }

    public void Merge(IReadOnlyList<SteamMailRecord> messages)
    {
        lock (_gate)
        {
            foreach (var message in messages)
            {
                if (_messages.TryGetValue(message.Id, out var existing))
                {
                    existing.Subject = message.Subject;
                    existing.Sender = message.Sender;
                    existing.ReceivedUtc = message.ReceivedUtc;
                    existing.Preview = message.Preview;
                    existing.Body = message.Body;
                    existing.RemoteKey = message.RemoteKey;
                    existing.GroupId = message.GroupId;
                    existing.Recipients = message.Recipients;
                }
                else
                {
                    _messages[message.Id] = message;
                }
            }

            var cutoff = DateTimeOffset.UtcNow.AddDays(-GetRetentionDays());
            foreach (var id in _messages.Values.Where(message => message.ReceivedUtc < cutoff).Select(message => message.Id).ToArray())
            {
                _messages.Remove(id);
            }

            while (_messages.Count > MaxMessages)
            {
                var oldest = _messages.Values.OrderBy(message => message.ReceivedUtc).First();
                _messages.Remove(oldest.Id);
            }

            _lastSyncUtc = DateTimeOffset.UtcNow;
            _lastSyncError = null;
            SaveUnsafe();
        }
    }

    public void SetSyncError(Exception exception)
    {
        lock (_gate)
        {
            _lastSyncUtc = DateTimeOffset.UtcNow;
            _lastSyncError = exception is OperationCanceledException ? "Synchronisierung abgebrochen." : "Mailbox konnte nicht synchronisiert werden.";
            SaveUnsafe();
        }
    }

    private static bool IsConfigured() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CS2_HUB_IMAP_HOST")) &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CS2_HUB_IMAP_USERNAME")) &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CS2_HUB_IMAP_PASSWORD"));

    private static int GetRetentionDays() =>
        int.TryParse(Environment.GetEnvironmentVariable("CS2_HUB_IMAP_RETENTION_DAYS"), out var days)
            ? Math.Clamp(days, 1, 365)
            : 30;

    private static SteamMailMessageDto ToDto(SteamMailRecord message) =>
        new(message.Id, message.Subject, message.Sender, message.ReceivedUtc, message.IsRead, message.Preview, !string.IsNullOrWhiteSpace(message.Body), message.GroupId);

    private void SaveUnsafe()
    {
        var temporaryPath = _path + ".tmp";
        var state = new SteamMailState(_lastSyncUtc, _lastSyncError, _messages.Values.OrderByDescending(message => message.ReceivedUtc).ToArray());
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, _path, overwrite: true);
    }

    private static bool MatchesDomain(string recipient, string allowedDomain)
    {
        var at = recipient.LastIndexOf('@');
        if (at < 0) return false;
        var domain = recipient[(at + 1)..].Trim().TrimEnd('>').ToLowerInvariant();
        var normalizedAllowed = allowedDomain.Trim().TrimStart('@').ToLowerInvariant();
        return domain == normalizedAllowed || domain.EndsWith($".{normalizedAllowed}", StringComparison.Ordinal);
    }
}

internal sealed record SteamMailSnapshot(
    IReadOnlyList<SteamMailMessageDto> Messages,
    IReadOnlyList<SteamMailGroupDto> Groups,
    DateTimeOffset? LastSyncUtc,
    string? LastSyncError,
    bool Configured);

internal sealed class SteamMailState
{
    public SteamMailState() { }

    public SteamMailState(DateTimeOffset? lastSyncUtc, string? lastSyncError, IReadOnlyList<SteamMailRecord> messages)
    {
        LastSyncUtc = lastSyncUtc;
        LastSyncError = lastSyncError;
        Messages = messages.ToList();
    }

    public DateTimeOffset? LastSyncUtc { get; set; }
    public string? LastSyncError { get; set; }
    public List<SteamMailRecord>? Messages { get; set; }
}

internal sealed class SteamMailRecord
{
    public Guid Id { get; set; }
    public string RemoteKey { get; set; } = string.Empty;
    public string Subject { get; set; } = "(Ohne Betreff)";
    public string Sender { get; set; } = "Unbekannter Absender";
    public DateTimeOffset ReceivedUtc { get; set; }
    public bool IsRead { get; set; }
    public string Preview { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string GroupId { get; set; } = "primary";
    public List<string> Recipients { get; set; } = [];
}
