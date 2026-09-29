using System.Globalization;
using NfaLoader.Models;

namespace NfaLoader.Services;

internal sealed class CsMatchHistoryService
{
    private const uint MatchList = 9139;
    private const uint RequestRecentUserGames = 9141;
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<IReadOnlyList<CsRecentMatch>> QueryRecentAsync(
        string refreshToken,
        string steamId,
        CancellationToken cancellationToken)
    {
        if (!ulong.TryParse(steamId, CultureInfo.InvariantCulture, out var steamId64))
        {
            throw new InvalidOperationException("Steam-ID is invalid.");
        }

        var accountId = CsGcSession.GetAccountId(steamId64);
        await using var cmClient = new SteamCmClient(HttpClient);
        await cmClient.ConnectAndLogOnAsync(refreshToken, steamId, cancellationToken);
        try
        {
            await cmClient.SetGamesPlayedAsync([CsGcSession.Cs2AppId], cancellationToken);
            await CsGcSession.ConnectAsync(cmClient, cancellationToken);
            var responseTask = cmClient.WaitForGcMessageAsync(
                CsGcSession.Cs2AppId,
                MatchList,
                TimeSpan.FromSeconds(30),
                cancellationToken,
                cacheUnmatched: true);
            await cmClient.SendGcProtobufMessageAsync(
                CsGcSession.Cs2AppId,
                RequestRecentUserGames,
                EncodeRequest(accountId),
                cancellationToken);
            var response = await responseTask;
            return DecodeMatchList(response.Payload).Take(10).ToArray();
        }
        finally
        {
            try { await cmClient.SetGamesPlayedAsync([], CancellationToken.None); } catch { }
        }
    }

    private static byte[] EncodeRequest(uint accountId) =>
        SteamProtoWriter.Build(writer => writer.WriteUInt32(1, accountId));

    private static IReadOnlyList<CsRecentMatch> DecodeMatchList(byte[] payload)
    {
        var matches = new List<CsRecentMatch>();
        var reader = new SteamProtoReader(payload);
        while (reader.TryReadTag(out var field, out var wireType))
        {
            if (field == 4)
            {
                matches.Add(DecodeMatch(reader.ReadLengthDelimited(wireType)));
            }
            else
            {
                reader.Skip(wireType);
            }
        }

        return matches
            .OrderByDescending(match => match.PlayedUtc ?? DateTimeOffset.MinValue)
            .ToArray();
    }

    private static CsRecentMatch DecodeMatch(byte[] payload)
    {
        ulong matchId = 0;
        DateTimeOffset? playedUtc = null;
        string? map = null;
        var rounds = new List<RoundStatsSnapshot>();
        var reader = new SteamProtoReader(payload);

        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 1:
                    matchId = reader.ReadVarint(wireType);
                    break;
                case 2:
                {
                    var seconds = reader.ReadVarint(wireType);
                    if (seconds > 0) playedUtc = DateTimeOffset.FromUnixTimeSeconds((long)seconds);
                    break;
                }
                case 3:
                    map = DecodeWatchableMap(reader.ReadLengthDelimited(wireType)) ?? map;
                    break;
                case 4:
                    rounds.Add(DecodeRoundStats(reader.ReadLengthDelimited(wireType)));
                    break;
                case 5:
                    rounds.Add(DecodeRoundStats(reader.ReadLengthDelimited(wireType)));
                    break;
                }
                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        var final = rounds
            .OrderBy(round => round.Round ?? int.MinValue)
            .LastOrDefault();
        map ??= final?.Map;
        var teamScore = final is not null && final.TeamScores.Count >= 2 ? final.TeamScores[0] : null;
        var opponentScore = final is not null && final.TeamScores.Count >= 2 ? final.TeamScores[1] : null;
        var outcome = teamScore is not null && opponentScore is not null
            ? teamScore > opponentScore ? "win" : teamScore < opponentScore ? "loss" : "draw"
            : null;

        return new CsRecentMatch(matchId, playedUtc, map, final?.MatchResult, teamScore, opponentScore, outcome, null, null, null, null);
    }

    private static string? DecodeWatchableMap(byte[] payload)
    {
        var reader = new SteamProtoReader(payload);
        while (reader.TryReadTag(out var field, out var wireType))
        {
            if (field == 10) return reader.ReadString(wireType);
            reader.Skip(wireType);
        }
        return null;
    }

    private static RoundStatsSnapshot DecodeRoundStats(byte[] payload)
    {
        string? map = null;
        int? round = null;
        int? matchResult = null;
        var teamScores = new List<int>();
        var reader = new SteamProtoReader(payload);
        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 3: map = reader.ReadString(wireType); break;
                case 4: round = (int)reader.ReadVarint(wireType); break;
                case 11: matchResult = (int)reader.ReadVarint(wireType); break;
                case 12: teamScores.Add((int)reader.ReadVarint(wireType)); break;
                default: reader.Skip(wireType); break;
            }
        }
        return new RoundStatsSnapshot(round, map, matchResult, teamScores);
    }

    private sealed record RoundStatsSnapshot(int? Round, string? Map, int? MatchResult, List<int> TeamScores);
}
