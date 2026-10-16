using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using NfaLoader.Localization;
using NfaLoader.Models;

namespace NfaLoader.Services;

internal sealed class CsPremierScoreService
{
    private const uint ClientRequestPlayersProfile = 9127;
    private const uint PlayersProfile = 9128;
    private const uint MatchmakingClient2GCHello = 9109;
    private const uint MatchmakingGC2ClientHello = 9110;
    private const uint PremierRankTypeId = 11;
    // 9110 may be pushed unprompted before or after the GC welcome; the wait must cover the handshake window, and the receive layer also buffers messages that arrive early.
    // The "disconnect GC, then reconnect" retry loop is still kept (same as cooldown.js: 6 rounds, 11 seconds of waiting per round,
    // reconnect 2.5 seconds after disconnecting), plus an overall time limit so GC welcome retries within one round cannot drag out the total time.
    private const int MaxHelloCycles = 6;
    private static readonly TimeSpan HelloWaitTimeout = TimeSpan.FromSeconds(11);
    private static readonly TimeSpan CachedHelloPollTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan GcReconnectDelay = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan RetryWelcomeTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan HelloTotalBudget = TimeSpan.FromSeconds(45);

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private readonly ILogger<CsPremierScoreService> _logger;

    public CsPremierScoreService(ILogger<CsPremierScoreService> logger)
    {
        _logger = logger;
    }

    public async Task<CsPremierScoreResult> QueryAsync(
        string refreshToken,
        string steamId,
        CancellationToken cancellationToken = default)
    {
        if (!ulong.TryParse(steamId, CultureInfo.InvariantCulture, out var steamId64))
        {
            throw new InvalidOperationException(Loc.T("Cs_Premier_BadSteam64"));
        }

        var accountId = CsGcSession.GetAccountId(steamId64);
        var checkId = Guid.NewGuid();
        var startedAt = Stopwatch.GetTimestamp();
        var helloDeadline = DateTimeOffset.UtcNow + HelloTotalBudget;
        CsAccountProfile? helloData = null;
        var usedProfileFallback = false;
        var observedGcMessages = new ConcurrentDictionary<uint, int>();
        _logger.LogInformation("GC status check {CheckId} started for account {AccountId}.", checkId, accountId);

        await using var cmClient = new SteamCmClient(HttpClient);
        cmClient.SetGcMessageTap(message =>
        {
            if (message.AppId != CsGcSession.Cs2AppId) return;
            observedGcMessages.AddOrUpdate(message.MessageType, 1, static (_, count) => count + 1);
        });
        await cmClient.ConnectAndLogOnAsync(refreshToken, steamId, cancellationToken);
        _logger.LogDebug("GC status check {CheckId} completed CM login for account {AccountId} after {ElapsedMs:0} ms.", checkId, accountId, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);

        try
        {
            var helloTask = WaitForMatchmakingHelloAsync(cmClient, cancellationToken);

            var webSession = await SteamWebSession.BuildAsync(cmClient, refreshToken, steamId, cancellationToken);
            _logger.LogDebug("GC status check {CheckId} built the web session for account {AccountId} after {ElapsedMs:0} ms.", checkId, accountId, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);

            await cmClient.SetGamesPlayedAsync([CsGcSession.Cs2AppId], cancellationToken);
            var welcomePayload = await CsGcSession.RequestWelcomeAsync(
                cmClient,
                RemainingTimeout(helloDeadline, TimeSpan.FromSeconds(45)),
                cancellationToken);
            var welcomeHelloPayload = TryDecodeMatchmakingHelloFromWelcome(welcomePayload);
            if (welcomeHelloPayload is not null)
            {
                helloData = DecodeAccountProfile(welcomeHelloPayload);
            }
            _logger.LogDebug("GC status check {CheckId} received the initial GC welcome for account {AccountId} after {ElapsedMs:0} ms.", checkId, accountId, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);

            // The 9110 waiter is attached before entering 730 so a 9110 pushed unprompted during the welcome phase is not missed.
            // PlayersProfile is still requested for Premier rating/level, and its restriction fields are used only when the payload explicitly contains them.
            await cmClient.SendGcProtobufMessageAsync(
                CsGcSession.Cs2AppId,
                MatchmakingClient2GCHello,
                [],
                cancellationToken);

            var profileTask = cmClient.WaitForGcMessageAsync(
                CsGcSession.Cs2AppId,
                PlayersProfile,
                TimeSpan.FromSeconds(30),
                cancellationToken);

            await cmClient.SendGcProtobufMessageAsync(
                CsGcSession.Cs2AppId,
                ClientRequestPlayersProfile,
                EncodePlayersProfileRequest(accountId),
                cancellationToken);

            var profileMessage = await profileTask;
            var profile = DecodePlayersProfile(accountId, profileMessage.Payload);
            _logger.LogDebug("GC status check {CheckId} received PlayersProfile for account {AccountId}: restrictionFields={HasRestrictionData}, rankings={RankingCount}, playerLevel={PlayerLevel}.", checkId, accountId, profile.HasRestrictionData, profile.Rankings.Count, profile.PlayerLevel);

            if (helloData is null)
            {
                helloData = await helloTask;
                helloData ??= await WaitForMatchmakingHelloAsync(
                    cmClient,
                    CachedHelloPollTimeout,
                    cancellationToken);
            }

            for (var cycle = 2;
                helloData is null && cycle <= MaxHelloCycles && DateTimeOffset.UtcNow < helloDeadline;
                cycle++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await cmClient.SetGamesPlayedAsync([], cancellationToken);
                    await Task.Delay(GcReconnectDelay, cancellationToken);
                    helloTask = WaitForMatchmakingHelloAsync(cmClient, cancellationToken);
                    await cmClient.SetGamesPlayedAsync([CsGcSession.Cs2AppId], cancellationToken);
                    var reconnectWelcomePayload = await CsGcSession.RequestWelcomeAsync(
                        cmClient,
                        RemainingTimeout(helloDeadline, RetryWelcomeTimeout),
                        cancellationToken);
                    var reconnectHelloPayload = TryDecodeMatchmakingHelloFromWelcome(reconnectWelcomePayload);
                    helloData = reconnectHelloPayload is null
                        ? null
                        : DecodeAccountProfile(reconnectHelloPayload);
                    _logger.LogDebug("GC status check {CheckId} received reconnect welcome for account {AccountId} on cycle {Cycle} after {ElapsedMs:0} ms.", checkId, accountId, cycle, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
                }
                catch (TimeoutException)
                {
                    // GC reconnect failed: the Premier rating is already in hand, so return the cooldown as unknown.
                    break;
                }

                await cmClient.SendGcProtobufMessageAsync(
                    CsGcSession.Cs2AppId,
                    MatchmakingClient2GCHello,
                    [],
                    cancellationToken);
                if (helloData is null)
                {
                    helloData = await helloTask;
                    helloData ??= await WaitForMatchmakingHelloAsync(
                        cmClient,
                        CachedHelloPollTimeout,
                        cancellationToken);
                }
            }

            var restrictionData = helloData ?? (profile.HasRestrictionData ? profile : null);
            usedProfileFallback = helloData is null && profile.HasRestrictionData;

            var premier = profile.Rankings.FirstOrDefault(ranking =>
                ranking.RankTypeId == PremierRankTypeId);

            return new CsPremierScoreResult(
                steamId,
                accountId,
                premier,
                profile.Rankings,
                restrictionData?.PenaltySeconds,
                restrictionData?.PenaltyReason,
                restrictionData?.VacBanned,
                profile.PlayerLevel,
                profile.InMatch);
        }
        finally
        {
            var gcMessages = string.Join(",", observedGcMessages.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}x{pair.Value}"));
            _logger.LogInformation("GC status check {CheckId} finished for account {AccountId} in {ElapsedMs:0} ms with cooldownData={HasCooldownData}, usedProfileFallback={UsedProfileFallback}, gcMessages={GcMessages}.", checkId, accountId, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, helloData is not null || usedProfileFallback, usedProfileFallback, string.IsNullOrWhiteSpace(gcMessages) ? "none" : gcMessages);
            cmClient.SetGcMessageTap(null);
            try
            {
                await cmClient.SetGamesPlayedAsync([], CancellationToken.None);
            }
            catch
            {
                // Best-effort cleanup before logoff.
            }
        }
    }

    /// <summary>Waits one round for 9110; returns null on timeout (the caller disconnects GC, reconnects and tries again). 9110 and the PlayersProfile
    /// account entry are the same proto message type, so DecodeAccountProfile is reused directly.</summary>
    private static async Task<CsAccountProfile?> WaitForMatchmakingHelloAsync(
        SteamCmClient cmClient,
        CancellationToken cancellationToken)
    {
        return await WaitForMatchmakingHelloAsync(cmClient, HelloWaitTimeout, cancellationToken);
    }

    private static TimeSpan RemainingTimeout(DateTimeOffset deadline, TimeSpan maximum)
    {
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return TimeSpan.FromMilliseconds(1);
        return remaining < maximum ? remaining : maximum;
    }

    // CMsgClientWelcome.game_data2 (field 6) carries the same
    // CMsgGCCStrike15_v2_MatchmakingGC2ClientHello payload as message 9110.
    // Steam commonly delivers the cooldown state this way instead of emitting a separate 9110.
    private static byte[]? TryDecodeMatchmakingHelloFromWelcome(byte[] welcomePayload)
    {
        try
        {
            var reader = new SteamProtoReader(welcomePayload);
            while (reader.TryReadTag(out var field, out var wireType))
            {
                if (field == 6)
                {
                    var payload = reader.ReadLengthDelimited(wireType);
                    return payload.Length == 0 ? null : payload;
                }

                reader.Skip(wireType);
            }
        }
        catch (InvalidOperationException)
        {
            // A malformed optional welcome field must not make the whole status check fail.
        }

        return null;
    }

    private static async Task<CsAccountProfile?> WaitForMatchmakingHelloAsync(
        SteamCmClient cmClient,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var message = await cmClient.WaitForGcMessageAsync(
                CsGcSession.Cs2AppId,
                MatchmakingGC2ClientHello,
                timeout,
                cancellationToken,
                cacheUnmatched: true);
            return DecodeAccountProfile(message.Payload);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private static byte[] EncodePlayersProfileRequest(uint accountId)
    {
        return SteamProtoWriter.Build(writer =>
        {
            writer.WriteUInt32(3, accountId);
            writer.WriteUInt32(4, 32);
        });
    }

    private static CsAccountProfile DecodePlayersProfile(uint requestedAccountId, byte[] body)
    {
        var profiles = new List<CsAccountProfile>();
        var reader = new SteamProtoReader(body);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 2:
                    profiles.Add(DecodeAccountProfile(reader.ReadLengthDelimited(wireType)));
                    break;

                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        var profile = profiles.FirstOrDefault(value => value.AccountId == requestedAccountId)
            ?? profiles.FirstOrDefault();

        if (profile is null)
        {
            throw new InvalidOperationException(Loc.T("Cs_Premier_NoProfile"));
        }

        return profile;
    }

    private static CsAccountProfile DecodeAccountProfile(byte[] body)
    {
        uint accountId = 0;
        uint penaltySeconds = 0;
        uint penaltyReason = 0;
        var vacBanned = 0;
        var hasRestrictionData = false;
        int? playerLevel = null;
        var inMatch = false;
        var rankings = new List<CsRankingInfo>();
        var reader = new SteamProtoReader(body);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 1:
                    accountId = (uint)reader.ReadVarint(wireType);
                    break;

                case 2:
                    inMatch = true;
                    reader.Skip(wireType);
                    break;

                case 4:
                {
                    // penalty_seconds is actually signed: once the cooldown has expired the GC sends "negative seconds remaining", which protobuf sign-extends into
                    // a 64-bit varint. Reading it straight as uint truncates it to an absurd number near 2^32 (a freshly unbanned account once showed as "49707 days...").
                    // Read it as int32, and turn anything ≤0 (expired / no cooldown) into zero.
                    var penalty = unchecked((int)reader.ReadVarint(wireType));
                    penaltySeconds = penalty > 0 ? (uint)penalty : 0;
                    hasRestrictionData = true;
                    break;
                }

                case 5:
                    penaltyReason = (uint)reader.ReadVarint(wireType);
                    hasRestrictionData = true;
                    break;

                case 6:
                    vacBanned = (int)reader.ReadVarint(wireType);
                    hasRestrictionData = true;
                    break;

                case 7:
                case 20:
                    rankings.Add(DecodeRanking(reader.ReadLengthDelimited(wireType)));
                    break;

                case 17:
                    playerLevel = (int)reader.ReadVarint(wireType);
                    break;

                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        return new CsAccountProfile(
            accountId,
            rankings,
            penaltySeconds,
            penaltyReason,
            vacBanned,
            playerLevel,
            inMatch,
            hasRestrictionData);
    }

    private static CsRankingInfo DecodeRanking(byte[] body)
    {
        uint rankTypeId = 0;
        uint rankId = 0;
        uint wins = 0;
        uint? mapId = null;
        var reader = new SteamProtoReader(body);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 2:
                    rankId = (uint)reader.ReadVarint(wireType);
                    break;

                case 3:
                    wins = (uint)reader.ReadVarint(wireType);
                    break;

                case 6:
                    rankTypeId = (uint)reader.ReadVarint(wireType);
                    break;

                case 13:
                    mapId ??= DecodePerMapRankMapId(reader.ReadLengthDelimited(wireType));
                    break;

                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        return new CsRankingInfo(rankTypeId, rankId, wins, mapId);
    }

    private static uint? DecodePerMapRankMapId(byte[] body)
    {
        var reader = new SteamProtoReader(body);
        while (reader.TryReadTag(out var field, out var wireType))
        {
            if (field == 1)
            {
                return (uint)reader.ReadVarint(wireType);
            }

            reader.Skip(wireType);
        }

        return null;
    }

    private sealed record CsAccountProfile(
        uint AccountId,
        IReadOnlyList<CsRankingInfo> Rankings,
        uint PenaltySeconds,
        uint PenaltyReason,
        int VacBanned,
        int? PlayerLevel,
        bool InMatch,
        bool HasRestrictionData);
}
