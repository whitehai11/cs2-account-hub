namespace NfaLoader.Services;

internal static class FormatHelper
{
    public static string FormatCooldownText(uint? seconds, uint? reason, string unknown) =>
        seconds is null ? unknown : TimeSpan.FromSeconds(seconds.Value).ToString();

    public static string FormatGcVacText(int? banned, string unknown) =>
        banned is null ? unknown : banned == 0 ? "None" : "VAC";

    public static string FormatCooldownStatusText(uint? seconds, uint? reason, int? banned, string unknownCooldown, string unknownVac) =>
        $"Cooldown: {FormatCooldownText(seconds, reason, unknownCooldown)}; VAC: {FormatGcVacText(banned, unknownVac)}";

    public static string FormatPlayerLevelText(int? level, string unknown) => level?.ToString() ?? unknown;
}
