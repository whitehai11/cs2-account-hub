using System.Security.Cryptography;
using System.Text;
using CS2AccountHub.Contracts;

internal sealed class AccountCheckScheduler : BackgroundService
{
    private readonly AccountStore _accounts;
    private readonly OperationStore _operations;
    private readonly OperationQueue _queue;
    private readonly SteamPublicProfileService _publicProfile;
    private readonly ILogger<AccountCheckScheduler> _logger;
    private readonly TimeSpan _interval;

    public AccountCheckScheduler(AccountStore accounts, OperationStore operations, OperationQueue queue, SteamPublicProfileService publicProfile, ILogger<AccountCheckScheduler> logger)
    {
        _accounts = accounts;
        _operations = operations;
        _queue = queue;
        _publicProfile = publicProfile;
        _logger = logger;
        _interval = ReadInterval();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            do
            {
                await ScheduleDueAccountsAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ScheduleDueAccountsAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var initialSchedules = new Dictionary<Guid, DateTimeOffset>();
        foreach (var account in _accounts.List(null, null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(account.ProtectedRefreshToken)) continue;

            if (account.MonitoringPaused)
            {
                if (account.NextCheckUtc is null || account.NextCheckUtc <= now)
                {
                    account.NextCheckUtc = now.AddMinutes(10);
                    _accounts.Upsert(account);
                }
                continue;
            }

            if (account.NextCheckUtc is null)
            {
                initialSchedules[account.Id] = now.Add(InitialDelay(account.SteamId));
                continue;
            }

            if (account.CooldownEndsUtc is { } cooldownEnd && cooldownEnd > now && cooldownEnd <= now.Add(_interval))
            {
                var finalCheck = cooldownEnd.AddSeconds(-30);
                if (account.NextCheckUtc > finalCheck)
                {
                    account.NextCheckUtc = finalCheck < now ? now : finalCheck;
                    _accounts.Upsert(account);
                }
            }

            if (account.NextCheckUtc > now || _operations.HasActiveForAccount(account.Id)) continue;

            SteamProfileSnapshot? publicProfile;
            try
            {
                publicProfile = await _publicProfile.GetAsync(account.SteamId, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                account.NextCheckUtc = now.AddMinutes(10);
                _accounts.Upsert(account);
                _logger.LogWarning("Skipped scheduled check for account {AccountId}: public Steam presence could not be verified ({ExceptionType}).", account.Id, exception.GetType().Name);
                continue;
            }
            if (publicProfile?.IsPlayingCs2 == true)
            {
                account.PublicPlayingCs2 = true;
                account.NextCheckUtc = now.AddMinutes(10);
                _accounts.Upsert(account);
                _logger.LogInformation("Skipped scheduled check for account {AccountId}: public Steam presence reports CS2.", account.Id);
                continue;
            }
            if (publicProfile?.IsPlayingCs2 == false && account.PublicPlayingCs2 == true)
            {
                account.PublicPlayingCs2 = false;
                _accounts.Upsert(account);
            }
            if (account.InMatch == true)
            {
                account.NextCheckUtc = now.AddMinutes(10);
                _accounts.Upsert(account);
                _logger.LogInformation("Skipped scheduled check for account {AccountId}: last GC status reported an active match.", account.Id);
                continue;
            }

            account.NextCheckUtc = now.Add(_interval).Add(StableJitter(account.SteamId));
            _accounts.Upsert(account);

            var operation = _operations.Create(account.Id, AccountCommand.RefreshStatus, null);
            await _queue.EnqueueAsync(operation.Id);
            _logger.LogInformation("Scheduled staggered status check for account {AccountId}.", account.Id);
        }

        _accounts.SetNextChecks(initialSchedules);
    }

    private TimeSpan InitialDelay(string steamId) =>
        TimeSpan.FromSeconds(Math.Max(30, StableSeconds(steamId) % Math.Max(60, (int)_interval.TotalSeconds)));

    private TimeSpan StableJitter(string steamId) =>
        TimeSpan.FromSeconds((StableSeconds(steamId + ":jitter") % 121) - 60);

    private static int StableSeconds(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return BitConverter.ToInt32(bytes, 0) & int.MaxValue;
    }

    private static TimeSpan ReadInterval()
    {
        var raw = Environment.GetEnvironmentVariable("CS2_HUB_CHECK_INTERVAL_MINUTES");
        return int.TryParse(raw, out var minutes) && minutes is >= 5 and <= 1440
            ? TimeSpan.FromMinutes(minutes)
            : TimeSpan.FromMinutes(30);
    }
}
