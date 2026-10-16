using System.Net;
using NfaLoader.Services;

internal sealed record SteamGameOwnershipSnapshot(bool OwnsCs2, DateTimeOffset CheckedUtc);

/// <summary>
/// Checks the signed-in Steam account's license for CS2 (AppID 730). This is intentionally
/// separate from the Game Coordinator check: an account can have no CS2 license and therefore
/// never answer the GC handshake, while Steam itself remains reachable and authenticated.
/// </summary>
internal sealed class SteamGameOwnershipService
{
    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false,
        AllowAutoRedirect = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    public async Task<SteamGameOwnershipSnapshot?> QueryAsync(
        string refreshToken,
        string steamId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var cm = new SteamCmClient(HttpClient);
            await cm.ConnectAndLogOnAsync(refreshToken, steamId, cancellationToken);
            var session = await SteamWebSession.BuildAsync(cm, refreshToken, steamId, cancellationToken);

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "https://store.steampowered.com/account/licenses/?l=english");
            request.Headers.Add("Cookie", session.CookieHeader);
            request.Headers.Add("User-Agent", "CS2AccountHub/1.0");
            using var response = await HttpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            var licensesHtml = await response.Content.ReadAsStringAsync(cancellationToken);
            if (licensesHtml.Length > 8_000_000) return null;

            // The account/licenses page lists only packages owned by the signed-in account.
            var ownsCs2 = licensesHtml.Contains("counter-strike 2", StringComparison.OrdinalIgnoreCase);
            return new SteamGameOwnershipSnapshot(ownsCs2, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (SteamCmException)
        {
            return null;
        }
    }
}
