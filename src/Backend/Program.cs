using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using CS2AccountHub.Contracts;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

var dataDirectory = Environment.GetEnvironmentVariable("CS2_HUB_DATA_DIR")
    ?? Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDirectory);

builder.Services.AddSingleton<AccountStore>();
builder.Services.AddSingleton<AccountEventStore>();
builder.Services.AddSingleton<SteamMailStore>();
builder.Services.AddSingleton<SteamMailService>();
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<OperationStore>();
builder.Services.AddSingleton<AuthSessionStore>();
builder.Services.AddSingleton<OperationQueue>();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")))
    .SetApplicationName("CS2AccountHub");
builder.Services.AddSingleton<TokenProtector>();
builder.Services.AddSingleton<WebSessionService>();
builder.Services.AddSingleton<SteamPublicProfileService>();
builder.Services.AddSingleton<ISteamAccountGateway, SteamGateway>();
builder.Services.AddHostedService<OperationWorker>();
builder.Services.AddHostedService<AccountCheckScheduler>();
builder.Services.AddHttpClient<DiscordWebhookNotifier>();
builder.Services.AddHostedService<CooldownNotificationWorker>();
builder.Services.AddHostedService<ProfileEnrichmentWorker>();
builder.Services.AddHostedService<SteamMailSyncWorker>();
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 3 * 1024 * 1024);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 180,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
    options.AddPolicy("web-login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 8,
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));
    options.AddPolicy("steam-auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 6,
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));
});

var app = builder.Build();
var apiKey = Environment.GetEnvironmentVariable("CS2_HUB_API_KEY");
var webSessions = app.Services.GetRequiredService<WebSessionService>();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: https:; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers["Cache-Control"] = "no-store";
    }

    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    var isApiRequest = context.Request.Path.StartsWithSegments("/api");
    var requestPath = context.Request.Path.Value;
    var isWebAuthRequest = string.Equals(requestPath, "/api/web/login", StringComparison.OrdinalIgnoreCase)
        || string.Equals(requestPath, "/api/web/me", StringComparison.OrdinalIgnoreCase)
        || string.Equals(requestPath, "/api/web/logout", StringComparison.OrdinalIgnoreCase);
    var apiKeyAccepted = !string.IsNullOrWhiteSpace(apiKey)
        && context.Request.Headers.TryGetValue("X-CS2-Hub-Key", out var supplied)
        && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(supplied.ToString()),
            System.Text.Encoding.UTF8.GetBytes(apiKey));

    if (isApiRequest && !isWebAuthRequest && !webSessions.IsAuthenticated(context) && !apiKeyAccepted)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/web/login", (WebLoginRequest request, WebSessionService sessions, HttpResponse response) =>
{
    if (!RequestValidation.IsValidWebLogin(request))
    {
        return Results.BadRequest(new { message = "Invalid dashboard credentials format." });
    }

    var expectedUser = Environment.GetEnvironmentVariable("CS2_HUB_WEB_USERNAME");
    var expectedPassword = Environment.GetEnvironmentVariable("CS2_HUB_WEB_PASSWORD");
    if (string.IsNullOrWhiteSpace(expectedUser) || string.IsNullOrWhiteSpace(expectedPassword) ||
        !WebSessionService.ConstantTimeEquals(request.Username, expectedUser) ||
        !WebSessionService.ConstantTimeEquals(request.Password, expectedPassword))
    {
        return Results.Json(new { message = "Invalid dashboard credentials." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    sessions.SignIn(response, request.Username.Trim());
    return Results.Ok(new { authenticated = true, username = request.Username.Trim() });
}).RequireRateLimiting("web-login");

app.MapGet("/api/web/me", (WebSessionService sessions, HttpContext context) =>
{
    var username = sessions.GetUsername(context);
    return username is null
        ? Results.Json(new { message = "Authentication required." }, statusCode: StatusCodes.Status401Unauthorized)
        : Results.Ok(new { authenticated = true, username });
});

app.MapPost("/api/web/logout", (WebSessionService sessions, HttpResponse response) =>
{
    sessions.SignOut(response);
    return Results.NoContent();
});

app.MapGet("/api/accounts", (AccountStore store, AccountSection? section, AccountPrimeBucket? prime) =>
{
    var accounts = store.List(section, prime)
        .Select(AccountMappings.ToSummary)
        .ToArray();
    return Results.Ok(accounts);
});

app.MapGet("/api/accounts/{id:guid}", (Guid id, AccountStore store) =>
{
    var account = store.Get(id);
    return account is null ? Results.NotFound() : Results.Ok(AccountMappings.ToDetail(account));
});

app.MapPatch("/api/accounts/{id:guid}/note", (Guid id, UpdateNoteRequest request, AccountStore store) =>
{
    if (!RequestValidation.IsValidNote(request)) return Results.BadRequest(new { message = "The note is too long." });
    return store.UpdateNote(id, request.Note)
        ? Results.NoContent()
        : Results.NotFound();
});

app.MapPatch("/api/accounts/{id:guid}/prime", (Guid id, SetPrimeRequest request, AccountStore store) =>
{
    return store.UpdatePrime(id, request.Prime)
        ? Results.NoContent()
        : Results.NotFound();
});

app.MapPatch("/api/accounts/{id:guid}/perma-green", (Guid id, SetPermaGreenRequest request, AccountStore store, AccountEventStore events) =>
{
    var found = store.SetPermaGreen(id, request.Enabled, out var changed);
    if (!found) return Results.NotFound();
    if (changed)
    {
        events.Append(id, "perma-green", request.Enabled ? "Perma-Green aktiviert" : "Perma-Green deaktiviert",
            request.Enabled ? "Der Account wird dauerhaft an den Anfang seiner Liste sortiert." : "Der Account wird wieder normal sortiert.",
            request.Enabled ? "success" : "info");
    }
    return Results.NoContent();
});

app.MapPatch("/api/accounts/{id:guid}/monitoring", (Guid id, SetMonitoringRequest request, AccountStore store, AccountEventStore events) =>
{
    var found = store.SetMonitoringPaused(id, request.Paused, out var changed);
    if (!found) return Results.NotFound();
    if (changed)
    {
        events.Append(id, "monitoring", request.Paused ? "Spielmodus aktiviert" : "Checks fortgesetzt",
            request.Paused ? "Automatische CM-/GC-Checks bleiben pausiert, damit eine laufende Steam-Sitzung nicht verdrängt wird." : "Der Scheduler darf diesen Account wieder automatisch prüfen.",
            request.Paused ? "warning" : "success");
    }
    return Results.NoContent();
});

app.MapGet("/api/timeline", (int? limit, Guid? accountId, string? type, AccountEventStore events, AccountStore accounts) =>
{
    var items = events.List(limit ?? 120, accountId, type)
        .Select(item =>
        {
            var account = accounts.Get(item.AccountId);
            return new AccountTimelineEventDto(
                item.Id,
                item.AccountId,
                account?.SteamId ?? "unbekannt",
                account?.PersonaName,
                account?.AvatarUrl,
                account?.IsPermaGreen ?? false,
                item.Type,
                item.Title,
                item.Detail,
                item.Severity,
                item.OccurredUtc);
        })
        .ToArray();
    return Results.Ok(items);
});

app.MapGet("/api/mail", (string? query, bool? unread, int? limit, SteamMailStore mail) =>
{
    var snapshot = mail.Snapshot(query, unread, limit ?? 50);
    return Results.Ok(new
    {
        configured = snapshot.Configured,
        lastSyncUtc = snapshot.LastSyncUtc,
        lastSyncError = snapshot.LastSyncError,
        messages = snapshot.Messages,
    });
});

app.MapGet("/api/mail/{id:guid}", (Guid id, SteamMailStore mail) =>
{
    var message = mail.Get(id);
    return message is null ? Results.NotFound() : Results.Ok(message);
});

app.MapPost("/api/mail/sync", async (SteamMailService service, CancellationToken cancellationToken) =>
{
    if (!service.IsConfigured) return Results.Conflict(new { message = "Steam Mail ist noch nicht konfiguriert." });
    await service.SyncAsync(cancellationToken);
    return Results.Ok(new { synced = true });
});

app.MapPatch("/api/mail/{id:guid}/read", (Guid id, MarkMailReadRequest request, SteamMailStore mail) =>
{
    return mail.MarkRead(id, request.IsRead) ? Results.NoContent() : Results.NotFound();
});

app.MapGet("/api/settings/loadout", (SettingsStore settings) => Results.Ok(settings.GetLoadout()));
app.MapPut("/api/settings/loadout", (LoadoutPresetDto request, SettingsStore settings) =>
{
    if (!RequestValidation.IsValidLoadout(request)) return Results.BadRequest(new { message = "The loadout preset is invalid or too large." });
    settings.SetLoadout(request);
    return Results.NoContent();
});
app.MapGet("/api/settings/personalize", (SettingsStore settings) => Results.Ok(settings.GetPersonalize()));
app.MapPut("/api/settings/personalize", (PersonalizePresetDto request, SettingsStore settings) =>
{
    if (!RequestValidation.IsValidPersonalize(request)) return Results.BadRequest(new { message = "The personalization preset is invalid or too large." });
    settings.SetPersonalize(request);
    return Results.NoContent();
});

app.MapPost("/api/auth/sessions", async (
    StartAuthRequest request,
    AuthSessionStore sessions,
    AccountStore accounts,
    ISteamAccountGateway gateway,
    CancellationToken cancellationToken) =>
{
    if (!RequestValidation.IsValidStartAuth(request))
    {
        return Results.BadRequest(new { message = "Account name and password are required and within the allowed length." });
    }

    var session = await sessions.StartAsync(request.AccountName.Trim(), request.Password, gateway, accounts, cancellationToken);
    return Results.Ok(new StartAuthResponse(session.Id, session.State, session.SteamId, session.PromptMessage));
}).RequireRateLimiting("steam-auth");

app.MapGet("/api/auth/sessions/{id:guid}", (Guid id, AuthSessionStore sessions) =>
{
    var session = sessions.Get(id);
    return session is null
        ? Results.NotFound()
        : Results.Ok(new StartAuthResponse(session.Id, session.State, session.SteamId, session.PromptMessage));
});

app.MapPost("/api/auth/sessions/{id:guid}/code", async (
    Guid id,
    SubmitGuardCodeRequest request,
    AuthSessionStore sessions,
    AccountStore accounts,
    ISteamAccountGateway gateway,
    CancellationToken cancellationToken) =>
{
    if (!RequestValidation.IsValidGuardCode(request))
    {
        return Results.BadRequest(new { message = "A Steam Guard code is required." });
    }

    var result = await sessions.SubmitCodeAsync(id, request.Code.Trim(), gateway, accounts, cancellationToken);
    return result is null ? Results.NotFound() : Results.Ok(result);
}).RequireRateLimiting("steam-auth");

app.MapGet("/api/operations/{id:guid}", (Guid id, OperationStore operations) =>
{
    var operation = operations.Get(id);
    return operation is null ? Results.NotFound() : Results.Ok(OperationMappings.ToDto(operation));
});

app.MapPost("/api/accounts/{id:guid}/commands", async (
    Guid id,
    CommandRequest request,
    AccountStore accounts,
    OperationStore operations,
    OperationQueue queue,
    SteamPublicProfileService publicProfile,
    CancellationToken cancellationToken) =>
{
    if (request.Command == AccountCommand.SignInToSteam)
    {
        return Results.BadRequest(new { message = "Local Steam sign-in handoff is disabled. Use the web Steam login." });
    }

    if (!RequestValidation.IsValidCommand(request))
    {
        return Results.BadRequest(new { message = "The command payload is invalid or too large." });
    }

    var account = accounts.Get(id);
    if (account is null)
    {
        return Results.NotFound();
    }

    if (account.MonitoringPaused || account.InMatch == true)
    {
        return Results.Conflict(new { message = "Spielmodus aktiv: CM-/GC-Aktionen sind pausiert, damit die laufende Steam-Sitzung nicht verdrängt wird." });
    }

    SteamProfileSnapshot? publicPresence;
    try
    {
        publicPresence = await publicProfile.GetAsync(account.SteamId, cancellationToken);
    }
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
    {
        return Results.Conflict(new { message = $"Steam-Präsenz konnte nicht sicher geprüft werden ({exception.GetType().Name}). Bitte Spielmodus aktivieren und später erneut versuchen." });
    }
    if (publicPresence?.IsPlayingCs2 == true)
    {
        return Results.Conflict(new { message = "CS2 wurde als laufendes Spiel erkannt. Die CM-/GC-Aktion wurde zum Schutz der Sitzung nicht gestartet." });
    }

    var operation = operations.Create(id, request.Command, request.Payload);
    await queue.EnqueueAsync(operation.Id);
    return Results.Accepted($"/api/operations/{operation.Id}", OperationMappings.ToDto(operation));
});

app.MapGet("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");

app.Run();

public sealed record WebLoginRequest(string Username, string Password);
public sealed record MarkMailReadRequest(bool IsRead);

internal static class RequestValidation
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    public static bool IsValidWebLogin(WebLoginRequest request) =>
        IsText(request.Username, 1, 128) && IsText(request.Password, 1, 512);

    public static bool IsValidStartAuth(StartAuthRequest request) =>
        IsText(request.AccountName, 1, 128) && IsText(request.Password, 1, 512);

    public static bool IsValidGuardCode(SubmitGuardCodeRequest request) =>
        IsText(request.Code, 1, 32);

    public static bool IsValidNote(UpdateNoteRequest request) =>
        request.Note is null || request.Note.Length <= 1000;

    public static bool IsValidLoadout(LoadoutPresetDto request) =>
        IsText(request.Name, 1, 64) && IsValidSlots(request.T) && IsValidSlots(request.Ct);

    public static bool IsValidPersonalize(PersonalizePresetDto request) =>
        IsOptionalText(request.PersonaName, 128) &&
        IsOptionalText(request.RealName, 128) &&
        IsOptionalText(request.Summary, 512) &&
        IsValidAvatar(request.AvatarBase64);

    public static bool IsValidCommand(CommandRequest request)
    {
        return request.Command switch
        {
            AccountCommand.RefreshStatus or AccountCommand.ClearWorkshop or AccountCommand.SyncLoadout or AccountCommand.RefreshMatches => IsEmptyPayload(request.Payload),
            AccountCommand.ApplyLoadout => TryDeserialize(request.Payload, out LoadoutPresetDto? loadout) && loadout is not null && IsValidLoadout(loadout),
            AccountCommand.Personalize => TryDeserialize(request.Payload, out PersonalizePresetDto? personalize) && personalize is not null && IsValidPersonalize(personalize),
            _ => false,
        };
    }

    private static bool IsText(string? value, int minimum, int maximum) =>
        value is not null && value.Trim().Length >= minimum && value.Length <= maximum;

    private static bool IsOptionalText(string? value, int maximum) => value is null || value.Length <= maximum;

    private static bool IsValidSlots(IReadOnlyDictionary<uint, uint>? slots) =>
        slots is not null && slots.Count <= 64 && slots.All(entry => entry.Key <= 1000 && entry.Value <= 1000);

    private static bool IsEmptyPayload(object? payload) =>
        payload is null || payload is JsonElement element && element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;

    private static bool IsValidAvatar(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (value.Length > 2_800_000) return false;
        try
        {
            return Convert.FromBase64String(value).Length <= 2 * 1024 * 1024;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryDeserialize<T>(object? payload, out T? value)
    {
        try
        {
            value = payload switch
            {
                JsonElement element => element.Deserialize<T>(WebJson),
                null => default,
                _ => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(payload, WebJson), WebJson),
            };
            return value is not null;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
    }
}

internal sealed class WebSessionService
{
    public const string CookieName = "cs2hub_session";
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(4);
    private readonly IDataProtector _protector;

    public WebSessionService(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("web-session-v1");
    }

    public void SignIn(HttpResponse response, string username)
    {
        var expires = DateTimeOffset.UtcNow.Add(Lifetime);
        var value = _protector.Protect($"{username}\n{expires.ToUnixTimeSeconds()}\n{GetPasswordVersion()}");
        response.Cookies.Append(CookieName, value, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Expires = expires,
            MaxAge = Lifetime,
            IsEssential = true,
        });
    }

    public void SignOut(HttpResponse response) => response.Cookies.Delete(CookieName, new CookieOptions
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        IsEssential = true,
    });

    public bool IsAuthenticated(HttpContext context) => GetUsername(context) is not null;

    public string? GetUsername(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var parts = _protector.Unprotect(value).Split('\n', 3);
            var expectedUsername = Environment.GetEnvironmentVariable("CS2_HUB_WEB_USERNAME");
            if (parts.Length != 3 || !long.TryParse(parts[1], out var expiry) ||
                expiry <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() || string.IsNullOrWhiteSpace(parts[0]) ||
                !ConstantTimeEquals(parts[0], expectedUsername) ||
                !ConstantTimeEquals(parts[2], GetPasswordVersion()))
            {
                return null;
            }

            return parts[0];
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    public static bool ConstantTimeEquals(string? left, string? right)
    {
        if (left is null || right is null) return false;
        var leftBytes = System.Text.Encoding.UTF8.GetBytes(left);
        var rightBytes = System.Text.Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string GetPasswordVersion()
    {
        var password = Environment.GetEnvironmentVariable("CS2_HUB_WEB_PASSWORD");
        return password is null
            ? string.Empty
            : Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(password)));
    }
}

internal sealed class AccountStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, AccountRecord> _accounts = new();
    private readonly string _path;

    public AccountStore()
    {
        var dataDirectory = Environment.GetEnvironmentVariable("CS2_HUB_DATA_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "accounts.json");

        try
        {
            var json = File.Exists(_path) ? File.ReadAllText(_path) : "[]";
            foreach (var account in JsonSerializer.Deserialize<List<AccountRecord>>(json) ?? [])
            {
                if (account.CooldownEndsUtc is null && account.StatusUpdatedUtc is not null && account.CooldownSeconds is > 0 and <= int.MaxValue)
                {
                    account.CooldownEndsUtc = account.StatusUpdatedUtc.Value.AddSeconds(account.CooldownSeconds.Value);
                }
                _accounts[account.Id] = account;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not load account state ({exception.GetType().Name}).");
        }
    }

    public IReadOnlyList<AccountRecord> List(AccountSection? section, AccountPrimeBucket? prime)
    {
        lock (_gate)
        {
            return _accounts.Values
                .Where(x => section is null || x.Section == section)
                .Where(x => prime is null || x.Prime == prime)
                .OrderByDescending(x => x.IsPermaGreen)
                .ThenBy(x => x.PersonaName ?? x.SteamId)
                .ToArray();
        }
    }

    public AccountRecord? Get(Guid id)
    {
        lock (_gate)
        {
            return _accounts.GetValueOrDefault(id);
        }
    }

    public AccountRecord Upsert(AccountRecord account)
    {
        lock (_gate)
        {
            _accounts[account.Id] = account;
            SaveUnsafe();
            return account;
        }
    }

    public bool UpdateNote(Guid id, string? note)
    {
        lock (_gate)
        {
            if (!_accounts.TryGetValue(id, out var account)) return false;
            account.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            SaveUnsafe();
            return true;
        }
    }

    public bool UpdatePrime(Guid id, AccountPrimeBucket prime)
    {
        lock (_gate)
        {
            if (!_accounts.TryGetValue(id, out var account)) return false;
            account.Prime = prime;
            SaveUnsafe();
            return true;
        }
    }

    public bool SetPermaGreen(Guid id, bool enabled, out bool changed)
    {
        lock (_gate)
        {
            if (!_accounts.TryGetValue(id, out var account))
            {
                changed = false;
                return false;
            }

            changed = account.IsPermaGreen != enabled;
            account.IsPermaGreen = enabled;
            SaveUnsafe();
            return true;
        }
    }

    public bool SetMonitoringPaused(Guid id, bool paused, out bool changed)
    {
        lock (_gate)
        {
            if (!_accounts.TryGetValue(id, out var account))
            {
                changed = false;
                return false;
            }

            changed = account.MonitoringPaused != paused;
            account.MonitoringPaused = paused;
            SaveUnsafe();
            return true;
        }
    }

    public void SetNextChecks(IReadOnlyDictionary<Guid, DateTimeOffset> updates)
    {
        if (updates.Count == 0) return;
        lock (_gate)
        {
            foreach (var (id, nextCheck) in updates)
            {
                if (_accounts.TryGetValue(id, out var account)) account.NextCheckUtc = nextCheck;
            }
            SaveUnsafe();
        }
    }

    private void SaveUnsafe()
    {
        var temporaryPath = _path + ".tmp";
        var json = JsonSerializer.Serialize(_accounts.Values.OrderBy(x => x.PersonaName ?? x.SteamId), new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _path, overwrite: true);
    }
}

internal sealed class OperationStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, OperationRecord> _operations = new();

    public OperationRecord Create(Guid accountId, AccountCommand command, object? payload)
    {
        var operation = new OperationRecord(Guid.NewGuid(), accountId, command, payload);
        lock (_gate) _operations[operation.Id] = operation;
        return operation;
    }

    public OperationRecord? Get(Guid id)
    {
        lock (_gate) return _operations.GetValueOrDefault(id);
    }

    public bool HasActiveForAccount(Guid accountId)
    {
        lock (_gate)
        {
            return _operations.Values.Any(operation =>
                operation.AccountId == accountId &&
                operation.State is OperationState.Queued or OperationState.Running);
        }
    }
}

internal sealed class OperationQueue
{
    private readonly System.Threading.Channels.Channel<Guid> _channel =
        System.Threading.Channels.Channel.CreateUnbounded<Guid>();

    public ValueTask EnqueueAsync(Guid operationId) => _channel.Writer.WriteAsync(operationId);
    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) => _channel.Reader.ReadAllAsync(cancellationToken);
}

internal sealed class OperationWorker : BackgroundService
{
    private readonly OperationQueue _queue;
    private readonly OperationStore _operations;
    private readonly AccountStore _accounts;
    private readonly ISteamAccountGateway _gateway;
    private readonly DiscordWebhookNotifier _notifier;
    private readonly AccountEventStore _events;
    private readonly ILogger<OperationWorker> _logger;

    public OperationWorker(OperationQueue queue, OperationStore operations, AccountStore accounts, ISteamAccountGateway gateway, DiscordWebhookNotifier notifier, AccountEventStore events, ILogger<OperationWorker> logger)
    {
        _queue = queue;
        _operations = operations;
        _accounts = accounts;
        _gateway = gateway;
        _notifier = notifier;
        _events = events;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var operationId in _queue.ReadAllAsync(stoppingToken))
        {
            var operation = _operations.Get(operationId);
            if (operation is null) continue;

            operation.State = OperationState.Running;
            AccountRecord? account = null;
            string? previousHealth = null;
            bool? previousInMatch = null;
            int? previousLoadoutMismatches = null;
            var previousCooldownActive = false;
            var previousCooldownNotificationSent = false;
            try
            {
                account = _accounts.Get(operation.AccountId)
                    ?? throw new InvalidOperationException("Account no longer exists.");
                previousHealth = account.HealthStatus;
                previousInMatch = account.InMatch;
                previousLoadoutMismatches = account.LoadoutMismatchCount;
                previousCooldownActive = account.CooldownSeconds is > 0 || account.CooldownEndsUtc > DateTimeOffset.UtcNow;
                previousCooldownNotificationSent = account.CooldownNotificationSent;

                var result = await _gateway.ExecuteAsync(account, operation.Command, operation.Payload, stoppingToken);
                if (result.Status is not null)
                {
                    account.ApplyStatus(result.Status, result.Prime);
                    account.ApplyProfile(await _gateway.QueryProfileAsync(account.SteamId, stoppingToken));
                    account.StatusUpdatedUtc = DateTimeOffset.UtcNow;
                    account.LastLoginUtc = DateTimeOffset.UtcNow;
                    _accounts.Upsert(account);

                    _events.Append(account.Id, "status", "Status geprüft",
                        $"{account.HealthStatus}; {account.Section}; {account.Prime}.",
                        account.Section == AccountSection.PlayReady ? "success" : "warning");
                    var currentCooldownActive = account.CooldownSeconds is > 0 || account.CooldownEndsUtc > DateTimeOffset.UtcNow;
                    if (previousCooldownActive != currentCooldownActive)
                    {
                        _events.Append(account.Id, "cooldown",
                            currentCooldownActive ? "Cooldown gestartet" : "Cooldown beendet",
                            currentCooldownActive ? $"Noch {account.CooldownSeconds ?? 0} Sekunden." : "Der Account wurde wieder ohne aktiven Cooldown bestätigt.",
                            currentCooldownActive ? "warning" : "success");
                    }

                    if (previousHealth is not null && !string.Equals(previousHealth, account.HealthStatus, StringComparison.Ordinal))
                    {
                        await NotifyEventAsync(account, "GC-Gesundheit geändert", $"Neuer Zustand: {account.HealthStatus}.", 0xF1C40F, stoppingToken);
                    }
                    if (previousInMatch is not null && previousInMatch != account.InMatch)
                    {
                        await NotifyEventAsync(account, "Steam-Präsenz geändert", account.InMatch == true ? "Der Account ist in einem CS2-Match." : "Der Account ist nicht mehr in einem CS2-Match.", 0x3498DB, stoppingToken);
                    }
                    if (previousCooldownActive && result.Status.HasCooldownData && !result.Status.HasCooldown && !previousCooldownNotificationSent)
                    {
                        if (await _notifier.SendCooldownDoneAsync(account, stoppingToken))
                        {
                            account.CooldownNotificationSent = true;
                            _accounts.Upsert(account);
                        }
                    }
                }
                if (result.Loadout is not null)
                {
                    account.CurrentLoadoutT = new Dictionary<uint, uint>(result.Loadout.T);
                    account.CurrentLoadoutCt = new Dictionary<uint, uint>(result.Loadout.Ct);
                    account.LoadoutMismatchCount = result.Loadout.MismatchCount;
                    account.LoadoutCheckedUtc = result.Loadout.CheckedUtc;
                    _accounts.Upsert(account);
                    if (previousLoadoutMismatches is not null && previousLoadoutMismatches != account.LoadoutMismatchCount)
                    {
                        await NotifyEventAsync(account, "Loadout-Abgleich geändert", $"Aktuell sind {account.LoadoutMismatchCount ?? 0} Slot-Abweichungen vorhanden.", 0x9B59B6, stoppingToken);
                    }
                }
                if (result.RecentMatches is not null)
                {
                    account.RecentMatches = result.RecentMatches
                        .Select(match => new RecentMatchDto(match.MatchId, match.PlayedUtc, match.Map, match.ResultCode, match.TeamScore, match.OpponentScore, match.Outcome, match.Mode, match.Kills, match.Assists, match.Deaths))
                        .ToList();
                    account.MatchesCheckedUtc = DateTimeOffset.UtcNow;
                    account.MatchesStatus = "ok";
                    _accounts.Upsert(account);
                    _events.Append(account.Id, "matches", "Letzte Spiele geladen", $"{account.RecentMatches.Count} Spiele vom Game Coordinator.");
                }
                operation.State = result.Success ? OperationState.Succeeded : OperationState.Failed;
                operation.Message = result.Message;
                if (result.Success && result.Status is null && result.Loadout is null && result.RecentMatches is null)
                {
                    _events.Append(account!.Id, "action", CommandTitle(operation.Command), result.Message, "success");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (account is null) account = _accounts.Get(operation.AccountId);
                if (account is not null)
                {
                    account.LastCheckUtc = DateTimeOffset.UtcNow;
                    account.HealthStatus = $"error:{ex.GetType().Name}";
                    account.LastCheckError = ex.GetType().Name;
                    if (operation.Command == AccountCommand.RefreshMatches)
                    {
                        account.MatchesCheckedUtc = DateTimeOffset.UtcNow;
                        account.MatchesStatus = "error";
                    }
                    _accounts.Upsert(account);
                    _events.Append(account.Id, "error", "Operation fehlgeschlagen", $"{operation.Command}: {ex.GetType().Name}.", "error");
                    if (previousHealth is not null && !string.Equals(previousHealth, account.HealthStatus, StringComparison.Ordinal))
                    {
                        await NotifyEventAsync(account, "GC-Check fehlgeschlagen", $"Technischer Fehler: {ex.GetType().Name}.", 0xE74C3C, CancellationToken.None);
                    }
                }
                operation.State = OperationState.Failed;
                operation.Message = "The operation failed. Check the server logs for details.";
                _logger.LogError("Operation {OperationId} failed with {ExceptionType}.", operation.Id, ex.GetType().Name);
            }
            finally
            {
                operation.CompletedUtc = DateTimeOffset.UtcNow;
            }
        }
    }

    private async Task NotifyEventAsync(AccountRecord account, string title, string description, int color, CancellationToken cancellationToken)
    {
        if (!_notifier.IsConfigured) return;
        await _notifier.SendAccountEventAsync(account, title, description, color, cancellationToken);
    }

    private static string CommandTitle(AccountCommand command) => command switch
    {
        AccountCommand.ClearWorkshop => "Workshop geleert",
        AccountCommand.ApplyLoadout => "Loadout angewendet",
        AccountCommand.Personalize => "Account personalisiert",
        AccountCommand.SyncLoadout => "Loadout geprüft",
        _ => "Account-Aktion ausgeführt",
    };
}

internal interface ISteamAccountGateway
{
    Task<NfaLoader.Services.CredentialsAuthResult> AuthenticateAsync(
        string accountName,
        string password,
        Func<NfaLoader.Services.SteamGuardPrompt, CancellationToken, Task<string?>> guardCodeProvider,
        IProgress<string>? progress,
        CancellationToken cancellationToken);

    Task<NfaLoader.Models.CsPremierScoreResult> QueryStatusAsync(
        string refreshToken,
        string steamId,
        CancellationToken cancellationToken);

    Task<AccountPrimeBucket> QueryPrimeAsync(
        string refreshToken,
        string steamId,
        NfaLoader.Models.CsPremierScoreResult status,
        CancellationToken cancellationToken);

    Task<SteamProfileSnapshot?> QueryProfileAsync(string steamId, CancellationToken cancellationToken);

    Task<GatewayResult> ExecuteAsync(AccountRecord account, AccountCommand command, object? payload, CancellationToken cancellationToken);
}

internal sealed record GatewayResult(bool Success, string Message, NfaLoader.Models.CsPremierScoreResult? Status = null, AccountPrimeBucket? Prime = null, NfaLoader.Models.CsLoadoutSnapshot? Loadout = null, IReadOnlyList<NfaLoader.Models.CsRecentMatch>? RecentMatches = null)
{
    public static GatewayResult WithStatus(bool success, string message, NfaLoader.Models.CsPremierScoreResult status, AccountPrimeBucket prime) =>
        new(success, message, status, prime);

    public static GatewayResult WithLoadout(bool success, string message, NfaLoader.Models.CsLoadoutSnapshot loadout) =>
        new(success, message, null, null, loadout);

    public static GatewayResult WithMatches(bool success, string message, IReadOnlyList<NfaLoader.Models.CsRecentMatch> matches) =>
        new(success, message, null, null, null, matches);
}

internal sealed class AuthSessionStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, AuthSessionRecord> _sessions = new();

    private readonly TokenProtector _tokens;
    private readonly AccountEventStore _events;

    public AuthSessionStore(TokenProtector tokens, AccountEventStore events)
    {
        _tokens = tokens;
        _events = events;
    }

    public async Task<AuthSessionRecord> StartAsync(string accountName, string password, ISteamAccountGateway gateway, AccountStore accounts, CancellationToken cancellationToken)
    {
        var session = new AuthSessionRecord(Guid.NewGuid(), accountName, password);
        lock (_gate) _sessions[session.Id] = session;
        _ = RunAsync(session, gateway, accounts);
        await Task.Yield();
        return session;
    }

    public AuthSessionRecord? Get(Guid id)
    {
        lock (_gate) return _sessions.GetValueOrDefault(id);
    }

    public async Task<StartAuthResponse?> SubmitCodeAsync(Guid id, string code, ISteamAccountGateway gateway, AccountStore accounts, CancellationToken cancellationToken)
    {
        AuthSessionRecord? session;
        lock (_gate) session = _sessions.GetValueOrDefault(id);
        if (session is null) return null;

        session.Code.TrySetResult(code);
        await Task.Yield();
        return new StartAuthResponse(session.Id, session.State, session.SteamId, session.PromptMessage);
    }

    private async Task RunAsync(AuthSessionRecord session, ISteamAccountGateway gateway, AccountStore accounts)
    {
        try
        {
            var result = await gateway.AuthenticateAsync(
                session.AccountName,
                session.TakePassword(),
                (prompt, cancellationToken) => WaitForGuardAsync(session, prompt, cancellationToken),
                progress: null,
                session.CancellationToken.Token);

            session.SteamId = result.SteamId;
            var account = new AccountRecord
            {
                AccountName = result.AccountName,
                SteamId = result.SteamId,
                PersonaName = result.AccountName,
                ProtectedRefreshToken = _tokens.Protect(result.RefreshToken),
                LastLoginUtc = DateTimeOffset.UtcNow,
            };

            var status = await gateway.QueryStatusAsync(result.RefreshToken, result.SteamId, session.CancellationToken.Token);
            account.ApplyStatus(status, await gateway.QueryPrimeAsync(result.RefreshToken, result.SteamId, status, session.CancellationToken.Token));
            account.ApplyProfile(await gateway.QueryProfileAsync(result.SteamId, session.CancellationToken.Token));
            account.StatusUpdatedUtc = DateTimeOffset.UtcNow;
            accounts.Upsert(account);
            _events.Append(account.Id, "account", "Account verbunden", $"{account.PersonaName ?? account.SteamId} wurde serverseitig hinzugefügt.", "success");

            session.State = AuthState.Completed;
            session.PromptMessage = "Account authenticated and status loaded.";
        }
        catch (OperationCanceledException)
        {
            session.State = AuthState.Failed;
            session.PromptMessage = "Authentication was cancelled.";
        }
        catch (Exception)
        {
            session.State = AuthState.Failed;
            session.PromptMessage = "Steam authentication failed. Check the account details and try again.";
        }
    }

    private static async Task<string?> WaitForGuardAsync(AuthSessionRecord session, NfaLoader.Services.SteamGuardPrompt prompt, CancellationToken cancellationToken)
    {
        session.State = prompt.Type is NfaLoader.Services.SteamGuardType.DeviceConfirmation or NfaLoader.Services.SteamGuardType.EmailConfirmation
            ? AuthState.AwaitingConfirmation
            : AuthState.AwaitingSteamGuard;
        session.PromptMessage = prompt.AssociatedMessage ?? "Steam Guard confirmation required.";
        return await session.Code.Task.WaitAsync(cancellationToken);
    }
}

internal sealed class AuthSessionRecord
{
    public AuthSessionRecord(Guid id, string accountName, string password)
    {
        Id = id;
        AccountName = accountName;
        _password = password;
    }

    public Guid Id { get; }
    public string AccountName { get; }
    private string? _password;
    public string TakePassword() => Interlocked.Exchange(ref _password, null)
        ?? throw new InvalidOperationException("Authentication password is no longer available.");
    public TaskCompletionSource<string> Code { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationTokenSource CancellationToken { get; } = new(TimeSpan.FromMinutes(5));
    public AuthState State { get; set; }
    public string? SteamId { get; set; }
    public string? PromptMessage { get; set; }

}

internal sealed class AccountRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string? AccountName { get; set; }
    public string SteamId { get; init; } = "";
    public string? ProtectedRefreshToken { get; set; }
    public string? PersonaName { get; set; }
    public string? AvatarUrl { get; set; }
    public AccountPrimeBucket Prime { get; set; } = AccountPrimeBucket.Unknown;
    public AccountSection Section { get; set; } = AccountSection.NotPlayReady;
    public int? PremierRating { get; set; }
    public int? PremierWins { get; set; }
    public int? Cs2Level { get; set; }
    public DateTimeOffset? LastLoginUtc { get; set; }
    public uint? CooldownSeconds { get; set; }
    public uint? LastCooldownSeconds { get; set; }
    public int CooldownTier { get; set; }
    [JsonIgnore]
    public uint? NextCooldownSeconds => CooldownTier > 0 && CooldownEndsUtc is null && LastCooldownSeconds is not null
        ? CooldownProgression.Next(CooldownTier)
        : null;
    public DateTimeOffset? CooldownEndsUtc { get; set; }
    public bool CooldownNotificationSent { get; set; }
    public uint? CooldownReason { get; set; }
    public bool? VacBanned { get; set; }
    public string? Note { get; set; }
    public bool IsPermaGreen { get; set; }
    public bool MonitoringPaused { get; set; }
    public DateTimeOffset? StatusUpdatedUtc { get; set; }
    public DateTimeOffset? NextCheckUtc { get; set; }
    public DateTimeOffset? LastCheckUtc { get; set; }
    public DateTimeOffset? GcConnectedUtc { get; set; }
    public DateTimeOffset? CooldownVerifiedUtc { get; set; }
    public string? HealthStatus { get; set; }
    public string? LastCheckError { get; set; }
    public bool? InMatch { get; set; }
    public bool? PublicPlayingCs2 { get; set; }
    public int? LoadoutMismatchCount { get; set; }
    public DateTimeOffset? LoadoutCheckedUtc { get; set; }
    public Dictionary<uint, uint> CurrentLoadoutT { get; set; } = new();
    public Dictionary<uint, uint> CurrentLoadoutCt { get; set; } = new();
    public DateTimeOffset? MatchesCheckedUtc { get; set; }
    public string? MatchesStatus { get; set; }
    public List<RecentMatchDto> RecentMatches { get; set; } = new();
    public List<RatingPointDto> RatingHistory { get; set; } = new();

    public void ApplyStatus(NfaLoader.Models.CsPremierScoreResult status, AccountPrimeBucket? prime = null)
    {
        var now = DateTimeOffset.UtcNow;
        PremierRating = status.HasPremierScore ? checked((int)status.PremierRanking!.RankId) : null;
        PremierWins = status.HasPremierScore ? checked((int)status.PremierRanking!.Wins) : null;
        Cs2Level = status.PlayerLevel;
        CooldownSeconds = status.PenaltySeconds;
        CooldownReason = status.PenaltyReason;
        VacBanned = status.GcVacBannedOrUnknown;
        InMatch = status.InMatch;
        LastCheckUtc = now;
        GcConnectedUtc = now;
        CooldownVerifiedUtc = status.HasCooldownData ? now : null;
        HealthStatus = status.HasCooldownData ? "healthy" : "gc-unknown";
        LastCheckError = null;

        if (PremierRating is { } rating)
        {
            if (RatingHistory.Count == 0 || RatingHistory[^1].Rating != rating || now - RatingHistory[^1].CapturedUtc >= TimeSpan.FromMinutes(10))
            {
                RatingHistory.Add(new RatingPointDto(now, rating));
                if (RatingHistory.Count > 180) RatingHistory.RemoveRange(0, RatingHistory.Count - 180);
            }
        }

        if (status.HasCooldown)
        {
            LastCooldownSeconds = status.PenaltySeconds;
            CooldownTier = Math.Max(CooldownTier, CooldownProgression.Classify(status.PenaltySeconds!.Value));
            var candidateEnd = now.AddSeconds(status.PenaltySeconds!.Value);
            if (CooldownEndsUtc is null || Math.Abs((candidateEnd - CooldownEndsUtc.Value).TotalMinutes) > 2)
            {
                CooldownNotificationSent = false;
            }

            CooldownEndsUtc = candidateEnd;
        }
        else if (status.HasCooldownData)
        {
            CooldownEndsUtc = null;
            CooldownNotificationSent = false;
        }

        if (prime is not null && prime != AccountPrimeBucket.Unknown) Prime = prime.Value;
        else if (Prime == AccountPrimeBucket.Unknown && status.HasPremierScore) Prime = AccountPrimeBucket.Prime;

        Section = status.HasCooldownData && !status.HasCooldown && status.GcVacBannedOrUnknown is false
            ? AccountSection.PlayReady
            : AccountSection.NotPlayReady;
    }

    public void ApplyProfile(SteamProfileSnapshot? profile)
    {
        if (profile is null) return;
        if (!string.IsNullOrWhiteSpace(profile.PersonaName)) PersonaName = profile.PersonaName;
        if (!string.IsNullOrWhiteSpace(profile.AvatarUrl)) AvatarUrl = profile.AvatarUrl;
        if (profile.IsPlayingCs2 is not null) PublicPlayingCs2 = profile.IsPlayingCs2;
    }
}

internal sealed class OperationRecord
{
    public OperationRecord(Guid id, Guid accountId, AccountCommand command, object? payload)
    {
        Id = id;
        AccountId = accountId;
        Command = command;
        Payload = payload;
        CreatedUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; }
    public Guid AccountId { get; }
    public AccountCommand Command { get; }
    public object? Payload { get; }
    public OperationState State { get; set; } = OperationState.Queued;
    public string? Message { get; set; }
    public DateTimeOffset CreatedUtc { get; }
    public DateTimeOffset? CompletedUtc { get; set; }
}

internal static class OperationMappings
{
    public static OperationDto ToDto(OperationRecord operation) => new(
        operation.Id,
        operation.AccountId,
        operation.Command,
        operation.State,
        operation.Message,
        operation.CreatedUtc,
        operation.CompletedUtc);
}

internal static class AccountMappings
{
    public static AccountSummaryDto ToSummary(AccountRecord account) => new(
        account.Id, account.SteamId, account.PersonaName, account.AvatarUrl, account.Prime, account.Section,
        account.PremierRating, account.Cs2Level, account.LastLoginUtc, account.CooldownSeconds,
        account.CooldownReason, account.VacBanned, account.Note, account.StatusUpdatedUtc, account.CooldownEndsUtc)
    {
        NextCooldownSeconds = account.NextCooldownSeconds,
        NextCheckUtc = account.NextCheckUtc,
        LastCheckUtc = account.LastCheckUtc,
        HealthStatus = account.HealthStatus,
        InMatch = account.InMatch,
        PublicPlayingCs2 = account.PublicPlayingCs2,
        LoadoutMismatchCount = account.LoadoutMismatchCount,
        LoadoutCheckedUtc = account.LoadoutCheckedUtc,
        IsPermaGreen = account.IsPermaGreen,
        MonitoringPaused = account.MonitoringPaused,
    };

    public static AccountDetailDto ToDetail(AccountRecord account) => new(
        account.Id, account.SteamId, account.PersonaName, account.AvatarUrl, account.Prime, account.Section,
        account.PremierRating, account.PremierWins, account.Cs2Level, account.LastLoginUtc,
        account.CooldownSeconds, account.CooldownReason, account.VacBanned, account.Note, account.StatusUpdatedUtc, account.CooldownEndsUtc)
    {
        NextCooldownSeconds = account.NextCooldownSeconds,
        NextCheckUtc = account.NextCheckUtc,
        LastCheckUtc = account.LastCheckUtc,
        HealthStatus = account.HealthStatus,
        InMatch = account.InMatch,
        PublicPlayingCs2 = account.PublicPlayingCs2,
        LoadoutMismatchCount = account.LoadoutMismatchCount,
        LoadoutCheckedUtc = account.LoadoutCheckedUtc,
        CurrentLoadoutT = new Dictionary<uint, uint>(account.CurrentLoadoutT),
        CurrentLoadoutCt = new Dictionary<uint, uint>(account.CurrentLoadoutCt),
        MatchesCheckedUtc = account.MatchesCheckedUtc,
        MatchesStatus = account.MatchesStatus,
        RecentMatches = account.RecentMatches,
        RatingHistory = account.RatingHistory,
        IsPermaGreen = account.IsPermaGreen,
        MonitoringPaused = account.MonitoringPaused,
    };
}
