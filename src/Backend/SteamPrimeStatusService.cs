using System.Net;
using CS2AccountHub.Contracts;
using NfaLoader.Models;
using NfaLoader.Services;

internal sealed class SteamPrimeStatusService
{
    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false,
        AllowAutoRedirect = false,
    }) { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<AccountPrimeBucket> QueryAsync(string refreshToken, string steamId, CsPremierScoreResult status, CancellationToken cancellationToken)
    {
        if (status.HasPremierScore) return AccountPrimeBucket.Prime;

        await using var cm = new SteamCmClient(HttpClient);
        await cm.ConnectAndLogOnAsync(refreshToken, steamId, cancellationToken);
        var session = await SteamWebSession.BuildAsync(cm, refreshToken, steamId, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://store.steampowered.com/app/730/CounterStrike_2/?l=english");
        request.Headers.Add("Cookie", session.CookieHeader);
        request.Headers.Add("User-Agent", "CS2AccountHub/1.0");
        using var response = await HttpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return AccountPrimeBucket.Unknown;

        var html = (await response.Content.ReadAsStringAsync(cancellationToken)).ToLowerInvariant();
        var marker = "prime status upgrade";
        var index = html.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return AccountPrimeBucket.Unknown;

        var start = Math.Max(0, index - 1200);
        var length = Math.Min(html.Length - start, 2400);
        var window = html.Substring(start, length);
        return window.Contains("already owned", StringComparison.Ordinal)
            || window.Contains("package_owned", StringComparison.Ordinal)
            || window.Contains("owned", StringComparison.Ordinal)
            ? AccountPrimeBucket.Prime
            : AccountPrimeBucket.NonPrime;
    }
}
