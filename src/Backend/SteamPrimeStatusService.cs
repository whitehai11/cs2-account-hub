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
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://store.steampowered.com/account/licenses/?l=english");
        request.Headers.Add("Cookie", session.CookieHeader);
        request.Headers.Add("User-Agent", "CS2AccountHub/1.0");
        using var response = await HttpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return AccountPrimeBucket.Unknown;

        var html = (await response.Content.ReadAsStringAsync(cancellationToken)).ToLowerInvariant();
        // The licenses page lists only packages owned by the signed-in account. The public CS2 store page
        // also contains the Prime upsell for Non-Prime accounts, so it cannot be used as the classifier.
        return html.Contains("prime status upgrade", StringComparison.Ordinal)
            ? AccountPrimeBucket.Prime
            : AccountPrimeBucket.NonPrime;
    }
}
