using System.Net;
using System.Xml.Linq;

internal sealed record SteamProfileSnapshot(string? PersonaName, string? AvatarUrl);

internal sealed class SteamPublicProfileService
{
    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    public async Task<SteamProfileSnapshot?> GetAsync(string steamId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(steamId)) return null;

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://steamcommunity.com/profiles/{Uri.EscapeDataString(steamId)}/?xml=1&l=english");
        request.Headers.UserAgent.ParseAdd("CS2AccountHub/1.0");

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode is >= HttpStatusCode.Ambiguous and < HttpStatusCode.BadRequest || !response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (body.Length > 1_000_000) return null;

        var document = XDocument.Parse(body, LoadOptions.None);
        var personaName = ValueOf(document, "steamID");
        var avatarUrl = ValueOf(document, "avatarFull");
        return new SteamProfileSnapshot(
            string.IsNullOrWhiteSpace(personaName) ? null : personaName.Trim(),
            IsTrustedAvatarUrl(avatarUrl) ? avatarUrl : null);
    }

    private static string? ValueOf(XDocument document, string name) =>
        document.Descendants().FirstOrDefault(element =>
            string.Equals(element.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static bool IsTrustedAvatarUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && (uri.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".akamaihd.net", StringComparison.OrdinalIgnoreCase));
    }
}
