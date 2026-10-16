using System.Net;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

internal sealed class SteamMailService
{
    private const int MaxBodyCharacters = 24_000;
    private const int MaxPreviewCharacters = 240;
    private readonly SteamMailStore _store;
    private readonly ILogger<SteamMailService> _logger;

    public SteamMailService(SteamMailStore store, ILogger<SteamMailService> logger)
    {
        _store = store;
        _logger = logger;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CS2_HUB_IMAP_HOST")) &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CS2_HUB_IMAP_USERNAME")) &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CS2_HUB_IMAP_PASSWORD"));

    public async Task<bool> SyncAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured) return false;

        try
        {
            var host = Environment.GetEnvironmentVariable("CS2_HUB_IMAP_HOST")!;
            var port = ParsePort();
            var username = Environment.GetEnvironmentVariable("CS2_HUB_IMAP_USERNAME")!;
            var password = Environment.GetEnvironmentVariable("CS2_HUB_IMAP_PASSWORD")!;
            var folderName = Environment.GetEnvironmentVariable("CS2_HUB_IMAP_FOLDER") ?? "INBOX";

            using var client = new ImapClient();
            await client.ConnectAsync(host, port, SecureSocketOptions.SslOnConnect, cancellationToken);
            await client.AuthenticateAsync(username, password, cancellationToken);

            var folder = await client.GetFolderAsync(folderName, cancellationToken);
            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
            var since = DateTime.UtcNow.AddDays(-GetRetentionDays());
            var uids = await folder.SearchAsync(SearchQuery.DeliveredAfter(since), cancellationToken);
            var summaries = await folder.FetchAsync(uids, MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.BodyStructure | MessageSummaryItems.Flags | MessageSummaryItems.InternalDate, cancellationToken);
            var records = new List<SteamMailRecord>();

            foreach (var summary in summaries.OrderByDescending(item => item.InternalDate ?? item.Envelope?.Date ?? DateTimeOffset.MinValue))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sender = summary.Envelope?.From?.Mailboxes.FirstOrDefault()?.Address
                    ?? summary.Envelope?.From?.FirstOrDefault()?.ToString()
                    ?? "Unbekannter Absender";
                var groupId = SteamMailRouting.ClassifySender(sender);
                if (groupId is null) continue;

                var body = await ReadTextBodyAsync(folder, summary, cancellationToken);
                var subject = Normalize(summary.Envelope?.Subject, "(Ohne Betreff)", 300);
                var receivedUtc = summary.InternalDate?.ToUniversalTime()
                    ?? summary.Envelope?.Date?.ToUniversalTime()
                    ?? DateTimeOffset.UtcNow;
                var remoteKey = BuildRemoteKey(folder.FullName, summary);
                records.Add(new SteamMailRecord
                {
                    Id = StableId(remoteKey),
                    RemoteKey = remoteKey,
                    Subject = subject,
                    Sender = Normalize(sender, "Unbekannter Absender", 320),
                    ReceivedUtc = receivedUtc,
                    IsRead = summary.Flags?.HasFlag(MessageFlags.Seen) == true,
                    Body = body,
                    Preview = NormalizePreview(body),
                    GroupId = groupId,
                });
            }

            _store.Merge(records);
            await client.DisconnectAsync(true, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _store.SetSyncError(exception);
            _logger.LogWarning("Steam Mail sync failed ({ExceptionType}).", exception.GetType().Name);
            return false;
        }
    }

    private static async Task<string> ReadTextBodyAsync(IMailFolder folder, IMessageSummary summary, CancellationToken cancellationToken)
    {
        if (summary.TextBody is not { } textPart) return string.Empty;
        var entity = await folder.GetBodyPartAsync(summary.UniqueId, textPart, cancellationToken);
        return NormalizeBody(entity is TextPart text ? text.Text : entity.ToString());
    }

    private static string NormalizeBody(string? value)
    {
        var body = string.IsNullOrWhiteSpace(value) ? string.Empty : value;
        body = Regex.Replace(body, "[\\t ]+", " ");
        body = Regex.Replace(body, "\\n{3,}", "\\n\\n");
        return body.Trim().Length > MaxBodyCharacters ? body.Trim()[..MaxBodyCharacters] : body.Trim();
    }

    private static string NormalizePreview(string body)
    {
        var preview = Regex.Replace(body, "\\s+", " ").Trim();
        return preview.Length > MaxPreviewCharacters ? preview[..MaxPreviewCharacters].TrimEnd() + "…" : preview;
    }

    private static string Normalize(string? value, string fallback, int maxLength)
    {
        var normalized = WebUtility.HtmlDecode(value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized.Length > maxLength ? normalized[..maxLength] : normalized;
    }

    private static string BuildRemoteKey(string folderName, IMessageSummary summary) =>
        $"{folderName}:{summary.UniqueId.Id}:{summary.Envelope?.MessageId ?? summary.Envelope?.Date?.ToUnixTimeSeconds().ToString() ?? "unknown"}";

    private static Guid StableId(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return new Guid(bytes[..16]);
    }

    private static int ParsePort() =>
        int.TryParse(Environment.GetEnvironmentVariable("CS2_HUB_IMAP_PORT"), out var port)
            ? Math.Clamp(port, 1, 65535)
            : 993;

    private static int GetRetentionDays() =>
        int.TryParse(Environment.GetEnvironmentVariable("CS2_HUB_IMAP_RETENTION_DAYS"), out var days)
            ? Math.Clamp(days, 1, 365)
            : 30;
}

internal sealed class SteamMailSyncWorker : BackgroundService
{
    private readonly SteamMailService _service;
    private readonly ILogger<SteamMailSyncWorker> _logger;

    public SteamMailSyncWorker(SteamMailService service, ILogger<SteamMailSyncWorker> logger)
    {
        _service = service;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await _service.SyncAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { _logger.LogWarning("Steam Mail worker failed ({ExceptionType}).", exception.GetType().Name); }

            var seconds = int.TryParse(Environment.GetEnvironmentVariable("CS2_HUB_IMAP_SYNC_SECONDS"), out var configured)
                ? Math.Clamp(configured, 60, 3600)
                : 300;
            await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
        }
    }
}
