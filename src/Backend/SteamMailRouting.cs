using CS2AccountHub.Contracts;

internal static class SteamMailRouting
{
    private static readonly string[] DefaultPrimarySenders = ["steampowered.com", "steamcommunity.com"];

    public static IReadOnlyList<SteamMailGroupDto> Summarize(IEnumerable<SteamMailRecord> messages)
    {
        var items = messages.ToArray();
        return GetGroups()
            .Select(group => new SteamMailGroupDto(
                group.Id,
                group.Label,
                items.Count(message => message.GroupId == group.Id),
                items.Count(message => message.GroupId == group.Id && !message.IsRead),
                group.Enabled))
            .ToArray();
    }

    public static string? ClassifySender(string sender)
    {
        foreach (var group in GetGroups().Where(group => group.Enabled))
        {
            if (group.Senders.Any(rule => Matches(sender, rule))) return group.Id;
        }

        return null;
    }

    private static IReadOnlyList<MailGroupConfig> GetGroups()
    {
        var primarySenders = ReadSenders("CS2_HUB_IMAP_SENDERS");
        if (primarySenders.Count == 0) primarySenders = DefaultPrimarySenders;

        var secondarySenders = ReadSenders("CS2_HUB_IMAP_SENDERS_SECONDARY");
        return
        [
            new MailGroupConfig("primary", ReadLabel("CS2_HUB_IMAP_PRIMARY_LABEL", "Steam"), primarySenders, true),
            new MailGroupConfig("secondary", ReadLabel("CS2_HUB_IMAP_SECONDARY_LABEL", "Zweite Domain"), secondarySenders, secondarySenders.Count > 0),
        ];
    }

    private static bool Matches(string sender, string rule)
    {
        var normalizedSender = sender.Trim().ToLowerInvariant();
        var normalizedRule = rule.Trim().TrimStart('@').ToLowerInvariant();
        if (normalizedSender == normalizedRule) return true;

        var at = normalizedSender.LastIndexOf('@');
        if (at < 0) return false;
        var domain = normalizedSender[(at + 1)..].TrimEnd('>');
        return domain == normalizedRule || domain.EndsWith("." + normalizedRule, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> ReadSenders(string variable) =>
        (Environment.GetEnvironmentVariable(variable) ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => value.Length is > 0 and <= 320)
            .ToArray();

    private static string ReadLabel(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable)?.Trim();
        return string.IsNullOrWhiteSpace(value) ? fallback : value[..Math.Min(60, value.Length)];
    }

    private sealed record MailGroupConfig(string Id, string Label, IReadOnlyList<string> Senders, bool Enabled);
}
