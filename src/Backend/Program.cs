using System.Text.Json;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using CS2AccountHub.Contracts;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

var dataDirectory = Environment.GetEnvironmentVariable("CS2_HUB_DATA_DIR")
    ?? Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDirectory);

builder.Services.AddSingleton<AccountStore>();
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<OperationStore>();
builder.Services.AddSingleton<AuthSessionStore>();
builder.Services.AddSingleton<OperationQueue>();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")))
    .SetApplicationName("CS2AccountHub");
builder.Services.AddSingleton<TokenProtector>();
builder.Services.AddSingleton<WebSessionService>();
builder.Services.AddSingleton<ISteamAccountGateway, SteamGateway>();
builder.Services.AddHostedService<OperationWorker>();
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
    OperationQueue queue) =>
{
    if (request.Command == AccountCommand.SignInToSteam)
    {
        return Results.BadRequest(new { message = "Local Steam sign-in handoff is disabled. Use the web Steam login." });
    }

    if (!RequestValidation.IsValidCommand(request))
    {
        return Results.BadRequest(new { message = "The command payload is invalid or too large." });
    }

    if (accounts.Get(id) is null)
    {
        return Results.NotFound();
    }

    var operation = operations.Create(id, request.Command, request.Payload);
    await queue.EnqueueAsync(operation.Id);
    return Results.Accepted($"/api/operations/{operation.Id}", OperationMappings.ToDto(operation));
});

app.MapGet("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");

app.Run();

public sealed record WebLoginRequest(string Username, string Password);

internal static class RequestValidation
{
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
            AccountCommand.RefreshStatus or AccountCommand.ClearWorkshop => IsEmptyPayload(request.Payload),
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
                JsonElement element => element.Deserialize<T>(),
                null => default,
                _ => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(payload)),
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
                .OrderBy(x => x.PersonaName ?? x.SteamId)
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
    private readonly ILogger<OperationWorker> _logger;

    public OperationWorker(OperationQueue queue, OperationStore operations, AccountStore accounts, ISteamAccountGateway gateway, ILogger<OperationWorker> logger)
    {
        _queue = queue;
        _operations = operations;
        _accounts = accounts;
        _gateway = gateway;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var operationId in _queue.ReadAllAsync(stoppingToken))
        {
            var operation = _operations.Get(operationId);
            if (operation is null) continue;

            operation.State = OperationState.Running;
            try
            {
                var account = _accounts.Get(operation.AccountId)
                    ?? throw new InvalidOperationException("Account no longer exists.");

                var result = await _gateway.ExecuteAsync(account, operation.Command, operation.Payload, stoppingToken);
                if (result.Status is not null)
                {
                    account.ApplyStatus(result.Status, result.Prime);
                    account.StatusUpdatedUtc = DateTimeOffset.UtcNow;
                    account.LastLoginUtc = DateTimeOffset.UtcNow;
                    _accounts.Upsert(account);
                }
                operation.State = result.Success ? OperationState.Succeeded : OperationState.Failed;
                operation.Message = result.Message;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
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

    Task<GatewayResult> ExecuteAsync(AccountRecord account, AccountCommand command, object? payload, CancellationToken cancellationToken);
}

internal sealed record GatewayResult(bool Success, string Message, NfaLoader.Models.CsPremierScoreResult? Status = null, AccountPrimeBucket? Prime = null)
{
    public static GatewayResult WithStatus(bool success, string message, NfaLoader.Models.CsPremierScoreResult status, AccountPrimeBucket prime) =>
        new(success, message, status, prime);
}

internal sealed class AuthSessionStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, AuthSessionRecord> _sessions = new();

    private readonly TokenProtector _tokens;

    public AuthSessionStore(TokenProtector tokens)
    {
        _tokens = tokens;
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
                ProtectedRefreshToken = _tokens.Protect(result.RefreshToken),
                LastLoginUtc = DateTimeOffset.UtcNow,
            };

            var status = await gateway.QueryStatusAsync(result.RefreshToken, result.SteamId, session.CancellationToken.Token);
            account.ApplyStatus(status, await gateway.QueryPrimeAsync(result.RefreshToken, result.SteamId, status, session.CancellationToken.Token));
            account.StatusUpdatedUtc = DateTimeOffset.UtcNow;
            accounts.Upsert(account);

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
    public uint? CooldownReason { get; set; }
    public bool? VacBanned { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset? StatusUpdatedUtc { get; set; }

    public void ApplyStatus(NfaLoader.Models.CsPremierScoreResult status, AccountPrimeBucket? prime = null)
    {
        PremierRating = status.HasPremierScore ? checked((int)status.PremierRanking!.RankId) : null;
        PremierWins = status.HasPremierScore ? checked((int)status.PremierRanking!.Wins) : null;
        Cs2Level = status.PlayerLevel;
        CooldownSeconds = status.PenaltySeconds;
        CooldownReason = status.PenaltyReason;
        VacBanned = status.GcVacBannedOrUnknown;

        if (prime is not null && prime != AccountPrimeBucket.Unknown) Prime = prime.Value;
        else if (Prime == AccountPrimeBucket.Unknown && status.HasPremierScore) Prime = AccountPrimeBucket.Prime;

        Section = status.HasCooldownData && !status.HasCooldown && status.GcVacBannedOrUnknown is false
            ? AccountSection.PlayReady
            : AccountSection.NotPlayReady;
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
        account.CooldownReason, account.VacBanned, account.Note, account.StatusUpdatedUtc);

    public static AccountDetailDto ToDetail(AccountRecord account) => new(
        account.Id, account.SteamId, account.PersonaName, account.AvatarUrl, account.Prime, account.Section,
        account.PremierRating, account.PremierWins, account.Cs2Level, account.LastLoginUtc,
        account.CooldownSeconds, account.CooldownReason, account.VacBanned, account.Note, account.StatusUpdatedUtc);
}
