namespace CS2AccountHub.Contracts;

public enum AccountSection
{
    PlayReady = 0,
    NotPlayReady = 1,
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
    public AccountSummaryDto(Guid id, string steamId, string? personaName, string? avatarUrl, AccountPrimeBucket prime, AccountSection section, int? premierRating, int? cs2Level, DateTimeOffset? lastLoginUtc, uint? cooldownSeconds, uint? cooldownReason, bool? vacBanned, string? note, DateTimeOffset? statusUpdatedUtc)
    {
        Id = id; SteamId = steamId; PersonaName = personaName; AvatarUrl = avatarUrl; Prime = prime; Section = section;
        PremierRating = premierRating; Cs2Level = cs2Level; LastLoginUtc = lastLoginUtc; CooldownSeconds = cooldownSeconds;
        CooldownReason = cooldownReason; VacBanned = vacBanned; Note = note; StatusUpdatedUtc = statusUpdatedUtc;
    }

    public Guid Id { get; set; }
    public string SteamId { get; set; }
    public string? PersonaName { get; set; }
    public string? AvatarUrl { get; set; }
    public AccountPrimeBucket Prime { get; set; }
    public AccountSection Section { get; set; }
    public int? PremierRating { get; set; }
    public int? Cs2Level { get; set; }
    public DateTimeOffset? LastLoginUtc { get; set; }
    public uint? CooldownSeconds { get; set; }
    public uint? CooldownReason { get; set; }
    public bool? VacBanned { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset? StatusUpdatedUtc { get; set; }
}

public sealed class AccountDetailDto
{
    public AccountDetailDto(Guid id, string steamId, string? personaName, string? avatarUrl, AccountPrimeBucket prime, AccountSection section, int? premierRating, int? premierWins, int? cs2Level, DateTimeOffset? lastLoginUtc, uint? cooldownSeconds, uint? cooldownReason, bool? vacBanned, string? note, DateTimeOffset? statusUpdatedUtc)
    {
        Id = id; SteamId = steamId; PersonaName = personaName; AvatarUrl = avatarUrl; Prime = prime; Section = section;
        PremierRating = premierRating; PremierWins = premierWins; Cs2Level = cs2Level; LastLoginUtc = lastLoginUtc;
        CooldownSeconds = cooldownSeconds; CooldownReason = cooldownReason; VacBanned = vacBanned; Note = note; StatusUpdatedUtc = statusUpdatedUtc;
    }

    public Guid Id { get; set; }
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
    public uint? CooldownReason { get; set; }
    public bool? VacBanned { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset? StatusUpdatedUtc { get; set; }
}

public sealed record UpdateNoteRequest(string? Note);

public sealed record SetPrimeRequest(AccountPrimeBucket Prime);

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

public sealed record StartAuthRequest(string AccountName, string Password);

public sealed record StartAuthResponse(
    Guid SessionId,
    AuthState State,
    string? SteamId,
    string? PromptMessage);

public sealed record SubmitGuardCodeRequest(string Code);

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
