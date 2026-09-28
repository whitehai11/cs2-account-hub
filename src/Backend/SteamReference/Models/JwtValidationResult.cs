namespace NfaLoader.Models;

public sealed record JwtValidationResult(string SteamId, DateTimeOffset ExpiresAt);
