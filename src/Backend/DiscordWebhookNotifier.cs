using System.Net;
using System.Net.Http.Json;

internal sealed class DiscordWebhookNotifier
{
    private readonly HttpClient _httpClient;

    public DiscordWebhookNotifier(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
    }

    public bool IsConfigured => TryGetWebhookUrl() is not null;

    public Task<bool> SendCooldownDoneAsync(AccountRecord account, CancellationToken cancellationToken) =>
        SendAccountEventAsync(
            account,
            $"Cooldown beendet: {account.PersonaName ?? account.SteamId}",
            "Der CS2-Cooldown ist beendet.",
            0x2ECC71,
            cancellationToken);

    public async Task<bool> SendAccountEventAsync(
        AccountRecord account,
        string title,
        string description,
        int color,
        CancellationToken cancellationToken)
    {
        var webhookUrl = TryGetWebhookUrl();
        if (webhookUrl is null) return false;

        var payload = new
        {
            username = "CS2 Account Hub",
            allowed_mentions = new { parse = Array.Empty<string>() },
            embeds = new[]
            {
                new
                {
                    title,
                    description = $"{description}\n<t:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:F>",
                    color,
                    thumbnail = string.IsNullOrWhiteSpace(account.AvatarUrl) ? null : new { url = account.AvatarUrl },
                    fields = new[]
                    {
                        Field("Account", account.PersonaName ?? "Unbekannt", true),
                        Field("Steam-ID", account.SteamId, true),
                        Field("Prime", account.Prime.ToString(), true),
                        Field("Premier Rating", account.PremierRating?.ToString() ?? "—", true),
                        Field("Premier Wins", account.PremierWins?.ToString() ?? "—", true),
                        Field("CS2 Level", account.Cs2Level?.ToString() ?? "—", true),
                        Field("VAC", account.VacBanned switch { true => "Ja", false => "Nein", _ => "Unbekannt" }, true),
                        Field("Cooldown-Grund", account.CooldownReason?.ToString() ?? "—", true),
                        Field("Letzter Login", account.LastLoginUtc?.ToString("u") ?? "—", false),
                        Field("Notiz", account.Note ?? "—", false),
                    },
                    footer = new { text = "CS2 Account Hub" },
                }
            }
        };

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(webhookUrl, payload, cancellationToken);
            if (response.IsSuccessStatusCode) return true;

            NfaLoader.Services.AppLog.Warn($"Discord webhook rejected an account event ({(int)response.StatusCode}).");
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            NfaLoader.Services.AppLog.Warn($"Discord account event failed ({exception.GetType().Name}).");
            return false;
        }
    }

    private static object Field(string name, string value, bool inline) => new
    {
        name,
        value = Truncate(value, 1024),
        inline,
    };

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";

    private static string? TryGetWebhookUrl()
    {
        var raw = Environment.GetEnvironmentVariable("CS2_HUB_DISCORD_WEBHOOK_URL");
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var trustedHost = string.Equals(uri.Host, "discord.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".discord.com", StringComparison.OrdinalIgnoreCase);
        return trustedHost
            && uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal)
            ? uri.AbsoluteUri
            : null;
    }
}

internal sealed class CooldownNotificationWorker : BackgroundService
{
    private readonly AccountStore _accounts;
    private readonly DiscordWebhookNotifier _notifier;
    private readonly ILogger<CooldownNotificationWorker> _logger;

    public CooldownNotificationWorker(AccountStore accounts, DiscordWebhookNotifier notifier, ILogger<CooldownNotificationWorker> logger)
    {
        _accounts = accounts;
        _notifier = notifier;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            do
            {
                await ScanAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        if (!_notifier.IsConfigured) return;

        foreach (var account in _accounts.List(null, null))
        {
            if (account.CooldownNotificationSent || account.CooldownEndsUtc is null || account.CooldownEndsUtc > DateTimeOffset.UtcNow)
            {
                continue;
            }

            try
            {
                if (await _notifier.SendCooldownDoneAsync(account, cancellationToken))
                {
                    account.CooldownNotificationSent = true;
                    _accounts.Upsert(account);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning("Cooldown notification failed for account {AccountId} ({ExceptionType}).", account.Id, exception.GetType().Name);
            }
        }
    }
}
