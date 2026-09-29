internal sealed class ProfileEnrichmentWorker : BackgroundService
{
    private readonly AccountStore _accounts;
    private readonly ISteamAccountGateway _gateway;

    public ProfileEnrichmentWorker(AccountStore accounts, ISteamAccountGateway gateway)
    {
        _accounts = accounts;
        _gateway = gateway;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            await EnrichMissingProfilesAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task EnrichMissingProfilesAsync(CancellationToken cancellationToken)
    {
        foreach (var account in _accounts.List(null, null))
        {
            if (!string.IsNullOrWhiteSpace(account.AvatarUrl) && !string.IsNullOrWhiteSpace(account.PersonaName)) continue;

            var profile = await _gateway.QueryProfileAsync(account.SteamId, cancellationToken);
            if (profile is null) continue;

            var oldName = account.PersonaName;
            var oldAvatar = account.AvatarUrl;
            account.ApplyProfile(profile);
            if (!string.Equals(oldName, account.PersonaName, StringComparison.Ordinal) ||
                !string.Equals(oldAvatar, account.AvatarUrl, StringComparison.Ordinal))
            {
                _accounts.Upsert(account);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }
}
