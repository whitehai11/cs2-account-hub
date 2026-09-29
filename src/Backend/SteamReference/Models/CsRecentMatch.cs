namespace NfaLoader.Models;

internal sealed record CsRecentMatch(
    ulong MatchId,
    DateTimeOffset? PlayedUtc,
    string? Map,
    int? ResultCode,
    int? TeamScore,
    int? OpponentScore);
