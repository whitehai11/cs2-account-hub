namespace CS2AccountHub.Contracts;

public enum AccountSection
{
    PlayReady = 0,
    NotPlayReady = 1,
    VacBanned = 2,
}

public enum AccountPrimeBucket
{
    Unknown = 0,
    Prime = 1,
    NonPrime = 2,
}

public enum OperationState
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
}

public enum AccountCommand
{
    RefreshStatus = 0,
    ClearWorkshop = 1,
    ApplyLoadout = 2,
    Personalize = 3,
    SignInToSteam = 4,
    SyncLoadout = 5,
    RefreshMatches = 6,
}

public enum AuthState
{
    AwaitingSteamGuard = 0,
    AwaitingConfirmation = 1,
    Completed = 2,
    Failed = 3,
}

public sealed class AccountSummaryDto
{
    public AccountSummaryDto(Guid id, string? accountName, string steamId, string? personaName, string? avatarUrl, AccountPrimeBucket prime, AccountSection section, int? premierRating, int? cs2Level, DateTimeOffset? lastLoginUtc, uint? cooldownSeconds, uint? cooldownReason, bool? vacBanned, string? note, DateTimeOffset? statusUpdatedUtc, DateTimeOffset? cooldownEndsUtc = null)
    {
        Id = id; AccountName = accountName; SteamId = steamId; PersonaName = personaName; AvatarUrl = avatarUrl; Prime = prime; Section = section;
        PremierRating = premierRating; Cs2Level = cs2Level; LastLoginUtc = lastLoginUtc; CooldownSeconds = cooldownSeconds;
        CooldownReason = cooldownReason; VacBanned = vacBanned; Note = note; StatusUpdatedUtc = statusUpdatedUtc; CooldownEndsUtc = cooldownEndsUtc;
    }

    public Guid Id { get; set; }
    public string? AccountName { get; set; }
    public string SteamId { get; set; }
    public string? PersonaName { get; set; }
    public string? AvatarUrl { get; set; }
    public AccountPrimeBucket Prime { get; set; }
    public AccountSection Section { get; set; }
    public int? PremierRating { get; set; }
    public int? Cs2Level { get; set; }
    public DateTimeOffset? LastLoginUtc { get; set; }
    public uint? CooldownSeconds { get; set; }
    public DateTimeOffset? CooldownEndsUtc { get; set; }
    public uint? NextCooldownSeconds { get; set; }
    public DateTimeOffset? NextCheckUtc { get; set; }
    public DateTimeOffset? LastCheckUtc { get; set; }
    public string? HealthStatus { get; set; }
    public bool LoginRequiresReauthentication { get; set; }
    public bool? InMatch { get; set; }
    public bool? PublicPlayingCs2 { get; set; }
    public int? LoadoutMismatchCount { get; set; }
    public DateTimeOffset? LoadoutCheckedUtc { get; set; }
    public uint? CooldownReason { get; set; }
    public bool? VacBanned { get; set; }
    public bool? Cs2Owned { get; set; }
    public DateTimeOffset? Cs2OwnershipCheckedUtc { get; set; }
    public bool? SteamGameBanned { get; set; }
    public int? SteamGameBanCount { get; set; }
    public bool? SteamVacBanned { get; set; }
    public int? SteamVacBanCount { get; set; }
    public bool? SteamCommunityBanned { get; set; }
    public string? SteamEconomyBan { get; set; }
    public DateTimeOffset? SteamBansCheckedUtc { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset? StatusUpdatedUtc { get; set; }
    public bool IsPermaGreen { get; set; }
    public bool MonitoringPaused { get; set; }
}

public sealed class AccountDetailDto
{
    public AccountDetailDto(Guid id, string? accountName, string steamId, string? personaName, string? avatarUrl, AccountPrimeBucket prime, AccountSection section, int? premierRating, int? premierWins, int? cs2Level, DateTimeOffset? lastLoginUtc, uint? cooldownSeconds, uint? cooldownReason, bool? vacBanned, string? note, DateTimeOffset? statusUpdatedUtc, DateTimeOffset? cooldownEndsUtc = null)
    {
        Id = id; AccountName = accountName; SteamId = steamId; PersonaName = personaName; AvatarUrl = avatarUrl; Prime = prime; Section = section;
        PremierRating = premierRating; PremierWins = premierWins; Cs2Level = cs2Level; LastLoginUtc = lastLoginUtc;
        CooldownSeconds = cooldownSeconds; CooldownReason = cooldownReason; VacBanned = vacBanned; Note = note; StatusUpdatedUtc = statusUpdatedUtc; CooldownEndsUtc = cooldownEndsUtc;
    }

    public Guid Id { get; set; }
    public string? AccountName { get; set; }
    public string SteamId { get; set; }
    public string? PersonaName { get; set; }
    public string? AvatarUrl { get; set; }
    public AccountPrimeBucket Prime { get; set; }
    public AccountSection Section { get; set; }
    public int? PremierRating { get; set; }
    public int? PremierWins { get; set; }
    public int? Cs2Level { get; set; }
    public DateTimeOffset? LastLoginUtc { get; set; }
    public uint? CooldownSeconds { get; set; }
    public DateTimeOffset? CooldownEndsUtc { get; set; }
    public uint? NextCooldownSeconds { get; set; }
    public DateTimeOffset? NextCheckUtc { get; set; }
    public DateTimeOffset? LastCheckUtc { get; set; }
    public string? HealthStatus { get; set; }
    public bool LoginRequiresReauthentication { get; set; }
    public bool? InMatch { get; set; }
    public bool? PublicPlayingCs2 { get; set; }
    public int? LoadoutMismatchCount { get; set; }
    public DateTimeOffset? LoadoutCheckedUtc { get; set; }
    public Dictionary<uint, uint> CurrentLoadoutT { get; set; } = new();
    public Dictionary<uint, uint> CurrentLoadoutCt { get; set; } = new();
    public DateTimeOffset? MatchesCheckedUtc { get; set; }
    public string? MatchesStatus { get; set; }
    public IReadOnlyList<RecentMatchDto> RecentMatches { get; set; } = Array.Empty<RecentMatchDto>();
    public IReadOnlyList<RatingPointDto> RatingHistory { get; set; } = Array.Empty<RatingPointDto>();
    public uint? CooldownReason { get; set; }
    public bool? VacBanned { get; set; }
    public bool? Cs2Owned { get; set; }
    public DateTimeOffset? Cs2OwnershipCheckedUtc { get; set; }
    public bool? SteamGameBanned { get; set; }
    public int? SteamGameBanCount { get; set; }
    public bool? SteamVacBanned { get; set; }
    public int? SteamVacBanCount { get; set; }
    public bool? SteamCommunityBanned { get; set; }
    public string? SteamEconomyBan { get; set; }
    public DateTimeOffset? SteamBansCheckedUtc { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset? StatusUpdatedUtc { get; set; }
    public bool IsPermaGreen { get; set; }
    public bool MonitoringPaused { get; set; }
}

public sealed record UpdateNoteRequest(string? Note);

public sealed record RecentMatchDto(
    ulong MatchId,
    DateTimeOffset? PlayedUtc,
    string? Map,
    int? ResultCode,
    int? TeamScore,
    int? OpponentScore,
    string? Outcome = null,
    string? Mode = null,
    int? Kills = null,
    int? Assists = null,
    int? Deaths = null);

public sealed record RatingPointDto(DateTimeOffset CapturedUtc, int Rating);

public sealed record SetPrimeRequest(AccountPrimeBucket Prime);

public sealed record SetPermaGreenRequest(bool Enabled);

public sealed record SetMonitoringRequest(bool Paused);

public sealed record AccountTimelineEventDto(
    Guid Id,
    Guid AccountId,
    string SteamId,
    string? PersonaName,
    string? AvatarUrl,
    bool IsPermaGreen,
    string Type,
    string Title,
    string? Detail,
    string Severity,
    DateTimeOffset OccurredUtc);

public sealed record SteamMailMessageDto(
    Guid Id,
    string Subject,
    string Sender,
    DateTimeOffset ReceivedUtc,
    bool IsRead,
    string Preview,
    bool HasBody,
    string GroupId = "primary");

public sealed record SteamMailDetailDto(
    Guid Id,
    string Subject,
    string Sender,
    DateTimeOffset ReceivedUtc,
    bool IsRead,
    string Body,
    string GroupId = "primary");

public sealed record SteamMailGroupDto(
    string Id,
    string Label,
    int Total,
    int Unread,
    bool Enabled);

public sealed record LoadoutPresetDto(
    string Name,
    IReadOnlyDictionary<uint, uint> T,
    IReadOnlyDictionary<uint, uint> Ct);

public sealed record PersonalizePresetDto(
    string? PersonaName,
    string? RealName,
    string? Summary,
    string? AvatarBase64,
    bool ClearAliasHistory);

public sealed record StartAuthRequest(string AccountName, string Password, Guid? ExistingAccountId = null);

public sealed record StartAuthResponse(
    Guid SessionId,
    AuthState State,
    string? SteamId,
    string? PromptMessage);

public sealed record SubmitGuardCodeRequest(string Code);

public sealed record SteamAuthenticatorSummaryDto(
    Guid Id,
    string AccountName,
    Guid? LinkedAccountId,
    string? LinkedDisplayName,
    DateTimeOffset ImportedUtc);

public sealed record SteamAuthenticatorCodeDto(
    Guid Id,
    string AccountName,
    string Code,
    int SecondsRemaining,
    DateTimeOffset GeneratedUtc);

public sealed record OperationDto(
    Guid Id,
    Guid AccountId,
    AccountCommand Command,
    OperationState State,
    string? Message,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? CompletedUtc,
    SteamSignInHandoff? Handoff = null);

public sealed record CommandRequest(
    AccountCommand Command,
    object? Payload = null);

public sealed record SteamSignInHandoff(
    Guid OperationId,
    Guid AccountId,
    string OneTimePayload,
    DateTimeOffset ExpiresUtc);

public sealed record SteamSignInPayload(
    string AccountName,
    string SteamId,
    string RefreshToken);
