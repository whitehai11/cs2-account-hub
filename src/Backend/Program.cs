using System.Text.Json;
using CS2AccountHub.Contracts;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

var dataDirectory = Environment.GetEnvironmentVariable("CS2_HUB_DATA_DIR")
    ?? Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDirectory);

builder.Services.AddSingleton<AccountStore>();
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<OperationStore>();
builder.Services.AddSingleton<HandoffStore>();
builder.Services.AddSingleton<AuthSessionStore>();
builder.Services.AddSingleton<OperationQueue>();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")))
    .SetApplicationName("CS2AccountHub");
builder.Services.AddSingleton<TokenProtector>();
builder.Services.AddSingleton<ISteamAccountGateway, SteamGateway>();
builder.Services.AddHostedService<OperationWorker>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()
        .SetIsOriginAllowed(_ => true));
});

var app = builder.Build();
var apiKey = Environment.GetEnvironmentVariable("CS2_HUB_API_KEY");
if (!string.IsNullOrWhiteSpace(apiKey))
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/api") &&
            (!context.Request.Headers.TryGetValue("X-CS2-Hub-Key", out var supplied) || supplied != apiKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next();
    });
}
app.UseCors();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

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
    settings.SetLoadout(request);
    return Results.NoContent();
});
app.MapGet("/api/settings/personalize", (SettingsStore settings) => Results.Ok(settings.GetPersonalize()));
app.MapPut("/api/settings/personalize", (PersonalizePresetDto request, SettingsStore settings) =>
{
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
    if (string.IsNullOrWhiteSpace(request.AccountName) || string.IsNullOrWhiteSpace(request.Password))
    {
        return Results.BadRequest(new { message = "Account name and password are required." });
    }

    var session = await sessions.StartAsync(request.AccountName.Trim(), request.Password, gateway, accounts, cancellationToken);
    return Results.Ok(new StartAuthResponse(session.Id, session.State, session.SteamId, session.PromptMessage));
});

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
    if (string.IsNullOrWhiteSpace(request.Code))
    {
        return Results.BadRequest(new { message = "A Steam Guard code is required." });
    }

    var result = await sessions.SubmitCodeAsync(id, request.Code.Trim(), gateway, accounts, cancellationToken);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.MapGet("/api/operations/{id:guid}", (Guid id, OperationStore operations) =>
{
    var operation = operations.Get(id);
    return operation is null ? Results.NotFound() : Results.Ok(OperationMappings.ToDto(operation));
});

app.MapGet("/api/operations/{id:guid}/handoff", (Guid id, OperationStore operations, HandoffStore handoffs) =>
{
    var operation = operations.Get(id);
    if (operation is null || operation.Command != AccountCommand.SignInToSteam || operation.State != OperationState.Succeeded)
    {
        return Results.NotFound();
    }

    var handoff = handoffs.Consume(id);
    return handoff is null ? Results.NotFound() : Results.Ok(handoff);
});

app.MapPost("/api/accounts/{id:guid}/commands", async (
    Guid id,
    CommandRequest request,
    AccountStore accounts,
    OperationStore operations,
    OperationQueue queue) =>
{
    if (accounts.Get(id) is null)
    {
        return Results.NotFound();
    }

    var operation = operations.Create(id, request.Command, request.Payload);
    await queue.EnqueueAsync(operation.Id);
    return Results.Accepted($"/api/operations/{operation.Id}", OperationMappings.ToDto(operation));
});

app.Run();

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
            Console.Error.WriteLine($"Could not load account state: {exception.Message}");
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
    private readonly HandoffStore _handoffs;
    private readonly ILogger<OperationWorker> _logger;

    public OperationWorker(OperationQueue queue, OperationStore operations, AccountStore accounts, ISteamAccountGateway gateway, HandoffStore handoffs, ILogger<OperationWorker> logger)
    {
        _queue = queue;
        _operations = operations;
        _accounts = accounts;
        _gateway = gateway;
        _handoffs = handoffs;
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

                if (operation.Command == AccountCommand.SignInToSteam)
                {
                    _handoffs.Create(operation.Id, account);
                    operation.HandoffReady = true;
                    operation.State = OperationState.Succeeded;
                    operation.Message = "One-time Steam sign-in handoff is ready for the Windows bridge.";
                    continue;
                }

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
                operation.Message = ex.Message;
                _logger.LogError(ex, "Operation {OperationId} failed", operation.Id);
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
        catch (Exception exception)
        {
            session.State = AuthState.Failed;
            session.PromptMessage = exception.Message;
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
    public bool HandoffReady { get; set; }
}

internal sealed class HandoffStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, (SteamSignInHandoff Handoff, DateTimeOffset Expires)> _handoffs = new();
    private readonly TokenProtector _tokens;

    public HandoffStore(TokenProtector tokens)
    {
        _tokens = tokens;
    }

    public SteamSignInHandoff Create(Guid operationId, AccountRecord account)
    {
        if (string.IsNullOrWhiteSpace(account.ProtectedRefreshToken) || string.IsNullOrWhiteSpace(account.AccountName))
        {
            throw new InvalidOperationException("The account has no usable local Steam sign-in data.");
        }

        var expires = DateTimeOffset.UtcNow.AddMinutes(2);
        var payload = new SteamSignInPayload(
            account.AccountName,
            account.SteamId,
            _tokens.Unprotect(account.ProtectedRefreshToken));
        var handoff = new SteamSignInHandoff(
            operationId,
            account.Id,
            JsonSerializer.Serialize(payload),
            expires);

        lock (_gate) _handoffs[operationId] = (handoff, expires);
        return handoff;
    }

    public SteamSignInHandoff? Consume(Guid operationId)
    {
        lock (_gate)
        {
            if (!_handoffs.Remove(operationId, out var entry) || entry.Expires <= DateTimeOffset.UtcNow)
            {
                return null;
            }

            return entry.Handoff;
        }
    }
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
