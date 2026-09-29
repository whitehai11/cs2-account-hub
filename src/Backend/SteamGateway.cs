using System.Text.Json;
using CS2AccountHub.Contracts;
using NfaLoader.Models;
using NfaLoader.Services;

internal sealed class SteamGateway : ISteamAccountGateway
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private readonly SteamCredentialsAuthService _auth = new();
    private readonly CsPremierScoreService _status = new();
    private readonly SteamPrimeStatusService _prime = new();
    private readonly CsLoadoutService _loadout = new();
    private readonly CsMatchHistoryService _matches = new();
    private readonly SteamWorkshopService _workshop = new();
    private readonly SteamProfileService _profile = new();
    private readonly SteamPublicProfileService _publicProfile = new();
    private readonly TokenProtector _tokens;
    private readonly SettingsStore _settings;

    public SteamGateway(TokenProtector tokens, SettingsStore settings)
    {
        _tokens = tokens;
        _settings = settings;
    }

    public Task<CredentialsAuthResult> AuthenticateAsync(
        string accountName,
        string password,
        Func<SteamGuardPrompt, CancellationToken, Task<string?>> guardCodeProvider,
        IProgress<string>? progress,
        CancellationToken cancellationToken) =>
        _auth.GetRefreshTokenAsync(accountName, password, guardCodeProvider, progress, cancellationToken);

    public async Task<CsPremierScoreResult> QueryStatusAsync(string refreshToken, string steamId, CancellationToken cancellationToken)
    {
        return await _status.QueryAsync(refreshToken, steamId, cancellationToken);
    }

    public Task<AccountPrimeBucket> QueryPrimeAsync(string refreshToken, string steamId, CsPremierScoreResult status, CancellationToken cancellationToken) =>
        _prime.QueryAsync(refreshToken, steamId, status, cancellationToken);

    public async Task<SteamProfileSnapshot?> QueryProfileAsync(string steamId, CancellationToken cancellationToken)
    {
        try
        {
            return await _publicProfile.GetAsync(steamId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLog.Warn($"Steam profile lookup failed ({exception.GetType().Name}).");
            return null;
        }
    }

    public async Task<GatewayResult> ExecuteAsync(AccountRecord account, AccountCommand command, object? payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(account.ProtectedRefreshToken))
        {
            return new GatewayResult(false, "The account has no server-side Steam session.");
        }

        var refreshToken = _tokens.Unprotect(account.ProtectedRefreshToken);
        switch (command)
        {
                case AccountCommand.RefreshStatus:
                {
                    var status = await QueryStatusAsync(refreshToken, account.SteamId, cancellationToken);
                    var prime = await QueryPrimeAsync(refreshToken, account.SteamId, status, cancellationToken);
                    return GatewayResult.WithStatus(true, status.StatusText, status, prime);
                }

                case AccountCommand.ApplyLoadout:
                {
                    var presetDto = Deserialize<LoadoutPresetDto>(payload);
                    var preset = presetDto is null
                        ? CsLoadoutPreset.Default()
                        : new CsLoadoutPreset
                        {
                            Name = presetDto.Name,
                            T = new Dictionary<uint, uint>(presetDto.T),
                            Ct = new Dictionary<uint, uint>(presetDto.Ct),
                        };
                    var result = await _loadout.ApplyPresetAsync(preset, refreshToken, account.SteamId, cancellationToken);
                    return new GatewayResult(result.IsSuccess, $"Loadout: {result.Confirmed}/{result.Requested} slots confirmed.");
                }

                case AccountCommand.SyncLoadout:
                {
                    var preset = _settings.GetLoadout();
                    var snapshot = await _loadout.ReadCurrentAsync(refreshToken, account.SteamId, preset.T, preset.Ct, cancellationToken);
                    return GatewayResult.WithLoadout(true, $"Loadout geprüft: {snapshot.MismatchCount} Abweichungen.", snapshot);
                }

                case AccountCommand.RefreshMatches:
                {
                    var matches = await _matches.QueryRecentAsync(refreshToken, account.SteamId, cancellationToken);
                    return GatewayResult.WithMatches(true, $"{matches.Count} letzte Spiele geladen.", matches);
                }

                case AccountCommand.ClearWorkshop:
                {
                    var count = await _workshop.ClearSubscriptionsAsync(refreshToken, cancellationToken: cancellationToken);
                    return new GatewayResult(true, $"Cleared {count} Workshop subscriptions.");
                }

                case AccountCommand.Personalize:
                {
                    var preset = Deserialize<PersonalizePresetDto>(payload)
                        ?? throw new InvalidOperationException("Personalization payload is missing.");
                    string? avatarPath = null;
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(preset.AvatarBase64))
                        {
                            avatarPath = Path.Combine(Path.GetTempPath(), $"cs2-hub-avatar-{Guid.NewGuid():N}.jpg");
                            await File.WriteAllBytesAsync(avatarPath, Convert.FromBase64String(preset.AvatarBase64), cancellationToken);
                        }

                        var request = new SteamProfileApplyRequest(preset.PersonaName, preset.RealName, preset.Summary, avatarPath, preset.ClearAliasHistory);
                        var result = await _profile.ApplyAsync(refreshToken, request, cancellationToken: cancellationToken);
                        return new GatewayResult(result.IsFullSuccess, "Personalization completed.");
                    }
                    finally
                    {
                        if (avatarPath is not null) try { File.Delete(avatarPath); } catch { }
                    }
                }

                case AccountCommand.SignInToSteam:
                    return new GatewayResult(false, "A Windows sign-in handoff is not implemented yet.");

            default:
                return new GatewayResult(false, "Unknown account command.");
        }
    }

    private static T? Deserialize<T>(object? payload)
    {
        if (payload is null) return default;
        if (payload is JsonElement element)
        {
            return element.Deserialize<T>(WebJson);
        }

        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(payload, WebJson), WebJson);
    }
}
